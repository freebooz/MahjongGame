// 玩家证据内存存储：提供受限证据投影、聊天授权和调查查询的测试实现。
// 数据仅在当前进程存活，仍必须执行与生产存储一致的幂等、授权窗口和去重约束。
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using Npgsql;
using NpgsqlTypes;

namespace GuiyangMahjong.Admin.Storage;

/// <summary>
/// 玩家调查证据与聊天授权的持久化边界。
/// 证据按事件和来源双重去重；聊天授权只返回同时匹配玩家、工单、操作者和有效期的记录。
/// </summary>
public interface IPlayerEvidenceStore
{
    /// <summary>初始化或验证证据表结构；失败时 Admin 服务不得进入就绪状态。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>检查证据与授权存储可用性，不延长任何授权窗口。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>按 EventId 幂等接收证据；同一来源被不同事件复用时必须拒绝。</summary>
    Task<PlayerEvidenceIngestResult> IngestAsync(
        IngestPlayerEvidenceRequest request,
        DateTimeOffset ingestedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>按玩家和证据类型读取有限批次，结果按实际发生时间倒序。</summary>
    Task<IReadOnlyList<PlayerEvidenceRecord>> ListAsync(
        string playerId,
        PlayerEvidenceType evidenceType,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>按 GrantId 幂等保存双人审批形成的聊天授权，不得通过重放延长有效期。</summary>
    Task<PlayerChatAccessGrantIngestResult> IngestChatGrantAsync(
        IngestPlayerChatAccessGrantRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 查询指定操作者当前有效的聊天授权；必须同时匹配玩家和工单，
    /// now 使用服务端 UTC 时间，过期记录返回空。
    /// </summary>
    Task<PlayerChatAccessGrant?> GetActiveChatGrantAsync(
        string playerId,
        string ticketId,
        string operatorId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单进程测试用证据存储。
/// 两个字典只在 gate 内访问，以模拟生产唯一约束；数据随进程退出丢失。
/// </summary>
public sealed class InMemoryPlayerEvidenceStore : IPlayerEvidenceStore
{
    // 证据按事件标识索引，聊天授权按 GrantId 索引；存储值均为克隆后的不可变记录。
    private readonly Dictionary<string, PlayerEvidenceRecord> evidence =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerChatAccessGrant> chatGrants =
        new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <inheritdoc/>
    public Task<PlayerEvidenceIngestResult> IngestAsync(
        IngestPlayerEvidenceRequest request,
        DateTimeOffset ingestedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var proposed = CreateEvidence(request, ingestedAtUtc);
        lock (gate)
        {
            if (evidence.TryGetValue(request.EventId, out var existing))
            {
                EnsureSame(existing, proposed);
                return Task.FromResult(
                    new PlayerEvidenceIngestResult(existing, true));
            }
            if (evidence.Values.Any(item =>
                item.EvidenceType == proposed.EvidenceType
                && item.SourceReference == proposed.SourceReference))
            {
                throw new InvalidOperationException(
                    "Evidence source reference was already ingested under a different event id.");
            }
            evidence.Add(request.EventId, proposed);
            return Task.FromResult(
                new PlayerEvidenceIngestResult(proposed, false));
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PlayerEvidenceRecord>> ListAsync(
        string playerId,
        PlayerEvidenceType evidenceType,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PlayerEvidenceRecord>>(
                evidence.Values
                    .Where(item => item.PlayerId == playerId
                        && item.EvidenceType == evidenceType)
                    .OrderByDescending(item => item.OccurredAtUtc)
                    .Take(limit)
                    .ToArray());
        }
    }

    /// <inheritdoc/>
    public Task<PlayerChatAccessGrantIngestResult> IngestChatGrantAsync(
        IngestPlayerChatAccessGrantRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var proposed = CreateGrant(request, createdAtUtc);
        lock (gate)
        {
            if (chatGrants.TryGetValue(request.GrantId, out var existing))
            {
                EnsureSame(existing, proposed);
                return Task.FromResult(
                    new PlayerChatAccessGrantIngestResult(existing, true));
            }
            chatGrants.Add(request.GrantId, proposed);
            return Task.FromResult(
                new PlayerChatAccessGrantIngestResult(proposed, false));
        }
    }

    /// <inheritdoc/>
    public Task<PlayerChatAccessGrant?> GetActiveChatGrantAsync(
        string playerId,
        string ticketId,
        string operatorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(chatGrants.Values
                .Where(item => item.PlayerId == playerId
                    && item.TicketId == ticketId
                    && item.GrantedTo == operatorId
                    && item.ExpiresAtUtc > now)
                .MaxBy(item => item.CreatedAtUtc));
        }
    }

    /// <summary>规范化 UTC 时间并克隆 JSON，生成不受请求对象生命周期影响的证据值。</summary>
    internal static PlayerEvidenceRecord CreateEvidence(
        IngestPlayerEvidenceRequest request,
        DateTimeOffset ingestedAtUtc) =>
        new(
            Guid.Parse(request.EventId).ToString(),
            request.PlayerId,
            request.EvidenceType,
            NormalizeTimestamp(request.OccurredAtUtc),
            request.SourceReference,
            request.Data.Clone(),
            request.Sensitivity,
            NormalizeTimestamp(ingestedAtUtc));

    /// <summary>克隆授权范围并规范化 UTC 时间，避免调用方修改原数组或时区语义。</summary>
    internal static PlayerChatAccessGrant CreateGrant(
        IngestPlayerChatAccessGrantRequest request,
        DateTimeOffset createdAtUtc) =>
        new(
            Guid.Parse(request.GrantId).ToString(),
            request.PlayerId,
            request.TicketId,
            request.GrantedTo,
            request.ApprovedBy,
            request.Reason,
            request.TraceId,
            NormalizeTimestamp(request.WindowStartsAtUtc),
            NormalizeTimestamp(request.WindowEndsAtUtc),
            NormalizeTimestamp(request.ExpiresAtUtc),
            request.Scopes
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            NormalizeTimestamp(createdAtUtc));

    internal static DateTimeOffset NormalizeTimestamp(
        DateTimeOffset value) =>
        new(
            value.UtcTicks - value.UtcTicks % 10,
            TimeSpan.Zero);

    internal static void EnsureSame(
        PlayerEvidenceRecord existing,
        PlayerEvidenceRecord proposed)
    {
        if (existing.PlayerId != proposed.PlayerId
            || existing.EvidenceType != proposed.EvidenceType
            || existing.SourceReference != proposed.SourceReference
            || existing.OccurredAtUtc != proposed.OccurredAtUtc
            || existing.Sensitivity != proposed.Sensitivity
            || !JsonElement.DeepEquals(existing.Data, proposed.Data))
        {
            throw new InvalidOperationException(
                "Evidence event id was reused with different parameters.");
        }
    }

    internal static void EnsureSame(
        PlayerChatAccessGrant existing,
        PlayerChatAccessGrant proposed)
    {
        if (existing.PlayerId != proposed.PlayerId
            || existing.TicketId != proposed.TicketId
            || existing.GrantedTo != proposed.GrantedTo
            || existing.ApprovedBy != proposed.ApprovedBy
            || existing.Reason != proposed.Reason
            || existing.TraceId != proposed.TraceId
            || existing.WindowStartsAtUtc != proposed.WindowStartsAtUtc
            || existing.WindowEndsAtUtc != proposed.WindowEndsAtUtc
            || existing.ExpiresAtUtc != proposed.ExpiresAtUtc
            || !existing.Scopes.SequenceEqual(proposed.Scopes))
        {
            throw new InvalidOperationException(
                "Chat grant id was reused with different parameters.");
        }
    }
}

/// <summary>
/// PostgreSQL 玩家证据生产存储。
/// 事件与来源唯一约束保证证据幂等，聊天授权查询同时约束玩家、工单、操作者和 UTC 有效期；
/// 该实例拥有数据源生命周期。
/// </summary>
public sealed class PostgresPlayerEvidenceStore(NpgsqlDataSource postgres)
    : IPlayerEvidenceStore, IAsyncDisposable
{
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
    public async Task<PlayerEvidenceIngestResult> IngestAsync(
        IngestPlayerEvidenceRequest request,
        DateTimeOffset ingestedAtUtc,
        CancellationToken cancellationToken)
    {
        var proposed =
            InMemoryPlayerEvidenceStore.CreateEvidence(request, ingestedAtUtc);
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, request.EventId, cancellationToken);
        await LockAsync(
            connection,
            transaction,
            $"{request.EvidenceType}:{request.SourceReference}",
            cancellationToken);
        var existing = await GetEvidenceAsync(
            connection,
            transaction,
            request.EventId,
            cancellationToken);
        if (existing is not null)
        {
            InMemoryPlayerEvidenceStore.EnsureSame(existing, proposed);
            await transaction.CommitAsync(cancellationToken);
            return new PlayerEvidenceIngestResult(existing, true);
        }
        var sourceExisting = await GetEvidenceBySourceAsync(
            connection,
            transaction,
            request.EvidenceType,
            request.SourceReference,
            cancellationToken);
        if (sourceExisting is not null)
        {
            throw new InvalidOperationException(
                "Evidence source reference was already ingested under a different event id.");
        }
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.player_evidence(
                event_id, player_id, evidence_type, occurred_at_utc,
                source_reference, data, sensitivity, ingested_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            """,
            connection,
            transaction);
        AddEvidenceParameters(insert, proposed);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PlayerEvidenceIngestResult(proposed, false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PlayerEvidenceRecord>> ListAsync(
        string playerId,
        PlayerEvidenceType evidenceType,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            $"""
            {EvidenceSelectSql}
            WHERE player_id=$1 AND evidence_type=$2
            ORDER BY occurred_at_utc DESC
            LIMIT $3
            """);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(evidenceType.ToString());
        command.Parameters.AddWithValue(limit);
        var result = new List<PlayerEvidenceRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadEvidence(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task<PlayerChatAccessGrantIngestResult> IngestChatGrantAsync(
        IngestPlayerChatAccessGrantRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var proposed =
            InMemoryPlayerEvidenceStore.CreateGrant(request, createdAtUtc);
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, request.GrantId, cancellationToken);
        var existing = await GetGrantAsync(
            connection,
            transaction,
            request.GrantId,
            cancellationToken);
        if (existing is not null)
        {
            InMemoryPlayerEvidenceStore.EnsureSame(existing, proposed);
            await transaction.CommitAsync(cancellationToken);
            return new PlayerChatAccessGrantIngestResult(existing, true);
        }
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.player_chat_access_grants(
                grant_id, player_id, ticket_id, granted_to, approved_by,
                reason, trace_id,
                window_starts_at_utc, window_ends_at_utc, expires_at_utc,
                scopes, created_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            """,
            connection,
            transaction);
        AddGrantParameters(insert, proposed);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PlayerChatAccessGrantIngestResult(proposed, false);
    }

    /// <inheritdoc/>
    public async Task<PlayerChatAccessGrant?> GetActiveChatGrantAsync(
        string playerId,
        string ticketId,
        string operatorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            $"""
            {GrantSelectSql}
            WHERE player_id=$1 AND ticket_id=$2 AND granted_to=$3
              AND expires_at_utc>$4
            ORDER BY created_at_utc DESC
            LIMIT 1
            """);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(ticketId);
        command.Parameters.AddWithValue(operatorId);
        command.Parameters.AddWithValue(now);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadGrant(reader)
            : null;
    }

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

    private static async Task<PlayerEvidenceRecord?> GetEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"{EvidenceSelectSql} WHERE event_id=$1",
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(eventId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadEvidence(reader)
            : null;
    }

    private static async Task<PlayerChatAccessGrant?> GetGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string grantId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"{GrantSelectSql} WHERE grant_id=$1",
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(grantId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadGrant(reader)
            : null;
    }

    private static async Task<PlayerEvidenceRecord?> GetEvidenceBySourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlayerEvidenceType evidenceType,
        string sourceReference,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"{EvidenceSelectSql} WHERE evidence_type=$1 AND source_reference=$2",
            connection,
            transaction);
        command.Parameters.AddWithValue(evidenceType.ToString());
        command.Parameters.AddWithValue(sourceReference);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadEvidence(reader)
            : null;
    }

    private static void AddEvidenceParameters(
        NpgsqlCommand command,
        PlayerEvidenceRecord evidence)
    {
        command.Parameters.AddWithValue(Guid.Parse(evidence.EventId));
        command.Parameters.AddWithValue(evidence.PlayerId);
        command.Parameters.AddWithValue(evidence.EvidenceType.ToString());
        command.Parameters.AddWithValue(evidence.OccurredAtUtc);
        command.Parameters.AddWithValue(evidence.SourceReference);
        command.Parameters.Add(
            new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Jsonb,
                Value = evidence.Data.GetRawText()
            });
        command.Parameters.AddWithValue(evidence.Sensitivity.ToString());
        command.Parameters.AddWithValue(evidence.IngestedAtUtc);
    }

