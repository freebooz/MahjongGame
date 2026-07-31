using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.Contracts.Common;
using Npgsql;

namespace GuiyangMahjong.BuildingBlocks.Persistence;

/// <summary>
/// PostgreSQL Outbox 实现。
/// 业务写入必须把已有 connection/transaction 传给 AddAsync，确保业务状态与事件原子提交。
/// </summary>
public sealed class PostgresOutboxStore(
    NpgsqlDataSource dataSource,
    PersistenceTableNames names) : IOutboxStore
{
    /// <summary>
    /// 在调用方业务事务中插入事件；event_id 主键阻止同一事实重复落库。
    /// 方法不会提交、回滚或发布消息，事务所有权始终属于调用方。
    /// </summary>
    public async Task AddAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {names.Outbox}(
                event_id,event_type,schema_version,aggregate_type,aggregate_id,
                aggregate_version,payload_json,occurred_at,created_at,status,
                attempt_count,next_attempt_at,lock_owner,lease_expires_at,
                published_at,error_summary)
            VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$9,$10,$11,$12,$13,$14,$15,$16)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(message.EventId.Value);
        command.Parameters.AddWithValue(message.EventType);
        command.Parameters.AddWithValue(message.SchemaVersion);
        command.Parameters.AddWithValue(message.AggregateType);
        command.Parameters.AddWithValue(message.AggregateId);
        command.Parameters.AddWithValue(message.AggregateVersion);
        command.Parameters.AddWithValue(message.PayloadJson);
        command.Parameters.AddWithValue(message.OccurredAt);
        command.Parameters.AddWithValue(message.CreatedAt);
        command.Parameters.AddWithValue(message.Status.ToString());
        command.Parameters.AddWithValue(message.AttemptCount);
        command.Parameters.AddWithValue(message.NextAttemptAt);
        command.Parameters.AddWithValue((object?)message.LockOwner ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)message.LeaseExpiresAt ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)message.PublishedAt ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)message.ErrorSummary ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateWorker(workerId, limit, leaseDuration);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            WITH candidates AS (
                SELECT event_id
                FROM {names.Outbox}
                WHERE (status='Pending' AND next_attempt_at <= $1)
                   OR (status='Processing' AND lease_expires_at <= $1)
                ORDER BY next_attempt_at, created_at
                FOR UPDATE SKIP LOCKED
                LIMIT $2
            )
            UPDATE {names.Outbox} AS item
            SET status='Processing',
                attempt_count=item.attempt_count + 1,
                lock_owner=$3,
                lease_expires_at=$4,
                error_summary=NULL
            FROM candidates
            WHERE item.event_id=candidates.event_id
            RETURNING item.event_id,item.event_type,item.schema_version,
                item.aggregate_type,item.aggregate_id,item.aggregate_version,
                item.payload_json::text,item.occurred_at,item.created_at,item.status,
                item.attempt_count,item.next_attempt_at,item.lock_owner,
                item.lease_expires_at,item.published_at,item.error_summary
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(now + leaseDuration);
        var messages = new List<OutboxMessage>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            messages.Add(ReadMessage(reader));
        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return messages;
    }

    /// <inheritdoc/>
    public Task<bool> MarkPublishedAsync(
        EventId eventId,
        string workerId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken) =>
        UpdateClaimAsync(
            eventId,
            workerId,
            "Published",
            publishedAt,
            null,
            null,
            cancellationToken);

    /// <inheritdoc/>
    public Task<bool> MarkFailedAsync(
        EventId eventId,
        string workerId,
        string errorSummary,
        DateTimeOffset nextAttemptAt,
        bool terminal,
        CancellationToken cancellationToken) =>
        UpdateClaimAsync(
            eventId,
            workerId,
            terminal ? "Failed" : "Pending",
            null,
            nextAttemptAt,
            Truncate(errorSummary),
            cancellationToken);

    /// <inheritdoc/>
    public async Task<int> ArchivePublishedAsync(
        DateTimeOffset publishedBefore,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"""
            WITH candidates AS (
                SELECT event_id
                FROM {names.Outbox}
                WHERE status='Published' AND published_at < $1
                ORDER BY published_at
                FOR UPDATE SKIP LOCKED
                LIMIT $2
            ), moved AS (
                DELETE FROM {names.Outbox} AS item
                USING candidates
                WHERE item.event_id=candidates.event_id
                RETURNING item.*
            )
            INSERT INTO {names.OutboxArchive}
            SELECT * FROM moved
            ON CONFLICT (event_id) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(publishedBefore);
        command.Parameters.AddWithValue(limit);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    private async Task<bool> UpdateClaimAsync(
        EventId eventId,
        string workerId,
        string status,
        DateTimeOffset? publishedAt,
        DateTimeOffset? nextAttemptAt,
        string? errorSummary,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE {names.Outbox}
            SET status=$1,
                published_at=$2,
                next_attempt_at=COALESCE($3,next_attempt_at),
                lock_owner=NULL,
                lease_expires_at=NULL,
                error_summary=$4
            WHERE event_id=$5 AND status='Processing' AND lock_owner=$6
            """);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue((object?)publishedAt ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)nextAttemptAt ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)errorSummary ?? DBNull.Value);
        command.Parameters.AddWithValue(eventId.Value);
        command.Parameters.AddWithValue(workerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static OutboxMessage ReadMessage(NpgsqlDataReader reader) =>
        new(
            EventId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            Enum.Parse<OutboxStatus>(reader.GetString(9)),
            reader.GetInt32(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
            reader.IsDBNull(15) ? null : reader.GetString(15));

    private static void ValidateWorker(
        string workerId,
        int limit,
        TimeSpan leaseDuration)
    {
        if (!StrongValueValidation.IsIdentifier(workerId)
            || limit is < 1 or > 10_000
            || leaseDuration <= TimeSpan.Zero
            || leaseDuration > TimeSpan.FromMinutes(30))
            throw new ArgumentException("Outbox 领取参数无效。");
    }

    private static string Truncate(string value) =>
        value.Length <= 512 ? value : value[..512];
}
