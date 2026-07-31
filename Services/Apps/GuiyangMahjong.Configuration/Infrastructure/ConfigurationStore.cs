using System.Text.Json;
using GuiyangMahjong.Configuration.Domain;
using Npgsql;

namespace GuiyangMahjong.Configuration.Infrastructure;

/// <summary>
/// 配置中心权威存储边界。发布方法必须在一个事务中写入不可变版本、切换当前指针、冻结草稿并写 Outbox；
/// 调用方不得拆分这些步骤，否则服务中断会产生“版本已生效但事件丢失”的状态。
/// </summary>
public interface IConfigurationStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task<ConfigurationDraft> CreateDraftAsync(ConfigurationDraft draft, CancellationToken cancellationToken);
    Task<ConfigurationDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConfigurationDraft>> ListDraftsAsync(int limit, CancellationToken cancellationToken);
    Task<ConfigurationDraft> TransitionDraftAsync(ConfigurationDraft draft, long expectedRevision, CancellationToken cancellationToken);
    Task<PublishedConfiguration?> GetCurrentAsync(string configKey, CancellationToken cancellationToken);
    Task<PublishedConfiguration?> GetVersionAsync(string configKey, long version, CancellationToken cancellationToken);
    Task<IReadOnlyList<PublishedConfiguration>> ListVersionsAsync(string configKey, int limit, CancellationToken cancellationToken);
    Task<PublishedConfiguration> PublishAsync(
        ConfigurationDraft draft,
        long expectedDraftRevision,
        PublishedConfiguration version,
        string eventEnvelopeJson,
        string publishIdempotencyKey,
        CancellationToken cancellationToken);
    Task<PublishedConfiguration> PublishRollbackAsync(
        PublishedConfiguration version,
        string eventEnvelopeJson,
        string idempotencyKey,
        CancellationToken cancellationToken);
    Task RecordApplicationAsync(ConfigurationApplicationReport report, CancellationToken cancellationToken);
}

/// <summary>进程内测试存储；单锁模拟数据库 CAS 与发布事务，禁止测试绕过生产并发约束。</summary>
public sealed class InMemoryConfigurationStore : IConfigurationStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ConfigurationDraft> drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PublishedConfiguration>> versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConfigurationDraft> draftIdempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublishedConfiguration> publishIdempotency = new(StringComparer.Ordinal);
    private readonly List<ConfigurationApplicationReport> reports = [];

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<ConfigurationDraft> CreateDraftAsync(ConfigurationDraft draft, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = $"{draft.CreatedBy}\n{draft.IdempotencyKey}";
            if (draftIdempotency.TryGetValue(key, out var existing))
            {
                if (existing.ConfigKey != draft.ConfigKey || existing.PayloadHash != draft.PayloadHash)
                    throw new ConfigurationConflictException("IDEMPOTENCY_KEY_REUSED");
                return Task.FromResult(existing);
            }
            drafts.Add(draft.DraftId, draft);
            draftIdempotency.Add(key, draft);
            return Task.FromResult(draft);
        }
    }

    public Task<ConfigurationDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(drafts.GetValueOrDefault(draftId));
    }

    public Task<IReadOnlyList<ConfigurationDraft>> ListDraftsAsync(int limit, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ConfigurationDraft>>(
            drafts.Values.OrderByDescending(item => item.CreatedAtUtc).Take(limit).ToArray());
    }

    public Task<ConfigurationDraft> TransitionDraftAsync(
        ConfigurationDraft draft, long expectedRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!drafts.TryGetValue(draft.DraftId, out var current) || current.Revision != expectedRevision)
                throw new ConfigurationConflictException("DRAFT_REVISION_CONFLICT");
            drafts[draft.DraftId] = draft;
            return Task.FromResult(draft);
        }
    }

    public Task<PublishedConfiguration?> GetCurrentAsync(string configKey, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(versions.GetValueOrDefault(configKey)?.LastOrDefault());
    }

    public Task<PublishedConfiguration?> GetVersionAsync(string configKey, long version, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(versions.GetValueOrDefault(configKey)?.SingleOrDefault(item => item.Version == version));
    }

    public Task<IReadOnlyList<PublishedConfiguration>> ListVersionsAsync(
        string configKey, int limit, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<PublishedConfiguration>>(
            (versions.GetValueOrDefault(configKey) ?? []).OrderByDescending(item => item.Version).Take(limit).ToArray());
    }

    public Task<PublishedConfiguration> PublishAsync(
        ConfigurationDraft draft,
        long expectedDraftRevision,
        PublishedConfiguration version,
        string eventEnvelopeJson,
        string publishIdempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!drafts.TryGetValue(draft.DraftId, out var current) || current.Revision != expectedDraftRevision)
                throw new ConfigurationConflictException("DRAFT_REVISION_CONFLICT");
            if (publishIdempotency.TryGetValue(publishIdempotencyKey, out var duplicate)) return Task.FromResult(duplicate);
            var list = versions.GetValueOrDefault(version.ConfigKey) ?? [];
            if (list.Any(item => item.Version == version.Version))
                throw new ConfigurationConflictException("CONFIG_VERSION_CONFLICT");
            list.Add(version);
            versions[version.ConfigKey] = list;
            drafts[draft.DraftId] = draft;
            publishIdempotency[publishIdempotencyKey] = version;
            _ = eventEnvelopeJson;
            return Task.FromResult(version);
        }
    }

    public Task<PublishedConfiguration> PublishRollbackAsync(
        PublishedConfiguration version,
        string eventEnvelopeJson,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (publishIdempotency.TryGetValue(idempotencyKey, out var duplicate)) return Task.FromResult(duplicate);
            var list = versions.GetValueOrDefault(version.ConfigKey) ?? [];
            list.Add(version);
            versions[version.ConfigKey] = list;
            publishIdempotency[idempotencyKey] = version;
            _ = eventEnvelopeJson;
            return Task.FromResult(version);
        }
    }

    public Task RecordApplicationAsync(ConfigurationApplicationReport report, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!reports.Any(item => item.ReportId == report.ReportId)) reports.Add(report);
        }
        return Task.CompletedTask;
    }
}

