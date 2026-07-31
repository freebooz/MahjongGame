using System.Text.Json;
using GuiyangMahjong.Economy.Domain;
using GuiyangMahjong.Schema;
using Npgsql;

namespace GuiyangMahjong.Economy.Storage;

/// <summary>PostgreSQL 权威实现；使用行锁和唯一约束保证并发幂等，不依赖 Redis 锁。</summary>
public sealed class PostgresEconomyStore(NpgsqlDataSource dataSource, bool applyMigrations) : IEconomyStore
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!applyMigrations) return;
        var path = ServiceSchemaPath.Resolve(typeof(PostgresEconomyStore).Assembly);
        await using var command = dataSource.CreateCommand(await File.ReadAllTextAsync(path, cancellationToken));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try { await using var command = dataSource.CreateCommand("SELECT 1");
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1; }
        catch (NpgsqlException) { return false; }
    }

    public async Task<RewardClaimResult> ClaimRewardAsync(RewardClaimRequest request, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 分别锁定奖励 ID 和来源事件；即使两个并发请求只复用其中一个标识，也必须串行核对载荷。
        foreach (var identity in new[] { $"event:{request.EventId}", $"grant:{request.RewardGrantId}" }.Order(StringComparer.Ordinal))
        {
            await using var identityLock = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@identity, 0))", connection, transaction);
            identityLock.Parameters.AddWithValue("identity", identity);
            await identityLock.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var existing = new NpgsqlCommand("""
            SELECT reward_grant_id, source_event_id::text, player_id, asset_code, amount, source_reference
            FROM reward.reward_grants WHERE reward_grant_id=@grant OR source_event_id=@event
            FOR UPDATE
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("grant", request.RewardGrantId);
            existing.Parameters.AddWithValue("event", Guid.Parse(request.EventId));
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var same = reader.GetString(0) == request.RewardGrantId && reader.GetString(1) == Guid.Parse(request.EventId).ToString()
                    && reader.GetString(2) == request.PlayerId && reader.GetString(3) == request.AssetCode
                    && reader.GetInt64(4) == request.Amount && reader.GetString(5) == request.SourceReference;
                if (!same) throw Conflict("Reward idempotency identity was reused with another payload.");
                await transaction.CommitAsync(cancellationToken);
                return new RewardClaimResult(request.EventId, true);
            }
        }
        var balance = await ApplyDeltaAsync(connection, transaction, request.PlayerId, request.AssetCode,
            request.Amount, now, cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO reward.reward_grants(reward_grant_id,source_event_id,source_reference,player_id,
                asset_code,amount,status,trace_id,claimed_at_utc)
            VALUES(@grant,@event,@source,@player,@asset,@amount,'Claimed',@trace,@claimed)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("grant", request.RewardGrantId);
            insert.Parameters.AddWithValue("event", Guid.Parse(request.EventId));
            insert.Parameters.AddWithValue("source", request.SourceReference);
            insert.Parameters.AddWithValue("player", request.PlayerId);
            insert.Parameters.AddWithValue("asset", request.AssetCode);
            insert.Parameters.AddWithValue("amount", request.Amount);
            insert.Parameters.AddWithValue("trace", request.TraceId);
            insert.Parameters.AddWithValue("claimed", request.OccurredAtUtc);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await WriteOutboxAsync(connection, transaction, "RewardClaimed", request, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _ = balance; // 余额写入与奖励同事务，回执保持旧接口的精简结构。
        return new RewardClaimResult(request.EventId, false);
    }

    public async Task<WalletOperationResult> ApplyWalletOperationAsync(Guid commandId,
        AdminWalletOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var existing = new NpgsqlCommand("""
            SELECT request_data::text, transaction_id::text, operation_type, player_id, asset_code, amount,
                   balance_after, balance_version, completed_at_utc
            FROM inventory.wallet_transactions WHERE command_id=@command FOR UPDATE
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("command", commandId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var stored = JsonSerializer.Deserialize<AdminWalletOperationRequest>(reader.GetString(0));
                if (stored != request) throw Conflict("Idempotency key was reused with another payload.");
                var duplicate = new WalletOperationResult(commandId.ToString(), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                    "Completed", true, reader.GetFieldValue<DateTimeOffset>(8));
                await transaction.CommitAsync(cancellationToken);
                return duplicate;
            }
        }
        string asset; long delta;
        if (request.OperationType == "GrantCompensation") { asset = request.AssetCode!; delta = request.Amount!.Value; }
        else
        {
            await using var reward = new NpgsqlCommand("""
                SELECT player_id, asset_code, amount, status FROM reward.reward_grants
                WHERE reward_grant_id=@grant FOR UPDATE
                """, connection, transaction);
            reward.Parameters.AddWithValue("grant", request.RewardGrantId!);
            await using var reader = await reward.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new EconomyOperationException("REWARD_NOT_FOUND", "Reward grant was not found.", 404);
            if (reader.GetString(0) != request.PlayerId) throw Conflict("Reward does not belong to player.");
            if (reader.GetString(3) != "Claimed") throw Conflict("Reward was already revoked.");
            asset = reader.GetString(1); delta = -reader.GetInt64(2);
            await reader.DisposeAsync();
            await using var revoke = new NpgsqlCommand("UPDATE reward.reward_grants SET status='Revoked', revoked_at_utc=@now WHERE reward_grant_id=@grant", connection, transaction);
            revoke.Parameters.AddWithValue("now", now); revoke.Parameters.AddWithValue("grant", request.RewardGrantId!);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }
        var balance = await ApplyDeltaAsync(connection, transaction, request.PlayerId, asset, delta, now, cancellationToken);
        var transactionId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO inventory.wallet_transactions(transaction_id,command_id,operation_type,player_id,asset_code,
                amount,balance_after,balance_version,request_data,case_id,requested_by,approved_by,reason,ticket_id,trace_id,completed_at_utc)
            VALUES(@id,@command,@type,@player,@asset,@amount,@after,@version,@request::jsonb,@case,@requested,@approved,@reason,@ticket,@trace,@now)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", transactionId); insert.Parameters.AddWithValue("command", commandId);
            insert.Parameters.AddWithValue("type", request.OperationType); insert.Parameters.AddWithValue("player", request.PlayerId);
            insert.Parameters.AddWithValue("asset", asset); insert.Parameters.AddWithValue("amount", delta);
            insert.Parameters.AddWithValue("after", balance.Balance); insert.Parameters.AddWithValue("version", balance.Version);
            insert.Parameters.AddWithValue("request", JsonSerializer.Serialize(request)); insert.Parameters.AddWithValue("case", Guid.Parse(request.CaseId));
            insert.Parameters.AddWithValue("requested", request.RequestedBy); insert.Parameters.AddWithValue("approved", request.ApprovedBy);
            insert.Parameters.AddWithValue("reason", request.Reason); insert.Parameters.AddWithValue("ticket", request.TicketId);
            insert.Parameters.AddWithValue("trace", request.TraceId); insert.Parameters.AddWithValue("now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var result = new WalletOperationResult(commandId.ToString(), transactionId.ToString(), request.OperationType,
            request.PlayerId, asset, delta, balance.Balance, balance.Version, "Completed", false, now);
        await WriteOutboxAsync(connection, transaction, "WalletChanged", result, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(string playerId, CancellationToken cancellationToken)
    {
        var values = new List<WalletBalance>();
        await using var command = dataSource.CreateCommand("SELECT player_id,asset_code,balance,version,updated_at_utc FROM inventory.wallet_balances WHERE player_id=@player ORDER BY asset_code");
        command.Parameters.AddWithValue("player", playerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(new(reader.GetString(0), reader.GetString(1),
            reader.GetInt64(2), reader.GetInt64(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return values;
    }

    private static async Task<WalletBalance> ApplyDeltaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string player, string asset, long delta, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO inventory.wallet_balances(player_id,asset_code,balance,version,updated_at_utc)
            VALUES(@player,@asset,@delta,1,@now)
            ON CONFLICT(player_id,asset_code) DO UPDATE SET
              balance=inventory.wallet_balances.balance+EXCLUDED.balance,
              version=inventory.wallet_balances.version+1, updated_at_utc=EXCLUDED.updated_at_utc
            WHERE inventory.wallet_balances.balance+EXCLUDED.balance>=0
            RETURNING balance,version
            """, connection, transaction);
        command.Parameters.AddWithValue("player", player); command.Parameters.AddWithValue("asset", asset);
        command.Parameters.AddWithValue("delta", delta); command.Parameters.AddWithValue("now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw Conflict("Wallet balance cannot become negative.");
        return new WalletBalance(player, asset, reader.GetInt64(0), reader.GetInt64(1), now);
    }

    private static async Task WriteOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string type, object payload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO economy_integration.platform_outbox(event_id,event_type,schema_version,payload,occurred_at_utc,available_at_utc) VALUES(@id,@type,1,@payload::jsonb,@now,@now)", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(payload)); command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static EconomyOperationException Conflict(string message) => new("ECONOMY_CONFLICT", message, 409);
}