    private static void AddGrantParameters(
        NpgsqlCommand command,
        PlayerChatAccessGrant grant)
    {
        command.Parameters.AddWithValue(Guid.Parse(grant.GrantId));
        command.Parameters.AddWithValue(grant.PlayerId);
        command.Parameters.AddWithValue(grant.TicketId);
        command.Parameters.AddWithValue(grant.GrantedTo);
        command.Parameters.AddWithValue(grant.ApprovedBy);
        command.Parameters.AddWithValue(grant.Reason);
        command.Parameters.AddWithValue(grant.TraceId);
        command.Parameters.AddWithValue(grant.WindowStartsAtUtc);
        command.Parameters.AddWithValue(grant.WindowEndsAtUtc);
        command.Parameters.AddWithValue(grant.ExpiresAtUtc);
        command.Parameters.Add(
            new NpgsqlParameter<string[]>
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                TypedValue = grant.Scopes
            });
        command.Parameters.AddWithValue(grant.CreatedAtUtc);
    }

    private static PlayerEvidenceRecord ReadEvidence(
        NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0).ToString(),
            reader.GetString(1),
            Enum.Parse<PlayerEvidenceType>(reader.GetString(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetString(4),
            JsonDocument.Parse(reader.GetString(5)).RootElement.Clone(),
            Enum.Parse<PlayerEvidenceSensitivity>(reader.GetString(6)),
            reader.GetFieldValue<DateTimeOffset>(7));

    private static PlayerChatAccessGrant ReadGrant(
        NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0).ToString(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetFieldValue<string[]>(10),
            reader.GetFieldValue<DateTimeOffset>(11));

    private const string EvidenceSelectSql =
        """
        SELECT event_id, player_id, evidence_type, occurred_at_utc,
               source_reference, data::text, sensitivity, ingested_at_utc
        FROM admin_monitor.player_evidence
        """;

    private const string GrantSelectSql =
        """
        SELECT grant_id, player_id, ticket_id, granted_to, approved_by,
               reason, trace_id,
               window_starts_at_utc, window_ends_at_utc, expires_at_utc,
               scopes, created_at_utc
        FROM admin_monitor.player_chat_access_grants
        """;

    /// <summary>异步释放该存储独占的 PostgreSQL 数据源和连接池。</summary>
    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