/// <summary>配置状态或乐观并发冲突；API 映射为稳定 409，不触发透明重试写操作。</summary>
public sealed class ConfigurationConflictException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// PostgreSQL 配置存储。所有 SQL 只访问 configuration 与 configuration_integration，
/// Admin、Room、Settlement、Inventory 等业务 Schema 不在该运行身份授权范围内。
/// </summary>
public sealed class PostgresConfigurationStore(NpgsqlDataSource postgres) : IConfigurationStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand("SELECT 1 FROM configuration.config_versions LIMIT 1");
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try { await InitializeAsync(cancellationToken); return true; }
        catch (NpgsqlException) { return false; }
    }

    public async Task<ConfigurationDraft> CreateDraftAsync(ConfigurationDraft draft, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO configuration.config_drafts(
              draft_id,config_key,schema_version,payload,payload_hash,status,created_by,created_at_utc,
              validated_by,validated_at_utc,approved_by,approved_at_utc,reason_code,ticket_id,trace_id,idempotency_key,revision)
            VALUES($1,$2,$3,$4::jsonb,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17)
            ON CONFLICT(created_by,idempotency_key) DO NOTHING
            """;
        await using var command = postgres.CreateCommand(sql);
        AddDraft(command, draft);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (inserted == 1) return draft;
        await using var existingCommand = postgres.CreateCommand(
            "SELECT draft_id FROM configuration.config_drafts WHERE created_by=$1 AND idempotency_key=$2");
        existingCommand.Parameters.AddWithValue(draft.CreatedBy);
        existingCommand.Parameters.AddWithValue(draft.IdempotencyKey);
        var id = (Guid)(await existingCommand.ExecuteScalarAsync(cancellationToken)
            ?? throw new ConfigurationConflictException("IDEMPOTENCY_LOOKUP_FAILED"));
        var existing = await GetDraftAsync(id.ToString(), cancellationToken)
            ?? throw new ConfigurationConflictException("IDEMPOTENCY_LOOKUP_FAILED");
        if (existing.ConfigKey != draft.ConfigKey || existing.PayloadHash != draft.PayloadHash)
            throw new ConfigurationConflictException("IDEMPOTENCY_KEY_REUSED");
        return existing;
    }

    public async Task<ConfigurationDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(draftId, out var id)) return null;
        await using var command = postgres.CreateCommand($"{DraftSelect} WHERE draft_id=$1");
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDraft(reader) : null;
    }

    public async Task<IReadOnlyList<ConfigurationDraft>> ListDraftsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand($"{DraftSelect} ORDER BY created_at_utc DESC LIMIT $1");
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ConfigurationDraft>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadDraft(reader));
        return result;
    }

    public async Task<ConfigurationDraft> TransitionDraftAsync(
        ConfigurationDraft draft, long expectedRevision, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE configuration.config_drafts SET status=$1,validated_by=$2,validated_at_utc=$3,
              approved_by=$4,approved_at_utc=$5,revision=$6
            WHERE draft_id=$7 AND revision=$8
            """;
        await using var command = postgres.CreateCommand(sql);
        command.Parameters.AddWithValue(draft.Status.ToString());
        command.Parameters.AddWithValue((object?)draft.ValidatedBy ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)draft.ValidatedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)draft.ApprovedBy ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)draft.ApprovedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(draft.Revision);
        command.Parameters.AddWithValue(Guid.Parse(draft.DraftId));
        command.Parameters.AddWithValue(expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ConfigurationConflictException("DRAFT_REVISION_CONFLICT");
        return draft;
    }

    public async Task<PublishedConfiguration?> GetCurrentAsync(string configKey, CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand($"{VersionSelect} WHERE v.config_key=$1 AND c.version_id=v.version_id");
        command.Parameters.AddWithValue(configKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
    }

    public async Task<PublishedConfiguration?> GetVersionAsync(string configKey, long version, CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand($"{VersionSelect} WHERE v.config_key=$1 AND v.version_number=$2");
        command.Parameters.AddWithValue(configKey);
        command.Parameters.AddWithValue(version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
    }

    public async Task<IReadOnlyList<PublishedConfiguration>> ListVersionsAsync(
        string configKey, int limit, CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand($"{VersionSelect} WHERE v.config_key=$1 ORDER BY v.version_number DESC LIMIT $2");
        command.Parameters.AddWithValue(configKey);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PublishedConfiguration>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadVersion(reader));
        return result;
    }

    public async Task<PublishedConfiguration> PublishAsync(
        ConfigurationDraft draft,
        long expectedDraftRevision,
        PublishedConfiguration version,
        string eventEnvelopeJson,
        string publishIdempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await LockDraftAsync(connection, transaction, draft.DraftId, cancellationToken);
        if (locked.Revision != expectedDraftRevision)
            throw new ConfigurationConflictException("DRAFT_REVISION_CONFLICT");
        var duplicate = await FindPublishedByIdempotencyAsync(connection, transaction, publishIdempotencyKey, cancellationToken);
        if (duplicate is not null) { await transaction.RollbackAsync(cancellationToken); return duplicate; }
        await InsertVersionAsync(connection, transaction, version, publishIdempotencyKey, cancellationToken);
        await SetCurrentAsync(connection, transaction, version, cancellationToken);
        await SetDraftPublishedAsync(connection, transaction, draft, expectedDraftRevision, cancellationToken);
        await InsertOutboxAsync(connection, transaction, version, eventEnvelopeJson, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return version;
    }

    public async Task<PublishedConfiguration> PublishRollbackAsync(
        PublishedConfiguration version,
        string eventEnvelopeJson,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var duplicate = await FindPublishedByIdempotencyAsync(connection, transaction, idempotencyKey, cancellationToken);
        if (duplicate is not null) { await transaction.RollbackAsync(cancellationToken); return duplicate; }
        await InsertVersionAsync(connection, transaction, version, idempotencyKey, cancellationToken);
        await SetCurrentAsync(connection, transaction, version, cancellationToken);
        await InsertOutboxAsync(connection, transaction, version, eventEnvelopeJson, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return version;
    }

    public async Task RecordApplicationAsync(ConfigurationApplicationReport report, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO configuration.config_application_reports(
              report_id,config_key,version_number,service_name,service_version,region,cell,result,error_code,applied_at_utc,trace_id)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) ON CONFLICT(report_id) DO NOTHING
            """;
        await using var command = postgres.CreateCommand(sql);
        command.Parameters.AddWithValue(Guid.Parse(report.ReportId)); command.Parameters.AddWithValue(report.ConfigKey);
        command.Parameters.AddWithValue(report.Version); command.Parameters.AddWithValue(report.ServiceName);
        command.Parameters.AddWithValue(report.ServiceVersion); command.Parameters.AddWithValue(report.Region);
        command.Parameters.AddWithValue(report.Cell); command.Parameters.AddWithValue(report.Result);
        command.Parameters.AddWithValue((object?)report.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue(report.AppliedAtUtc); command.Parameters.AddWithValue(report.TraceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ConfigurationDraft> LockDraftAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string draftId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"{DraftSelect} WHERE draft_id=$1 FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(Guid.Parse(draftId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDraft(reader) : throw new KeyNotFoundException("Draft not found.");
    }

    private static async Task<PublishedConfiguration?> FindPublishedByIdempotencyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"{VersionSelect} WHERE v.idempotency_key=$1", connection, transaction);
        command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVersion(reader) : null;
    }

    private static async Task InsertVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PublishedConfiguration version, string idempotencyKey, CancellationToken cancellationToken)
    {
        const string sql = """
          INSERT INTO configuration.config_versions(version_id,config_key,version_number,schema_version,payload,payload_hash,
            signature,published_at_utc,published_by,approved_by,ticket_id,trace_id,rollback_of_version,idempotency_key)
          VALUES($1,$2,$3,$4,$5::jsonb,$6,$7,$8,$9,$10,$11,$12,$13,$14)
          """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(Guid.Parse(version.VersionId)); command.Parameters.AddWithValue(version.ConfigKey);
        command.Parameters.AddWithValue(version.Version); command.Parameters.AddWithValue(version.SchemaVersion);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(version.Payload, JsonOptions));
        command.Parameters.AddWithValue(version.PayloadHash); command.Parameters.AddWithValue(version.Signature);
        command.Parameters.AddWithValue(version.PublishedAtUtc); command.Parameters.AddWithValue(version.PublishedBy);
        command.Parameters.AddWithValue(version.ApprovedBy); command.Parameters.AddWithValue(version.TicketId);
        command.Parameters.AddWithValue(version.TraceId); command.Parameters.AddWithValue((object?)version.RollbackOfVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(idempotencyKey); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetCurrentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PublishedConfiguration version, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
          INSERT INTO configuration.config_current(config_key,version_id,version_number,updated_at_utc)
          VALUES($1,$2,$3,$4) ON CONFLICT(config_key) DO UPDATE SET version_id=EXCLUDED.version_id,
            version_number=EXCLUDED.version_number,updated_at_utc=EXCLUDED.updated_at_utc
          WHERE configuration.config_current.version_number < EXCLUDED.version_number
          """, connection, transaction);
        command.Parameters.AddWithValue(version.ConfigKey); command.Parameters.AddWithValue(Guid.Parse(version.VersionId));
        command.Parameters.AddWithValue(version.Version); command.Parameters.AddWithValue(version.PublishedAtUtc);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ConfigurationConflictException("CURRENT_VERSION_CONFLICT");
    }

    private static async Task SetDraftPublishedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ConfigurationDraft draft, long expectedRevision, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
          UPDATE configuration.config_drafts SET status='Published',approved_by=$1,approved_at_utc=$2,revision=$3
          WHERE draft_id=$4 AND revision=$5
          """, connection, transaction);
        command.Parameters.AddWithValue(draft.ApprovedBy!); command.Parameters.AddWithValue(draft.ApprovedAtUtc!.Value);
        command.Parameters.AddWithValue(draft.Revision); command.Parameters.AddWithValue(Guid.Parse(draft.DraftId));
        command.Parameters.AddWithValue(expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new ConfigurationConflictException("DRAFT_REVISION_CONFLICT");
    }

    private static async Task InsertOutboxAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PublishedConfiguration version, string envelope, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(envelope);
        var root = document.RootElement;
        await using var command = new NpgsqlCommand("""
          INSERT INTO configuration_integration.platform_outbox(event_id,event_type,schema_version,aggregate_type,aggregate_id,
            aggregate_version,payload_json,occurred_at,created_at,status,attempt_count,next_attempt_at)
          VALUES($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$8,'Pending',0,$8)
          """, connection, transaction);
        command.Parameters.AddWithValue(Guid.Parse(root.GetProperty("event_id").GetString()!));
        command.Parameters.AddWithValue(root.GetProperty("event_type").GetString()!);
        command.Parameters.AddWithValue(root.GetProperty("schema_version").GetInt32());
        command.Parameters.AddWithValue(root.GetProperty("aggregate_type").GetString()!);
        command.Parameters.AddWithValue(root.GetProperty("aggregate_id").GetString()!);
        command.Parameters.AddWithValue(root.GetProperty("aggregate_version").GetInt64());
        command.Parameters.AddWithValue(envelope); command.Parameters.AddWithValue(version.PublishedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddDraft(NpgsqlCommand command, ConfigurationDraft draft)
    {
        command.Parameters.AddWithValue(Guid.Parse(draft.DraftId)); command.Parameters.AddWithValue(draft.ConfigKey);
        command.Parameters.AddWithValue(draft.SchemaVersion); command.Parameters.AddWithValue(JsonSerializer.Serialize(draft.Payload, JsonOptions));
        command.Parameters.AddWithValue(draft.PayloadHash); command.Parameters.AddWithValue(draft.Status.ToString());
        command.Parameters.AddWithValue(draft.CreatedBy); command.Parameters.AddWithValue(draft.CreatedAtUtc);
        command.Parameters.AddWithValue((object?)draft.ValidatedBy ?? DBNull.Value); command.Parameters.AddWithValue((object?)draft.ValidatedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)draft.ApprovedBy ?? DBNull.Value); command.Parameters.AddWithValue((object?)draft.ApprovedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)draft.ReasonCode ?? DBNull.Value); command.Parameters.AddWithValue(draft.TicketId);
        command.Parameters.AddWithValue(draft.TraceId); command.Parameters.AddWithValue(draft.IdempotencyKey); command.Parameters.AddWithValue(draft.Revision);
    }

    private static ConfigurationDraft ReadDraft(NpgsqlDataReader reader) => new(
        reader.GetGuid(0).ToString(), reader.GetString(1), reader.GetInt32(2),
        JsonSerializer.Deserialize<PlatformConfigurationPayload>(reader.GetString(3), JsonOptions)!,
        reader.GetString(4), Enum.Parse<ConfigurationDraftStatus>(reader.GetString(5)), reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11), reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetInt64(16));

    private static PublishedConfiguration ReadVersion(NpgsqlDataReader reader) => new(
        reader.GetGuid(0).ToString(), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3),
        JsonSerializer.Deserialize<PlatformConfigurationPayload>(reader.GetString(4), JsonOptions)!, reader.GetString(5),
        reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetString(9),
        reader.GetString(10), reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetInt64(12));

    private const string DraftSelect = """
      SELECT draft_id,config_key,schema_version,payload::text,payload_hash,status,created_by,created_at_utc,
        validated_by,validated_at_utc,approved_by,approved_at_utc,reason_code,ticket_id,trace_id,idempotency_key,revision
      FROM configuration.config_drafts
      """;
    private const string VersionSelect = """
      SELECT v.version_id,v.config_key,v.version_number,v.schema_version,v.payload::text,v.payload_hash,v.signature,
        v.published_at_utc,v.published_by,v.approved_by,v.ticket_id,v.trace_id,v.rollback_of_version
      FROM configuration.config_versions v LEFT JOIN configuration.config_current c ON c.config_key=v.config_key
      """;

    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
