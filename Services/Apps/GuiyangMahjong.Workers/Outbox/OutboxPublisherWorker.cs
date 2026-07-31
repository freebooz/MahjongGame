using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.Contracts.Events;
using GuiyangMahjong.Workers.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Workers.Outbox;

/// <summary>
/// 多来源 Outbox 发布 Worker。领取使用 PostgreSQL SKIP LOCKED 和租约，可水平扩展；
/// NATS 不可用时只更新重试时间，业务事务中已保存的消息不会丢失。
/// </summary>
public sealed class OutboxPublisherWorker(
    OutboxSourceRegistry sources,
    IEventPublisher publisher,
    IOptions<WorkersOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter =
        new("GuiyangMahjong.Workers", "1.0.0");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "mahjong_outbox_publish_retries_total");
    private readonly string workerId =
        $"outbox-{Environment.MachineName}-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            foreach (var source in sources.Sources)
            {
                handled += await PublishSourceAsync(source, stoppingToken);
            }
            if (handled == 0)
            {
                await Task.Delay(
                    options.Value.PollIntervalMilliseconds,
                    stoppingToken);
            }
        }
    }

    private async Task<int> PublishSourceAsync(
        OutboxSource source,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OutboxMessage> messages;
        var now = timeProvider.GetUtcNow();
        try
        {
            messages = await source.Store.ClaimAsync(
                workerId,
                options.Value.OutboxBatchSize,
                now,
                TimeSpan.FromSeconds(options.Value.OutboxLeaseSeconds),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                "Outbox 领取失败，Source={Source} ErrorType={ErrorType}",
                source.Name,
                exception.GetType().Name);
            return 0;
        }

        foreach (var message in messages)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<EventEnvelope>(
                    message.PayloadJson,
                    JsonOptions)
                    ?? throw new InvalidDataException("Outbox 事件信封为空。");
                ValidateEnvelope(message, envelope);
                await publisher.PublishAsync(envelope, cancellationToken);
                if (!await source.Store.MarkPublishedAsync(
                        message.EventId,
                        workerId,
                        timeProvider.GetUtcNow(),
                        cancellationToken))
                {
                    // 发布确认后标记失败属于未知结果；租约到期后使用相同 MsgId 重发，由 JetStream 和 Inbox 去重。
                    throw new InvalidOperationException("Outbox 发布标记租约已丢失。");
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var terminal = message.AttemptCount
                    >= options.Value.MaximumPublishAttempts
                    || exception is InvalidDataException
                    || exception is JsonException;
                var nextAttempt = timeProvider.GetUtcNow()
                    + RetryDelay(message.AttemptCount);
                _ = await source.Store.MarkFailedAsync(
                    message.EventId,
                    workerId,
                    exception.Message,
                    nextAttempt,
                    terminal,
                    cancellationToken);
                Retries.Add(
                    1,
                    new TagList
                    {
                        { "source", source.Name },
                        { "terminal", terminal }
                    });
                logger.LogWarning(
                    "Outbox 发布失败，Source={Source} EventId={EventId} Attempt={Attempt} Terminal={Terminal} ErrorType={ErrorType}",
                    source.Name,
                    message.EventId.Value,
                    message.AttemptCount,
                    terminal,
                    exception.GetType().Name);
            }
        }
        return messages.Count;
    }

    private static void ValidateEnvelope(
        OutboxMessage message,
        EventEnvelope envelope)
    {
        if (message.EventId != envelope.EventId
            || !string.Equals(message.EventType, envelope.EventType, StringComparison.Ordinal)
            || message.SchemaVersion != envelope.SchemaVersion
            || !string.Equals(message.AggregateId, envelope.AggregateId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Outbox 列与事件信封不一致。");
        }
        _ = PlatformEventSubjects.Resolve(
            envelope.EventType,
            envelope.SchemaVersion);
    }

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Clamp(attempt, 1, 8))));
}
