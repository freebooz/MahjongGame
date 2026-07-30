using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using Npgsql;
using NpgsqlTypes;

namespace GuiyangMahjong.Admin.Storage;

/// <summary>
/// 争议调查、客服和补偿案件的持久化边界。
/// sourceCommandId 保证由同一管理命令只创建一个案件，关闭只能从 Open 单向迁移，
/// 结案人、结论和证据包哈希必须作为不可覆盖的调查事实保存。
/// </summary>
public interface IAdminCaseStore
{
    /// <summary>初始化或验证案件表结构；失败时 Admin 不得进入就绪状态。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>检查案件存储的读写可用性，不创建或关闭案件。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 从已审批管理动作创建案件；相同来源命令仅允许相同案件类型和目标的幂等重放。
    /// createdAtUtc 由服务端时间源生成。
    /// </summary>
    Task<AdminCaseCreateResult> CreateAsync(
        string sourceCommandId,
        AdminCaseType caseType,
        AdminActionRecord action,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);

    /// <summary>按案件 UUID 读取当前记录；不存在或标识无效时返回空。</summary>
    Task<AdminCaseRecord?> GetAsync(
        string caseId,
        CancellationToken cancellationToken);

    /// <summary>按创建时间倒序读取有界案件列表，调用方负责 RBAC 与字段脱敏。</summary>
    Task<IReadOnlyList<AdminCaseRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken);
    /// <summary>仅将 Open 案件单向关闭；已关闭案件不会被重新打开或覆盖结论。</summary>
    Task<AdminCaseRecord?> CloseAsync(
        string caseId,
        string closedBy,
        string resolution,
        string evidencePackageHash,
        DateTimeOffset closedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单进程开发/测试用案件存储。
/// gate 把幂等检查、创建和结案更新组成原子临界区；数据不持久化，禁止用于生产调查。
/// </summary>
public sealed class InMemoryAdminCaseStore : IAdminCaseStore
{
    // 字典以 sourceCommandId 为键，确保一个执行命令只能派生一个案件。
    private readonly Dictionary<string, AdminCaseRecord> cases =
        new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <inheritdoc/>
    public Task<AdminCaseCreateResult> CreateAsync(
        string sourceCommandId,
        AdminCaseType caseType,
        AdminActionRecord action,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (cases.TryGetValue(sourceCommandId, out var existing))
            {
                EnsureSame(existing, caseType, action);
                return Task.FromResult(new AdminCaseCreateResult(existing, true));
            }
            var created = CreateRecord(
                sourceCommandId,
                caseType,
                action,
                createdAtUtc);
            cases.Add(sourceCommandId, created);
            return Task.FromResult(new AdminCaseCreateResult(created, false));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AdminCaseRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<AdminCaseRecord>>(
                cases.Values
                    .OrderByDescending(item => item.CreatedAtUtc)
                    .Take(limit)
                    .ToArray());
        }
    }

    /// <inheritdoc/>
    public Task<AdminCaseRecord?> GetAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(cases.Values.FirstOrDefault(
                item => item.CaseId == caseId));
        }
    }

    /// <inheritdoc/>
    public Task<AdminCaseRecord?> CloseAsync(
        string caseId,
        string closedBy,
        string resolution,
        string evidencePackageHash,
        DateTimeOffset closedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var entry = cases.FirstOrDefault(item => item.Value.CaseId == caseId);
            if (entry.Value is null) return Task.FromResult<AdminCaseRecord?>(null);
            if (entry.Value.Status == "Closed") return Task.FromResult<AdminCaseRecord?>(entry.Value);
            var closed = entry.Value with
            {
                Status = "Closed",
                ClosedAtUtc = closedAtUtc,
                ClosedBy = closedBy,
                Resolution = resolution,
                EvidencePackageHash = evidencePackageHash
            };
            cases[entry.Key] = closed;
            return Task.FromResult<AdminCaseRecord?>(closed);
        }
    }

    /// <summary>
    /// 从带独立审批的动作创建不可变案件记录。
    /// 缺少审批时失败，BeforeState 作为发起时证据保留，后续目标变化不能覆盖。
    /// </summary>
    internal static AdminCaseRecord CreateRecord(
        string sourceCommandId,
        AdminCaseType caseType,
        AdminActionRecord action,
        DateTimeOffset createdAtUtc)
    {
        var approval = action.Approval
            ?? throw new InvalidOperationException(
                "An approved action is required to create a case.");
        return new AdminCaseRecord(
            Guid.NewGuid().ToString(),
            sourceCommandId,
            action.ActionRequestId,
            caseType,
            action.TargetType,
            action.TargetId,
            action.RequestedBy,
            approval.ApprovedBy,
            createdAtUtc,
            action.Reason,
            action.TicketId,
            action.TraceId,
            action.BeforeState,
            "Open");
    }

    /// <summary>验证 sourceCommandId 重放的案件类型、动作、目标及调查关联完全一致。</summary>
    internal static void EnsureSame(
        AdminCaseRecord existing,
        AdminCaseType caseType,
        AdminActionRecord action)
    {
        if (existing.CaseType != caseType
            || existing.ActionRequestId != action.ActionRequestId
            || existing.TargetType != action.TargetType
            || existing.TargetId != action.TargetId
            || existing.TraceId != action.TraceId
            || existing.TicketId != action.TicketId)
        {
            throw new InvalidOperationException(
                "Case command id was reused with different parameters.");
        }
    }
}

