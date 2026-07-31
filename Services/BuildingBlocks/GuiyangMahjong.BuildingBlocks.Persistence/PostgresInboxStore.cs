using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using Npgsql;

namespace GuiyangMahjong.BuildingBlocks.Persistence;

/// <summary>
/// PostgreSQL Inbox 实现。
/// TryBegin 和 Complete 必须与消费者业务写入共用同一事务，回滚时二者一起消失。
/// </summary>
public sealed class PostgresInboxStore(
    NpgsqlDataSource dataSource,
    PersistenceTableNames names) : IInboxMaintenance
{
    /// <summary>
    /// 在调用方事务内登记消费开始；唯一键阻止相同消费者重复处理 event_id。
    /// 不支持的 Schema 在任何业务副作用前返回 UnsupportedSchema。
    /// </summary>
    public async Task<InboxBeginResult> TryBeginAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EventEnvelope envelope,
        string consumerName,
        int maximumSchemaVersion,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        if (!StrongValueValidation.IsIdentifier(consumerName)
            || maximumSchemaVersion <= 0)
            throw new ArgumentException("Inbox 消费者参数无效。");
        if (envelope.SchemaVersion > maximumSchemaVersion)
            return InboxBeginResult.UnsupportedSchema;

        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {names.Inbox}(
                consumer_name,event_id,event_type,schema_version,status,
                received_at,completed_at,failure_count,error_summary)
            VALUES ($1,$2,$3,$4,'Processing',$5,NULL,0,NULL)
            ON CONFLICT (consumer_name,event_id) DO NOTHING
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue(consumerName);
        insert.Parameters.AddWithValue(envelope.EventId.Value);
        insert.Parameters.AddWithValue(envelope.EventType);
        insert.Parameters.AddWithValue(envelope.SchemaVersion);
        insert.Parameters.AddWithValue(receivedAt);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
            return InboxBeginResult.Started;

        // 已记录失败的消息允许在下一次投递时由同一消费者重新开始；
        // 状态更新仍位于业务事务中，因此后续业务再次失败时会一起回滚。
        await using var restart = new NpgsqlCommand(
            $"""
            UPDATE {names.Inbox}
            SET status='Processing',received_at=$1,error_summary=NULL
            WHERE consumer_name=$2 AND event_id=$3 AND status='Failed'
            """,
            connection,
            transaction);
        restart.Parameters.AddWithValue(receivedAt);
        restart.Parameters.AddWithValue(consumerName);
        restart.Parameters.AddWithValue(envelope.EventId.Value);
        if (await restart.ExecuteNonQueryAsync(cancellationToken) == 1)
            return InboxBeginResult.Started;

        await using var select = new NpgsqlCommand(
            $"""
            SELECT status
            FROM {names.Inbox}
            WHERE consumer_name=$1 AND event_id=$2
            """,
            connection,
            transaction);
        select.Parameters.AddWithValue(consumerName);
        select.Parameters.AddWithValue(envelope.EventId.Value);
        var status = (string?)await select.ExecuteScalarAsync(cancellationToken)
                     ?? throw new InvalidOperationException(
                         "Inbox 唯一冲突后记录不可见。");
        return status == "Completed"
            ? InboxBeginResult.DuplicateCompleted
            : InboxBeginResult.AlreadyProcessing;
    }

    /// <summary>在消费者业务事务内标记完成；只有 Processing 可以迁移为 Completed。</summary>
    public async Task CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EventId eventId,
        string consumerName,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {names.Inbox}
            SET status='Completed',completed_at=$1,error_summary=NULL
            WHERE consumer_name=$2 AND event_id=$3 AND status='Processing'
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(completedAt);
        command.Parameters.AddWithValue(consumerName);
        command.Parameters.AddWithValue(eventId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Inbox 完成状态迁移失败。");
    }

    /// <summary>
    /// 在业务事务已经回滚后单独记录失败摘要。
    /// 摘要上限 512 字符且不得包含异常堆栈、凭据或私有业务载荷。
    /// </summary>
    public async Task RecordFailureAsync(
        EventEnvelope envelope,
        string consumerName,
        string errorSummary,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO {names.Inbox} AS target(
                consumer_name,event_id,event_type,schema_version,status,
                received_at,completed_at,failure_count,error_summary)
            VALUES ($1,$2,$3,$4,'Failed',$5,NULL,1,$6)
            ON CONFLICT (consumer_name,event_id) DO UPDATE
            SET status=CASE
                    WHEN target.status='Completed' THEN 'Completed'
                    ELSE 'Failed'
                END,
                failure_count=target.failure_count + 1,
                error_summary=CASE
                    WHEN target.status='Completed'
                    THEN target.error_summary
                    ELSE EXCLUDED.error_summary
                END
            """);
        command.Parameters.AddWithValue(consumerName);
        command.Parameters.AddWithValue(envelope.EventId.Value);
        command.Parameters.AddWithValue(envelope.EventType);
        command.Parameters.AddWithValue(envelope.SchemaVersion);
        command.Parameters.AddWithValue(failedAt);
        command.Parameters.AddWithValue(Truncate(errorSummary));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteCompletedBeforeAsync(
        DateTimeOffset completedBefore,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var command = dataSource.CreateCommand(
            $"""
            WITH candidates AS (
                SELECT consumer_name,event_id
                FROM {names.Inbox}
                WHERE status='Completed' AND completed_at < $1
                ORDER BY completed_at
                LIMIT $2
            )
            DELETE FROM {names.Inbox} AS item
            USING candidates
            WHERE item.consumer_name=candidates.consumer_name
              AND item.event_id=candidates.event_id
            """);
        command.Parameters.AddWithValue(completedBefore);
        command.Parameters.AddWithValue(limit);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Truncate(string value) =>
        value.Length <= 512 ? value : value[..512];
}
