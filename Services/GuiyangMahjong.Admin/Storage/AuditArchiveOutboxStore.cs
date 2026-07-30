// 审计归档 Outbox 存储：把业务审计提交与外部归档投递解耦，保存重试次数和确认状态。
// 读取批次必须有界且保持稳定顺序，确认与失败更新只能作用于对应 Outbox 主键。
using System.Text.Json;
using Npgsql;

namespace GuiyangMahjong.Admin.Storage;

/// <summary>
/// 等待投递到独立审计归档系统的最小记录。
/// Payload 是提交时冻结的脱敏审计 JSON，AttemptCount 用于有界重试和告警。
/// </summary>
public sealed record AuditArchiveOutboxRecord(
    string AuditId,
    JsonElement Payload,
    int AttemptCount);

/// <summary>
/// 审计归档 Outbox 的租约式消费接口。
/// 领取、完成和失败均以 workerId 约束所有权，避免多副本重复确认其他工作者的任务。
/// </summary>
public interface IAuditArchiveOutboxStore
{
    /// <summary>检查归档队列是否可访问且不存在需人工处理的永久失败项。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>按可用时间领取有限批次，并将租约设置到指定 UTC 时间。</summary>
    Task<IReadOnlyList<AuditArchiveOutboxRecord>> ClaimAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    /// <summary>当前租约所有者确认外部归档成功并记录 UTC 完成时间；重复确认无副作用。</summary>
    Task CompleteAsync(
        string auditId,
        string workerId,
        DateTimeOffset archivedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>记录脱敏错误并释放租约；永久失败不再自动领取，瞬态失败延迟重试。</summary>
    Task FailAsync(
        string auditId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken);
}

/// <summary>
/// 未配置外部归档时的空实现，只用于开发和显式关闭归档的环境。
/// 它不会声称存在待处理记录，也不会把任何审计载荷发送到外部系统。
/// </summary>
public sealed class InMemoryAuditArchiveOutboxStore
    : IAuditArchiveOutboxStore
{
    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <inheritdoc/>
    public Task<IReadOnlyList<AuditArchiveOutboxRecord>> ClaimAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditArchiveOutboxRecord>>([]);

    /// <inheritdoc/>
    public Task CompleteAsync(
        string auditId,
        string workerId,
        DateTimeOffset archivedAtUtc,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task FailAsync(
        string auditId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// PostgreSQL 审计归档 Outbox 实现。
/// 使用 FOR UPDATE SKIP LOCKED 和所有者条件更新支持多副本消费，
/// 数据源生命周期由该存储拥有并通过异步释放结束。
/// </summary>
public sealed class PostgresAuditArchiveOutboxStore(
    NpgsqlDataSource postgres) : IAuditArchiveOutboxStore, IAsyncDisposable
{
    /// <inheritdoc/>
    public async Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = postgres.CreateCommand(
                """
                SELECT NOT EXISTS (
                    SELECT 1
                    FROM admin_monitor.audit_archive_outbox
                    WHERE status='Failed')
                """);
            return await command.ExecuteScalarAsync(cancellationToken)
                is true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AuditArchiveOutboxRecord>> ClaimAsync(
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
                SELECT audit_id
                FROM admin_monitor.audit_archive_outbox
                WHERE (status='Pending' AND available_at_utc <= $1)
                   OR (status='Processing' AND lease_expires_at_utc <= $1)
                ORDER BY available_at_utc
                FOR UPDATE SKIP LOCKED
                LIMIT $2
            )
            UPDATE admin_monitor.audit_archive_outbox AS item
            SET status='Processing',
                attempt_count=item.attempt_count+1,
                lock_owner=$3,
                lease_expires_at_utc=$4
            FROM candidates
            WHERE item.audit_id=candidates.audit_id
            RETURNING item.audit_id, item.payload::text, item.attempt_count
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(leaseExpiresAtUtc);
        var result = new List<AuditArchiveOutboxRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AuditArchiveOutboxRecord(
                reader.GetGuid(0).ToString(),
                JsonDocument.Parse(reader.GetString(1))
                    .RootElement.Clone(),
                reader.GetInt32(2)));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc/>
    public async Task CompleteAsync(
        string auditId,
        string workerId,
        DateTimeOffset archivedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            UPDATE admin_monitor.audit_archive_outbox
            SET status='Archived', archived_at_utc=$1,
                lock_owner=NULL, lease_expires_at_utc=NULL, last_error=NULL
            WHERE audit_id=$2 AND status='Processing' AND lock_owner=$3
            """);
        command.Parameters.AddWithValue(archivedAtUtc);
        command.Parameters.AddWithValue(Guid.Parse(auditId));
        command.Parameters.AddWithValue(workerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task FailAsync(
        string auditId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            UPDATE admin_monitor.audit_archive_outbox
            SET status=$1, available_at_utc=$2,
                lock_owner=NULL, lease_expires_at_utc=NULL, last_error=$3
            WHERE audit_id=$4 AND status='Processing' AND lock_owner=$5
            """);
        command.Parameters.AddWithValue(terminal ? "Failed" : "Pending");
        command.Parameters.AddWithValue(availableAtUtc);
        command.Parameters.AddWithValue(
            error.Length > 1000 ? error[..1000] : error);
        command.Parameters.AddWithValue(Guid.Parse(auditId));
        command.Parameters.AddWithValue(workerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>异步释放该实例独占的 PostgreSQL 数据源及连接池资源。</summary>
    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