/// <summary>
/// PostgreSQL 案件生产存储。
/// 使用事务级 advisory lock 和唯一来源命令保证多副本幂等，
/// 结案 SQL 只更新 Open 记录；该实例拥有数据源生命周期。
/// </summary>
public sealed class PostgresAdminCaseStore(NpgsqlDataSource postgres)
    : IAdminCaseStore, IAsyncDisposable
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
    public async Task<AdminCaseCreateResult> CreateAsync(
        string sourceCommandId,
        AdminCaseType caseType,
        AdminActionRecord action,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var proposed = InMemoryAdminCaseStore.CreateRecord(
            sourceCommandId,
            caseType,
            action,
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
            InMemoryAdminCaseStore.EnsureSame(existing, caseType, action);
            await transaction.CommitAsync(cancellationToken);
            return new AdminCaseCreateResult(existing, true);
        }
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.management_cases(
                case_id, source_command_id, action_request_id, case_type,
                target_type, target_id, requested_by, approved_by,
                created_at_utc, reason, ticket_id, trace_id, before_state, status)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,'Open')
            """,
            connection,
            transaction);
        AddParameters(insert, proposed);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminCaseCreateResult(proposed, false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AdminCaseRecord>> ListAsync(
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
        var result = new List<AdminCaseRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(Read(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task<AdminCaseRecord?> GetAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(caseId, out var parsed)) return null;
        await using var command = postgres.CreateCommand(
            $"{SelectSql} WHERE case_id=$1");
        command.Parameters.AddWithValue(parsed);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<AdminCaseRecord?> CloseAsync(
        string caseId,
        string closedBy,
        string resolution,
        string evidencePackageHash,
        DateTimeOffset closedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(caseId, out var parsed)) return null;
        await using var command = postgres.CreateCommand(
            $"""
            UPDATE admin_monitor.management_cases
            SET status='Closed', closed_at_utc=$2, closed_by=$3,
                resolution=$4, evidence_package_hash=$5
            WHERE case_id=$1 AND status='Open'
            RETURNING case_id, source_command_id, action_request_id, case_type,
                      target_type, target_id, requested_by, approved_by,
                      created_at_utc, reason, ticket_id, trace_id,
                      before_state::text, status, closed_at_utc, closed_by,
                      resolution, evidence_package_hash
            """);
        command.Parameters.AddWithValue(parsed);
        command.Parameters.AddWithValue(closedAtUtc);
        command.Parameters.AddWithValue(closedBy);
        command.Parameters.AddWithValue(resolution);
        command.Parameters.AddWithValue(evidencePackageHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken)) return Read(reader);
        return await GetAsync(caseId, cancellationToken);
    }

    private static async Task<AdminCaseRecord?> GetByCommandAsync(
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static void AddParameters(
        NpgsqlCommand command,
        AdminCaseRecord record)
    {
        command.Parameters.AddWithValue(Guid.Parse(record.CaseId));
        command.Parameters.AddWithValue(Guid.Parse(record.SourceCommandId));
        command.Parameters.AddWithValue(Guid.Parse(record.ActionRequestId));
        command.Parameters.AddWithValue(record.CaseType.ToString());
        command.Parameters.AddWithValue(record.TargetType);
        command.Parameters.AddWithValue(record.TargetId);
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

    private static AdminCaseRecord Read(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0).ToString(),
            reader.GetGuid(1).ToString(),
            reader.GetGuid(2).ToString(),
            Enum.Parse<AdminCaseType>(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(12)),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17));

    private const string SelectSql =
        """
        SELECT case_id, source_command_id, action_request_id, case_type,
               target_type, target_id, requested_by, approved_by,
               created_at_utc, reason, ticket_id, trace_id,
               before_state::text, status, closed_at_utc, closed_by,
               resolution, evidence_package_hash
        FROM admin_monitor.management_cases
        """;

    /// <summary>异步释放该存储独占的 PostgreSQL 数据源和连接池。</summary>
    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
