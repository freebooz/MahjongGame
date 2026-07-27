using System.Text.Json;
using Npgsql;

namespace GuiyangMahjong.Admin.Storage;

public sealed record AuditArchiveOutboxRecord(
    string AuditId,
    JsonElement Payload,
    int AttemptCount);

public interface IAuditArchiveOutboxStore
{
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditArchiveOutboxRecord>> ClaimAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);
    Task CompleteAsync(
        string auditId,
        string workerId,
        DateTimeOffset archivedAtUtc,
        CancellationToken cancellationToken);
    Task FailAsync(
        string auditId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken);
}

public sealed class InMemoryAuditArchiveOutboxStore
    : IAuditArchiveOutboxStore
{
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<AuditArchiveOutboxRecord>> ClaimAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditArchiveOutboxRecord>>([]);

    public Task CompleteAsync(
        string auditId,
        string workerId,
        DateTimeOffset archivedAtUtc,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailAsync(
        string auditId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PostgresAuditArchiveOutboxStore(
    NpgsqlDataSource postgres) : IAuditArchiveOutboxStore, IAsyncDisposable
{
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

    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
