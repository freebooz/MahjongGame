// 玩家资产操作存储：持久化补偿与奖励撤销请求、审批和执行回执。
// 同一幂等键和来源证据不得产生不同资产结果，任何冲突必须显式失败并进入审计。
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using Npgsql;
using NpgsqlTypes;

namespace GuiyangMahjong.Admin.Storage;

/// <summary>
/// 玩家资产操作证据存储。
/// 创建必须按 sourceCommandId 幂等，终态迁移只能从待钱包执行进入完成或拒绝，
/// 该接口不提供任意余额写入能力。
/// </summary>
public interface IPlayerAssetOperationStore
{
    /// <summary>初始化或验证资产操作表结构；失败时服务不得就绪。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>检查资产证据存储是否可读写，不改变玩家资产。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 从已审批动作和补偿案件创建待执行证据；来源命令冲突复用必须失败。
    /// createdAtUtc 由服务端时间源提供。
    /// </summary>
    Task<PlayerAssetOperationCreateResult> CreateAsync(
        string sourceCommandId,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);

    /// <summary>按创建时间倒序列出有限批次，供审计与调查使用。</summary>
    Task<IReadOnlyList<PlayerAssetOperationRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>将指定来源命令迁移到允许的资产终态；重复写入同一终态保持幂等。</summary>
    Task<PlayerAssetOperationRecord> SetStatusAsync(
        string sourceCommandId,
        string status,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单进程测试用玩家资产证据存储。
/// gate 保证幂等检查与创建/迁移不可交错；不持久化且绝不能用于生产经济系统。
/// </summary>
public sealed class InMemoryPlayerAssetOperationStore
    : IPlayerAssetOperationStore
{
    private readonly Dictionary<string, PlayerAssetOperationRecord> operations =
        new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <inheritdoc/>
    public Task<PlayerAssetOperationCreateResult> CreateAsync(
        string sourceCommandId,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (operations.TryGetValue(sourceCommandId, out var existing))
            {
                EnsureSame(existing, operationType, action, compensationCase);
                return Task.FromResult(
                    new PlayerAssetOperationCreateResult(existing, true));
            }
            var created = CreateRecord(
                sourceCommandId,
                operationType,
                action,
                compensationCase,
                createdAtUtc);
            operations.Add(sourceCommandId, created);
            return Task.FromResult(
                new PlayerAssetOperationCreateResult(created, false));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PlayerAssetOperationRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PlayerAssetOperationRecord>>(
                operations.Values
                    .OrderByDescending(item => item.CreatedAtUtc)
                    .Take(limit)
                    .ToArray());
        }
    }

    /// <inheritdoc/>
    public Task<PlayerAssetOperationRecord> SetStatusAsync(
        string sourceCommandId,
        string status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFinalStatus(status);
        lock (gate)
        {
            if (!operations.TryGetValue(sourceCommandId, out var current))
                throw new InvalidOperationException(
                    "Asset operation was not found.");
            if (current.Status != "ApprovedPendingWalletExecution"
                && current.Status != status)
                throw new InvalidOperationException(
                    "Asset operation already has a different terminal status.");
            var updated = current with { Status = status };
            operations[sourceCommandId] = updated;
            return Task.FromResult(updated);
        }
    }

    /// <summary>限制钱包回执只能写入两个受控终态，阻止任意状态污染调查记录。</summary>
    internal static void ValidateFinalStatus(string status)
    {
        if (status is not ("WalletCompleted" or "WalletRejected"))
            throw new InvalidOperationException(
                "Asset operation terminal status is invalid.");
    }

    /// <summary>
    /// 从审批动作和案件生成不可变操作记录；缺少独立审批时失败，
    /// BeforeState 被克隆后保存，避免调用方后续 JSON 生命周期影响记录。
    /// </summary>
    internal static PlayerAssetOperationRecord CreateRecord(
        string sourceCommandId,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase,
        DateTimeOffset createdAtUtc)
    {
        var approval = action.Approval
            ?? throw new InvalidOperationException(
                "An approved action is required for an asset operation.");
        if (compensationCase.CaseType != AdminCaseType.CompensationReview
            || compensationCase.Status != "Open")
        {
            throw new InvalidOperationException(
                "An open compensation review case is required.");
        }
        if (!action.Parameters.HasValue
            || action.Parameters.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Structured asset operation parameters are required.");
        }
        var parameters = action.Parameters.Value;
        var caseId = RequireString(parameters, "caseId");
        if (caseId != compensationCase.CaseId)
        {
            throw new InvalidOperationException(
                "The approved compensation case does not match the action.");
        }
        string? assetCode = null;
        long? amount = null;
        string? rewardGrantId = null;
        if (operationType == PlayerAssetOperationType.GrantCompensation)
        {
            assetCode = RequireString(parameters, "assetCode");
            if (!parameters.TryGetProperty("amount", out var amountElement)
                || !amountElement.TryGetInt64(out var parsedAmount)
                || parsedAmount <= 0)
            {
                throw new InvalidOperationException(
                    "A positive compensation amount is required.");
            }
            amount = parsedAmount;
        }
        else
        {
            rewardGrantId = RequireString(parameters, "rewardGrantId");
        }
        return new PlayerAssetOperationRecord(
            Guid.NewGuid().ToString(),
            sourceCommandId,
            action.ActionRequestId,
            compensationCase.CaseId,
            operationType,
            action.TargetId,
            assetCode,
            amount,
            rewardGrantId,
            action.RequestedBy,
            approval.ApprovedBy,
            createdAtUtc,
            action.Reason,
            action.TicketId,
            action.TraceId,
            action.BeforeState,
            "ApprovedPendingWalletExecution");
    }

    internal static void EnsureSame(
        PlayerAssetOperationRecord existing,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase)
    {
        if (existing.OperationType != operationType
            || existing.ActionRequestId != action.ActionRequestId
            || existing.CaseId != compensationCase.CaseId
            || existing.PlayerId != action.TargetId
            || existing.TraceId != action.TraceId
            || existing.TicketId != action.TicketId)
        {
            throw new InvalidOperationException(
                "Asset command id was reused with different parameters.");
        }
    }

    private static string RequireString(
        JsonElement parameters,
        string propertyName)
    {
        if (!parameters.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException(
                $"Asset operation parameter {propertyName} is required.");
        }
        return element.GetString()!;
    }
}

/// <summary>
/// PostgreSQL 玩家资产操作证据存储。
/// sourceCommandId 唯一约束防止重复经济操作，终态更新受允许前置状态限制；
/// 该实例拥有数据源生命周期但不直接持有玩家钱包余额。
/// </summary>
public sealed class PostgresPlayerAssetOperationStore(NpgsqlDataSource postgres)
    : IPlayerAssetOperationStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var path = AdminStoragePaths.SchemaPath;
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
    public async Task<PlayerAssetOperationCreateResult> CreateAsync(
        string sourceCommandId,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var proposed = InMemoryPlayerAssetOperationStore.CreateRecord(
            sourceCommandId,
            operationType,
            action,
            compensationCase,
            createdAtUtc);
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (var commandLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))",
            connection,
            transaction))
        {
            commandLock.Parameters.AddWithValue(sourceCommandId);
            await commandLock.ExecuteNonQueryAsync(cancellationToken);
        }
        var existing = await GetByCommandAsync(
            connection,
            transaction,
            sourceCommandId,
            cancellationToken);
        if (existing is not null)
        {
            InMemoryPlayerAssetOperationStore.EnsureSame(
                existing,
                operationType,
                action,
                compensationCase);
            await transaction.CommitAsync(cancellationToken);
            return new PlayerAssetOperationCreateResult(existing, true);
        }
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.player_asset_operations(
                operation_id, source_command_id, action_request_id, case_id,
                operation_type, player_id, asset_code, amount, reward_grant_id,
                requested_by, approved_by, created_at_utc, reason, ticket_id,
                trace_id, before_state, status)
            VALUES (
                $1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,
                'ApprovedPendingWalletExecution')
            """,
            connection,
            transaction);
        AddParameters(insert, proposed);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PlayerAssetOperationCreateResult(proposed, false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PlayerAssetOperationRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            $"""
            {SelectSql}
            ORDER BY created_at_utc DESC
            LIMIT $1
            """);
        command.Parameters.AddWithValue(limit);
        var result = new List<PlayerAssetOperationRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(Read(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task<PlayerAssetOperationRecord> SetStatusAsync(
        string sourceCommandId,
        string status,
        CancellationToken cancellationToken)
    {
        InMemoryPlayerAssetOperationStore.ValidateFinalStatus(status);
        await using var command = postgres.CreateCommand(
            $"""
            UPDATE admin_monitor.player_asset_operations
            SET status=$1
            WHERE source_command_id=$2
              AND status IN ('ApprovedPendingWalletExecution', $1)
            RETURNING operation_id, source_command_id, action_request_id,
                      case_id, operation_type, player_id, asset_code, amount,
                      reward_grant_id, requested_by, approved_by,
                      created_at_utc, reason, ticket_id, trace_id,
                      before_state::text, status
            """);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(Guid.Parse(sourceCommandId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            return Read(reader);
        await reader.DisposeAsync();
        await using var lookup = postgres.CreateCommand(
            $"{SelectSql} WHERE source_command_id=$1");
        lookup.Parameters.AddWithValue(Guid.Parse(sourceCommandId));
        await using var lookupReader =
            await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await lookupReader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                "Asset operation was not found.");
        throw new InvalidOperationException(
            "Asset operation already has a different terminal status.");
    }

    private static async Task<PlayerAssetOperationRecord?> GetByCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceCommandId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"{SelectSql} WHERE source_command_id=$1",
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(sourceCommandId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static void AddParameters(
        NpgsqlCommand command,
        PlayerAssetOperationRecord record)
    {
        command.Parameters.AddWithValue(Guid.Parse(record.OperationId));
        command.Parameters.AddWithValue(Guid.Parse(record.SourceCommandId));
        command.Parameters.AddWithValue(Guid.Parse(record.ActionRequestId));
        command.Parameters.AddWithValue(Guid.Parse(record.CaseId));
        command.Parameters.AddWithValue(record.OperationType.ToString());
        command.Parameters.AddWithValue(record.PlayerId);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Varchar,
            Value = (object?)record.AssetCode ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bigint,
            Value = (object?)record.Amount ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Varchar,
            Value = (object?)record.RewardGrantId ?? DBNull.Value
        });
        command.Parameters.AddWithValue(record.RequestedBy);
        command.Parameters.AddWithValue(record.ApprovedBy);
        command.Parameters.AddWithValue(record.CreatedAtUtc);
        command.Parameters.AddWithValue(record.Reason);
        command.Parameters.AddWithValue(record.TicketId);
        command.Parameters.AddWithValue(record.TraceId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(record.BeforeState, JsonOptions));
    }

    private static PlayerAssetOperationRecord Read(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0).ToString(),
            reader.GetGuid(1).ToString(),
            reader.GetGuid(2).ToString(),
            reader.GetGuid(3).ToString(),
            Enum.Parse<PlayerAssetOperationType>(reader.GetString(4)),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(15)),
            reader.GetString(16));

    private const string SelectColumns =
        """
        SELECT operation_id, source_command_id, action_request_id, case_id,
               operation_type, player_id, asset_code, amount, reward_grant_id,
               requested_by, approved_by, created_at_utc, reason, ticket_id,
               trace_id, before_state::text, status
        """;

    private const string SelectSql =
        $"""
        {SelectColumns}
        FROM admin_monitor.player_asset_operations
        """;

    /// <summary>异步释放该存储独占的 PostgreSQL 数据源和连接池。</summary>
    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
