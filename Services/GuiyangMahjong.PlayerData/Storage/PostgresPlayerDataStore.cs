// PostgreSQL PlayerData 存储：以数据库事务保证资产余额、流水、奖励和证据投影原子一致。
// SQL 使用参数化查询与最小权限身份；重复幂等键只能重放同一结果，冲突载荷必须拒绝。
using System.Text.Json;
using GuiyangMahjong.PlayerData.Domain;
using Npgsql;
using NpgsqlTypes;

namespace GuiyangMahjong.PlayerData.Storage;

/// <summary>
/// PostgreSQL PlayerData 生产存储。
/// 奖励领取、余额版本、资产证据及投影 Outbox 在同一事务提交，
/// 行锁和唯一键保证多副本幂等且余额不会被并发覆盖；该实例拥有数据源生命周期。
/// </summary>
public sealed class PostgresPlayerDataStore(NpgsqlDataSource postgres)
    : IPlayerDataStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var path = PlayerDataStoragePaths.SchemaPath;
        await using var command = postgres.CreateCommand(
            await File.ReadAllTextAsync(path, cancellationToken));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = postgres.CreateCommand("SELECT 1");
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<EvidenceRecordResult> RecordEvidenceAsync(
        RecordEvidenceRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var result = await InsertEvidenceAsync(
            connection,
            transaction,
            request,
            recordedAtUtc,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc/>
    public async Task<EvidenceRecordResult> RecordRewardClaimAsync(
        RewardClaimRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var now =
            InMemoryPlayerDataStore.NormalizeTimestamp(recordedAtUtc);
        var assetCode = request.AssetCode.ToUpperInvariant();
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(
            connection,
            transaction,
            request.EventId,
            cancellationToken);
        var existingEvidence = await GetEvidenceAsync(
            connection,
            transaction,
            request.EventId,
            cancellationToken);
        if (existingEvidence is not null)
        {
            if (existingEvidence.EvidenceType !=
                    PlayerEvidenceType.RewardClaim
                || existingEvidence.PlayerId != request.PlayerId
                || existingEvidence.SourceReference !=
                    request.SourceReference)
            {
                throw PlayerDataOperationException.Conflict(
                    "Reward event id was reused with different parameters.");
            }
            await transaction.CommitAsync(cancellationToken);
            return new EvidenceRecordResult(request.EventId, true);
        }

        await LockAsync(
            connection,
            transaction,
            $"reward:{request.RewardGrantId}",
            cancellationToken);
        bool? existingRewardMatches = null;
        await using (var rewardLookup = new NpgsqlCommand(
            """
            SELECT player_id, asset_code, amount
            FROM player_data.reward_grants
            WHERE reward_grant_id=$1
            """,
            connection,
            transaction))
        {
            rewardLookup.Parameters.AddWithValue(request.RewardGrantId);
            await using var reader =
                await rewardLookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingRewardMatches =
                    reader.GetString(0) == request.PlayerId
                    && reader.GetString(1) == assetCode
                    && reader.GetInt64(2) == request.Amount;
            }
        }
        if (existingRewardMatches.HasValue)
        {
            if (!existingRewardMatches.Value)
            {
                throw PlayerDataOperationException.Conflict(
                    "Reward grant id was reused with different parameters.");
            }
            await transaction.CommitAsync(cancellationToken);
            return new EvidenceRecordResult(request.EventId, true);
        }

        await using (var insertReward = new NpgsqlCommand(
            """
            INSERT INTO player_data.reward_grants(
                reward_grant_id, player_id, asset_code, amount,
                status, claimed_at_utc)
            VALUES ($1,$2,$3,$4,'Claimed',$5)
            """,
            connection,
            transaction))
        {
            insertReward.Parameters.AddWithValue(request.RewardGrantId);
            insertReward.Parameters.AddWithValue(request.PlayerId);
            insertReward.Parameters.AddWithValue(assetCode);
            insertReward.Parameters.AddWithValue(request.Amount);
            insertReward.Parameters.AddWithValue(
                InMemoryPlayerDataStore.NormalizeTimestamp(
                    request.OccurredAtUtc));
            await insertReward.ExecuteNonQueryAsync(cancellationToken);
        }
        var balance = await AddBalanceAsync(
            connection,
            transaction,
            request.PlayerId,
            assetCode,
            request.Amount,
            now,
            cancellationToken);
        await InsertEvidenceAsync(
            connection,
            transaction,
            new RecordEvidenceRequest(
                request.EventId,
                request.PlayerId,
                PlayerEvidenceType.RewardClaim,
                request.OccurredAtUtc,
                request.SourceReference,
                JsonSerializer.SerializeToElement(new
                {
                    request.RewardGrantId,
                    assetCode,
                    request.Amount,
                    status = "Claimed",
                    request.TraceId
                }),
                PlayerEvidenceSensitivity.Financial),
            now,
            cancellationToken);
        await InsertEvidenceAsync(
            connection,
            transaction,
            new RecordEvidenceRequest(
                InMemoryPlayerDataStore.CreateDerivedId(
                    request.EventId,
                    "asset-change"),
                request.PlayerId,
                PlayerEvidenceType.AssetChange,
                request.OccurredAtUtc,
                $"reward-asset:{request.RewardGrantId}",
                JsonSerializer.SerializeToElement(new
                {
                    transactionType = "RewardClaim",
                    request.RewardGrantId,
                    assetCode,
                    amount = request.Amount,
                    balanceAfter = balance.Balance,
                    balanceVersion = balance.Version,
                    request.TraceId
                }),
                PlayerEvidenceSensitivity.Financial),
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EvidenceRecordResult(request.EventId, false);
    }

    /// <inheritdoc/>
    public async Task<WalletOperationResult> ApplyWalletOperationAsync(
        string commandId,
        AdminWalletOperationRequest request,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        var now =
            InMemoryPlayerDataStore.NormalizeTimestamp(completedAtUtc);
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(
            connection,
            transaction,
            commandId,
            cancellationToken);
        var existing = await GetWalletOperationAsync(
            connection,
            transaction,
            commandId,
            cancellationToken);
        if (existing is not null)
        {
            InMemoryPlayerDataStore.EnsureSame(
                existing.Value.Request,
                request);
            await transaction.CommitAsync(cancellationToken);
            return existing.Value.Result with { Duplicate = true };
        }
        if (request.RequestedBy == request.ApprovedBy)
            throw PlayerDataOperationException.Conflict(
                "Wallet operations require a separate approver.");

        string assetCode;
        long amount;
        if (request.OperationType == "GrantCompensation")
        {
            assetCode = request.AssetCode?.ToUpperInvariant()
                ?? throw PlayerDataOperationException.Invalid(
                    "assetCode is required for compensation.");
            amount = request.Amount
                ?? throw PlayerDataOperationException.Invalid(
                    "amount is required for compensation.");
            if (amount <= 0)
                throw PlayerDataOperationException.Invalid(
                    "Compensation amount must be positive.");
        }
        else if (request.OperationType == "RevokeReward")
        {
            var rewardId = request.RewardGrantId
                ?? throw PlayerDataOperationException.Invalid(
                    "rewardGrantId is required for reward reversal.");
            await using var rewardCommand = new NpgsqlCommand(
                """
                SELECT player_id, asset_code, amount, status
                FROM player_data.reward_grants
                WHERE reward_grant_id=$1
                FOR UPDATE
                """,
                connection,
                transaction);
            rewardCommand.Parameters.AddWithValue(rewardId);
            await using var rewardReader =
                await rewardCommand.ExecuteReaderAsync(cancellationToken);
            if (!await rewardReader.ReadAsync(cancellationToken)
                || rewardReader.GetString(0) != request.PlayerId
                || rewardReader.GetString(3) != "Claimed")
            {
                throw PlayerDataOperationException.Conflict(
                    "The referenced claimed reward was not found.");
            }
            assetCode = rewardReader.GetString(1);
            amount = -rewardReader.GetInt64(2);
            await rewardReader.DisposeAsync();
            var balanceBefore = await GetBalanceAsync(
                connection,
                transaction,
                request.PlayerId,
                assetCode,
                cancellationToken);
            if (balanceBefore.Balance + amount < 0)
                throw PlayerDataOperationException.Conflict(
                    "Reward reversal would make the authoritative balance negative.");
            await using var revoke = new NpgsqlCommand(
                """
                UPDATE player_data.reward_grants
                SET status='Revoked', revoked_at_utc=$1
                WHERE reward_grant_id=$2 AND status='Claimed'
                """,
                connection,
                transaction);
            revoke.Parameters.AddWithValue(now);
            revoke.Parameters.AddWithValue(rewardId);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            throw PlayerDataOperationException.Invalid(
                "Wallet operation type is invalid.");
        }

        var balance = await AddBalanceAsync(
            connection,
            transaction,
            request.PlayerId,
            assetCode,
            amount,
            now,
            cancellationToken);
        var result = new WalletOperationResult(
            commandId,
            Guid.NewGuid().ToString(),
            request.OperationType,
            request.PlayerId,
            assetCode,
            amount,
            balance.Balance,
            balance.Version,
            "Completed",
            false,
            now);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO player_data.wallet_transactions(
                transaction_id, command_id, operation_type, player_id,
                asset_code, amount, balance_after, balance_version,
                request_data, case_id, requested_by, approved_by, reason,
                ticket_id, trace_id, completed_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue(Guid.Parse(result.TransactionId));
            insert.Parameters.AddWithValue(Guid.Parse(commandId));
            insert.Parameters.AddWithValue(request.OperationType);
            insert.Parameters.AddWithValue(request.PlayerId);
            insert.Parameters.AddWithValue(assetCode);
            insert.Parameters.AddWithValue(amount);
            insert.Parameters.AddWithValue(balance.Balance);
            insert.Parameters.AddWithValue(balance.Version);
            AddJson(insert, JsonSerializer.SerializeToElement(request));
            insert.Parameters.AddWithValue(Guid.Parse(request.CaseId));
            insert.Parameters.AddWithValue(request.RequestedBy);
            insert.Parameters.AddWithValue(request.ApprovedBy);
            insert.Parameters.AddWithValue(request.Reason);
            insert.Parameters.AddWithValue(request.TicketId);
            insert.Parameters.AddWithValue(request.TraceId);
            insert.Parameters.AddWithValue(now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertEvidenceAsync(
            connection,
            transaction,
            new RecordEvidenceRequest(
                InMemoryPlayerDataStore.CreateDerivedId(
                    commandId,
                    "asset-change"),
                request.PlayerId,
                PlayerEvidenceType.AssetChange,
                now,
                $"admin-wallet:{commandId}",
                JsonSerializer.SerializeToElement(new
                {
                    result.TransactionId,
                    result.OperationType,
                    result.AssetCode,
                    result.Amount,
                    result.BalanceAfter,
                    result.BalanceVersion,
                    request.CaseId,
                    request.TicketId,
                    request.TraceId
                }),
                PlayerEvidenceSensitivity.Financial),
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(
        string playerId,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            SELECT player_id, asset_code, balance, version, updated_at_utc
            FROM player_data.wallet_balances
            WHERE player_id=$1
            ORDER BY asset_code
            """);
        command.Parameters.AddWithValue(playerId);
        var result = new List<WalletBalance>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadBalance(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProjectionOutboxRecord>>
        ClaimProjectionsAsync(
            string workerId,
            int limit,
            DateTimeOffset now,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken)
    {
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            WITH candidates AS (
                SELECT event_id
                FROM player_data.projection_outbox
                WHERE (status='Pending' AND available_at_utc <= $1)
                   OR (status='Processing' AND lease_expires_at_utc <= $1)
                ORDER BY available_at_utc
                FOR UPDATE SKIP LOCKED
                LIMIT $2
            )
            UPDATE player_data.projection_outbox AS item
            SET status='Processing',
                attempt_count=item.attempt_count+1,
                lock_owner=$3,
                lease_expires_at_utc=$4
            FROM candidates
            WHERE item.event_id=candidates.event_id
            RETURNING item.event_id, item.payload::text, item.status,
                      item.attempt_count, item.available_at_utc,
                      item.lock_owner, item.lease_expires_at_utc,
                      item.last_error
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(leaseExpiresAtUtc);
        var result = new List<ProjectionOutboxRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadProjection(reader));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc/>
    public async Task CompleteProjectionAsync(
        string eventId,
        string workerId,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            UPDATE player_data.projection_outbox
            SET status='Completed', lock_owner=NULL,
                lease_expires_at_utc=NULL, last_error=NULL
            WHERE event_id=$1 AND status='Processing' AND lock_owner=$2
            """);
        command.Parameters.AddWithValue(Guid.Parse(eventId));
        command.Parameters.AddWithValue(workerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task FailProjectionAsync(
        string eventId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            UPDATE player_data.projection_outbox
            SET status=$1, available_at_utc=$2, lock_owner=NULL,
                lease_expires_at_utc=NULL, last_error=$3
            WHERE event_id=$4 AND status='Processing' AND lock_owner=$5
            """);
        command.Parameters.AddWithValue(terminal ? "Failed" : "Pending");
        command.Parameters.AddWithValue(availableAtUtc);
        command.Parameters.AddWithValue(
            error.Length > 1000 ? error[..1000] : error);
        command.Parameters.AddWithValue(Guid.Parse(eventId));
        command.Parameters.AddWithValue(workerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<EvidenceRecordResult> InsertEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecordEvidenceRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        await LockAsync(
            connection,
            transaction,
            request.EventId,
            cancellationToken);
        await LockAsync(
            connection,
            transaction,
            $"{request.EvidenceType}:{request.SourceReference}",
            cancellationToken);
        var proposed = new InMemoryPlayerDataStore.StoredEvidence(
            request.EventId,
            request.PlayerId,
            request.EvidenceType,
            InMemoryPlayerDataStore.NormalizeTimestamp(
                request.OccurredAtUtc),
            request.SourceReference,
            request.Data.Clone(),
            request.Sensitivity,
            InMemoryPlayerDataStore.NormalizeTimestamp(recordedAtUtc));
        var existing = await GetEvidenceAsync(
            connection,
            transaction,
            request.EventId,
            cancellationToken);
        if (existing is not null)
        {
            InMemoryPlayerDataStore.EnsureSame(existing, proposed);
            return new EvidenceRecordResult(existing.EventId, true);
        }
        await using (var sourceLookup = new NpgsqlCommand(
            """
            SELECT event_id
            FROM player_data.evidence_events
            WHERE evidence_type=$1 AND source_reference=$2
            """,
            connection,
            transaction))
        {
            sourceLookup.Parameters.AddWithValue(
                request.EvidenceType.ToString());
            sourceLookup.Parameters.AddWithValue(request.SourceReference);
            if (await sourceLookup.ExecuteScalarAsync(cancellationToken)
                is not null)
            {
                throw PlayerDataOperationException.Conflict(
                    "Evidence source reference was already recorded.");
            }
        }
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO player_data.evidence_events(
                event_id, player_id, evidence_type, occurred_at_utc,
                source_reference, data, sensitivity, recorded_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue(Guid.Parse(proposed.EventId));
            insert.Parameters.AddWithValue(proposed.PlayerId);
            insert.Parameters.AddWithValue(
                proposed.EvidenceType.ToString());
            insert.Parameters.AddWithValue(proposed.OccurredAtUtc);
            insert.Parameters.AddWithValue(proposed.SourceReference);
            AddJson(insert, proposed.Data);
            insert.Parameters.AddWithValue(
                proposed.Sensitivity.ToString());
            insert.Parameters.AddWithValue(proposed.RecordedAtUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var outbox = new NpgsqlCommand(
            """
            INSERT INTO player_data.projection_outbox(
                event_id, payload, status, attempt_count, available_at_utc)
            VALUES ($1,$2,'Pending',0,$3)
            """,
            connection,
            transaction))
        {
            outbox.Parameters.AddWithValue(Guid.Parse(proposed.EventId));
            AddJson(
                outbox,
                InMemoryPlayerDataStore.ToProjectionPayload(proposed));
            outbox.Parameters.AddWithValue(proposed.RecordedAtUtc);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        return new EvidenceRecordResult(proposed.EventId, false);
    }

    private static async Task<InMemoryPlayerDataStore.StoredEvidence?>
        GetEvidenceAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string eventId,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, player_id, evidence_type, occurred_at_utc,
                   source_reference, data::text, sensitivity, recorded_at_utc
            FROM player_data.evidence_events
            WHERE event_id=$1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(eventId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new InMemoryPlayerDataStore.StoredEvidence(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                Enum.Parse<PlayerEvidenceType>(reader.GetString(2)),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4),
                JsonDocument.Parse(reader.GetString(5))
                    .RootElement.Clone(),
                Enum.Parse<PlayerEvidenceSensitivity>(
                    reader.GetString(6)),
                reader.GetFieldValue<DateTimeOffset>(7))
            : null;
    }

    private static async Task<(
        AdminWalletOperationRequest Request,
        WalletOperationResult Result)?> GetWalletOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT transaction_id, operation_type, player_id, asset_code,
                   amount, balance_after, balance_version, request_data::text,
                   completed_at_utc
            FROM player_data.wallet_transactions
            WHERE command_id=$1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(commandId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var request = JsonSerializer.Deserialize<AdminWalletOperationRequest>(
            reader.GetString(7),
            JsonOptions)
            ?? throw new InvalidOperationException(
                "Persisted wallet request is invalid.");
        return (
            request,
            new WalletOperationResult(
                commandId,
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                "Completed",
                false,
                reader.GetFieldValue<DateTimeOffset>(8)));
    }

    private static async Task<WalletBalance> AddBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string playerId,
        string assetCode,
        long delta,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO player_data.wallet_balances(
                player_id, asset_code, balance, version, updated_at_utc)
            VALUES ($1,$2,$3,1,$4)
            ON CONFLICT (player_id, asset_code) DO UPDATE
            SET balance=player_data.wallet_balances.balance + EXCLUDED.balance,
                version=player_data.wallet_balances.version + 1,
                updated_at_utc=EXCLUDED.updated_at_utc
            WHERE player_data.wallet_balances.balance + EXCLUDED.balance >= 0
            RETURNING player_id, asset_code, balance, version, updated_at_utc
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(assetCode);
        command.Parameters.AddWithValue(delta);
        command.Parameters.AddWithValue(now);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw PlayerDataOperationException.Conflict(
                "Wallet balance cannot become negative.");
        return ReadBalance(reader);
    }

    private static async Task<WalletBalance> GetBalanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string playerId,
        string assetCode,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT player_id, asset_code, balance, version, updated_at_utc
            FROM player_data.wallet_balances
            WHERE player_id=$1 AND asset_code=$2
            FOR UPDATE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(assetCode);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadBalance(reader)
            : new WalletBalance(
                playerId,
                assetCode,
                0,
                0,
                DateTimeOffset.UnixEpoch);
    }

    private static WalletBalance ReadBalance(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetFieldValue<DateTimeOffset>(4));

    private static ProjectionOutboxRecord ReadProjection(
        NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0).ToString(),
            JsonDocument.Parse(reader.GetString(1)).RootElement.Clone(),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

    private static async Task LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue(key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddJson(NpgsqlCommand command, JsonElement value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value.GetRawText()
        });

    /// <summary>异步释放该存储独占的 PostgreSQL 数据源和连接池。</summary>
    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
