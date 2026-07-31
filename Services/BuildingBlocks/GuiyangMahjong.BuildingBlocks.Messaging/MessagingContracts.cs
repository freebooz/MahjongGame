using System.Collections.Concurrent;
using System.Text.Json;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;

namespace GuiyangMahjong.BuildingBlocks.Messaging;

/// <summary>Outbox 消息生命周期；只有 Published 可进入正常清理或归档。</summary>
public enum OutboxStatus
{
    Pending,
    Processing,
    Published,
    Failed
}

/// <summary>
/// 与业务事务同事务写入的 Outbox 实体。
/// PayloadJson 是完整版本化事件信封，不得保存 Token、Join Ticket 或私有手牌。
/// </summary>
public sealed record OutboxMessage(
    EventId EventId,
    string EventType,
    int SchemaVersion,
    string AggregateType,
    string AggregateId,
    long AggregateVersion,
    string PayloadJson,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt,
    OutboxStatus Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    string? LockOwner,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? PublishedAt,
    string? ErrorSummary)
{
    /// <summary>将事件信封转换为初始 Pending 消息，序列化失败时不得继续业务提交。</summary>
    public static OutboxMessage FromEnvelope(
        EventEnvelope envelope,
        DateTimeOffset createdAt,
        JsonSerializerOptions? serializerOptions = null) =>
        new(
            envelope.EventId,
            envelope.EventType,
            envelope.SchemaVersion,
            envelope.AggregateType,
            envelope.AggregateId,
            envelope.AggregateVersion,
            JsonSerializer.Serialize(
                envelope,
                serializerOptions
                ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            envelope.OccurredAt,
            createdAt,
            OutboxStatus.Pending,
            0,
            createdAt,
            null,
            null,
            null,
            null);
}

/// <summary>Inbox 消费状态；Processing 可由超时恢复，Completed 必须快速确认重复事件。</summary>
public enum InboxStatus
{
    Processing,
    Completed,
    Failed
}

/// <summary>Inbox 持久记录，以 ConsumerName + EventId 形成唯一消费身份。</summary>
public sealed record InboxMessage(
    string ConsumerName,
    EventId EventId,
    string EventType,
    int SchemaVersion,
    InboxStatus Status,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt,
    int FailureCount,
    string? ErrorSummary);

/// <summary>消费者开始处理事件时的 Inbox 判定。</summary>
public enum InboxBeginResult
{
    Started,
    DuplicateCompleted,
    AlreadyProcessing,
    UnsupportedSchema
}

/// <summary>生产消息发布器边界；实现不得对未知结果自动透明重试业务命令。</summary>
public interface IEventPublisher
{
    /// <summary>发布一个已从 Outbox 领取的事件；取消表示当前投递未确认。</summary>
    Task PublishAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken);
}

/// <summary>
/// 测试和本地开发消息发布器。
/// 只保存进程内事件副本，不注册网络监听，不代表 NATS 或任何生产消息系统。
/// </summary>
public sealed class InMemoryEventPublisher : IEventPublisher
{
    private readonly ConcurrentQueue<EventEnvelope> events = new();

    /// <summary>已经确认发布的事件快照。</summary>
    public IReadOnlyCollection<EventEnvelope> Events => events.ToArray();

    /// <inheritdoc/>
    public Task PublishAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Enqueue(envelope);
        return Task.CompletedTask;
    }
}

/// <summary>Outbox 后台发布和清理所需的持久化操作，不暴露业务表。</summary>
public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> MarkPublishedAsync(
        EventId eventId,
        string workerId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken);

    Task<bool> MarkFailedAsync(
        EventId eventId,
        string workerId,
        string errorSummary,
        DateTimeOffset nextAttemptAt,
        bool terminal,
        CancellationToken cancellationToken);

    Task<int> ArchivePublishedAsync(
        DateTimeOffset publishedBefore,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>Inbox 失败记录清理边界；正常去重保留期由消费服务的数据治理策略决定。</summary>
public interface IInboxMaintenance
{
    Task<int> DeleteCompletedBeforeAsync(
        DateTimeOffset completedBefore,
        int limit,
        CancellationToken cancellationToken);
}
