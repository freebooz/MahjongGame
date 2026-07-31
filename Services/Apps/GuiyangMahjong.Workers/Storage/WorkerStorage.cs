using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.BuildingBlocks.Persistence;
using GuiyangMahjong.Contracts.Events;
using Npgsql;

namespace GuiyangMahjong.Workers.Storage;

/// <summary>Worker 投影类型；每一种都使用独立 Durable Consumer 和 Inbox 身份。</summary>
public enum ProjectionKind
{
    GameRecords,
    Leaderboard,
    Audit
}

/// <summary>消费事务结果，用于决定 ACK、快速 ACK 重复消息或稍后重试。</summary>
public enum ProjectionResult
{
    Applied,
    Duplicate,
    Stale
}

/// <summary>
/// Worker 自有 PostgreSQL 存储。Inbox、聚影和聚合版本检查点始终在同一事务提交，
/// 因此“业务成功但 ACK 丢失”只会再次命中 Duplicate，不会重复产生投影副作用。
/// </summary>
public sealed class WorkerStorage(NpgsqlDataSource dataSource)
    : IInboxMaintenance
{
    private static readonly PersistenceTableNames Names = new("worker_integration");
    private readonly PostgresInboxStore inbox = new(dataSource, Names);

    /// <summary>验证数据库连通性和关键 Inbox 表是否存在。</summary>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT to_regclass('worker_integration.platform_inbox') IS NOT NULL");
            return (bool?)await command.ExecuteScalarAsync(cancellationToken) == true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// 幂等应用一个事件。高版本已先到达时，迟到事件只完成 Inbox 并返回 Stale，
    /// 禁止旧状态覆盖新状态；同一事件再次投递返回 Duplicate。
    /// </summary>
    public async Task<ProjectionResult> ApplyAsync(
        string consumerName,
        string subject,
        ProjectionKind kind,
        EventEnvelope envelope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var begin = await inbox.TryBeginAsync(
            connection,
            transaction,
            envelope,
            consumerName,
            maximumSchemaVersion: 1,
            now,
            cancellationToken);
        if (begin == InboxBeginResult.DuplicateCompleted)
        {
            await transaction.CommitAsync(cancellationToken);
            return ProjectionResult.Duplicate;
        }
        if (begin != InboxBeginResult.Started)
        {
            throw new InvalidOperationException(
                $"Inbox 无法开始消费：{begin}。");
        }

        var accepted = await AdvanceCheckpointAsync(
            connection,
            transaction,
            consumerName,
            envelope,
            now,
            cancellationToken);
        if (accepted)
        {
            await ApplyProjectionAsync(
                connection,
                transaction,
                subject,
                kind,
                envelope,
                cancellationToken);
        }
        await inbox.CompleteAsync(
            connection,
            transaction,
            envelope.EventId,
            consumerName,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted ? ProjectionResult.Applied : ProjectionResult.Stale;
    }

    /// <summary>
    /// 记录需要人工判断的失败消息。只保存经过截断的错误摘要和受控事件载荷，
    /// 不保存异常堆栈、凭据、Join Ticket 或私有手牌。
    /// </summary>
    public async Task RecordFailureAsync(
        string consumerName,
        string subject,
        EventEnvelope? envelope,
        long deliveryCount,
        string errorCode,
        string errorSummary,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        var summary = errorSummary.Length <= 512
            ? errorSummary
            : errorSummary[..512];
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO worker_integration.failed_events(
                failure_id,event_id,subject,consumer_name,event_type,schema_version,
                delivery_count,error_code,error_summary,failed_at,status)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,'PendingReview')
            ON CONFLICT (consumer_name,subject,event_id) DO UPDATE
            SET delivery_count=GREATEST(
                    worker_integration.failed_events.delivery_count,
                    EXCLUDED.delivery_count),
                error_code=EXCLUDED.error_code,
                error_summary=EXCLUDED.error_summary,
                failed_at=EXCLUDED.failed_at
            """);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue((object?)envelope?.EventId.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(subject);
        command.Parameters.AddWithValue(consumerName);
        command.Parameters.AddWithValue((object?)envelope?.EventType ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)envelope?.SchemaVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(deliveryCount);
        command.Parameters.AddWithValue(errorCode);
        command.Parameters.AddWithValue(summary);
        command.Parameters.AddWithValue(failedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task<int> DeleteCompletedBeforeAsync(
        DateTimeOffset completedBefore,
        int limit,
        CancellationToken cancellationToken) =>
        inbox.DeleteCompletedBeforeAsync(
            completedBefore,
            limit,
            cancellationToken);

    private static async Task<bool> AdvanceCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string consumerName,
        EventEnvelope envelope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO worker_integration.projection_checkpoints(
                consumer_name,aggregate_type,aggregate_id,aggregate_version,event_id,updated_at)
            VALUES ($1,$2,$3,$4,$5,$6)
            ON CONFLICT (consumer_name,aggregate_type,aggregate_id) DO UPDATE
            SET aggregate_version=EXCLUDED.aggregate_version,
                event_id=EXCLUDED.event_id,
                updated_at=EXCLUDED.updated_at
            WHERE worker_integration.projection_checkpoints.aggregate_version
                  < EXCLUDED.aggregate_version
            RETURNING event_id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(consumerName);
        command.Parameters.AddWithValue(envelope.AggregateType);
        command.Parameters.AddWithValue(envelope.AggregateId);
        command.Parameters.AddWithValue(envelope.AggregateVersion);
        command.Parameters.AddWithValue(envelope.EventId.Value);
        command.Parameters.AddWithValue(now);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ApplyProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string subject,
        ProjectionKind kind,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var (sql, parameters) = kind switch
        {
            ProjectionKind.GameRecords => (
                """
                INSERT INTO worker_projection.game_records(
                    event_id,event_type,aggregate_id,aggregate_version,occurred_at,payload_json)
                VALUES ($1,$2,$3,$4,$5,$6::jsonb)
                ON CONFLICT (event_id) DO NOTHING
                """,
                new object[]
                {
                    envelope.EventId.Value, envelope.EventType, envelope.AggregateId,
                    envelope.AggregateVersion, envelope.OccurredAt, envelope.Payload.GetRawText()
                }),
            ProjectionKind.Leaderboard => (
                """
                INSERT INTO worker_projection.leaderboard_updates(
                    event_id,match_id,occurred_at,payload_json)
                VALUES ($1,$2,$3,$4::jsonb)
                ON CONFLICT (event_id) DO NOTHING
                """,
                new object[]
                {
                    envelope.EventId.Value, envelope.AggregateId,
                    envelope.OccurredAt, envelope.Payload.GetRawText()
                }),
            _ => (
                """
                INSERT INTO worker_projection.audit_events(
                    event_id,subject,event_type,aggregate_type,aggregate_id,occurred_at,
                    trace_id,correlation_id,payload_json)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb)
                ON CONFLICT (event_id) DO NOTHING
                """,
                new object[]
                {
                    envelope.EventId.Value, subject, envelope.EventType,
                    envelope.AggregateType, envelope.AggregateId, envelope.OccurredAt,
                    envelope.TraceId, envelope.CorrelationId.Value,
                    envelope.Payload.GetRawText()
                })
        };
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        for (var index = 0; index < parameters.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index]);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
