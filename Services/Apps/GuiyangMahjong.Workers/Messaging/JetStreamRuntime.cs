using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.Contracts.Events;
using GuiyangMahjong.Workers.Options;
using GuiyangMahjong.Workers.Storage;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Client.Core;
using NATS.Net;

namespace GuiyangMahjong.Workers.Messaging;

/// <summary>一个 Durable Consumer 的稳定名称、Subject 白名单和投影职责。</summary>
public sealed record ConsumerDefinition(
    string Name,
    IReadOnlyCollection<string> Subjects,
    ProjectionKind ProjectionKind);

/// <summary>
/// Workers 使用的三个独立消费视图。共享 Stream 但不共享 Inbox 身份，
/// 因此审计、战绩和排行榜可分别扩容、暂停或恢复。
/// </summary>
public static class WorkerConsumers
{
    public static readonly ConsumerDefinition GameRecords = new(
        "game-record-projection-v1",
        ["match.finished.v1", "settlement.committed.v1"],
        ProjectionKind.GameRecords);

    public static readonly ConsumerDefinition Leaderboard = new(
        "leaderboard-projection-v1",
        ["settlement.committed.v1"],
        ProjectionKind.Leaderboard);

    public static readonly ConsumerDefinition Audit = new(
        "audit-projection-v1",
        PlatformEventSubjects.All,
        ProjectionKind.Audit);

    public static IReadOnlyCollection<ConsumerDefinition> All =>
        [GameRecords, Leaderboard, Audit];
}

/// <summary>
/// NATS JetStream 运行时：声明 Stream/Durable Consumer、执行显式 ACK/NAK/TERM，
/// 并把 Consumer Pending 暴露为低基数指标。业务去重由 PostgreSQL Inbox 完成。
/// </summary>
public sealed class JetStreamRuntime : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly ActivitySource ActivitySource =
        new("GuiyangMahjong.Workers", "1.0.0");
    private static readonly Meter Meter =
        new("GuiyangMahjong.Workers", "1.0.0");
    private static readonly Counter<long> Consumed = Meter.CreateCounter<long>(
        "mahjong_worker_consumed_total");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>(
        "mahjong_worker_duplicate_total");
    private static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>(
        "mahjong_worker_dead_letter_total");
    private static readonly Histogram<double> HandlerDuration = Meter.CreateHistogram<double>(
        "mahjong_worker_handler_duration_ms",
        unit: "ms");

    private readonly WorkersOptions options;
    private readonly WorkerStorage storage;
    private readonly ILogger<JetStreamRuntime> logger;
    private readonly TimeProvider timeProvider;
    private readonly NatsClient client;
    private readonly INatsJSContext jetStream;
    private volatile bool initialized;

    public JetStreamRuntime(
        IOptions<WorkersOptions> options,
        WorkerStorage storage,
        ILogger<JetStreamRuntime> logger,
        TimeProvider timeProvider)
    {
        this.options = options.Value;
        this.storage = storage;
        this.logger = logger;
        this.timeProvider = timeProvider;
        client = new NatsClient(new NatsOpts
        {
            Url = this.options.NatsUrl,
            Name = "guiyang-mahjong-workers",
            // 凭据独立于 URL，防止连接异常把敏感值带入结构化日志。
            AuthOpts = string.IsNullOrWhiteSpace(this.options.NatsUsername)
                ? NatsAuthOpts.Default
                : new NatsAuthOpts
                {
                    Username = this.options.NatsUsername,
                    Password = this.options.NatsPassword
                }
        });
        jetStream = client.CreateJetStreamContext();
    }

    /// <summary>只有数据库和 NATS 均可用且 Stream/Consumer 已声明时才视为就绪。</summary>
    public bool Initialized => initialized;

    /// <summary>
    /// 幂等声明 Stream 和 Durable Consumer。生产副本数来自显式配置；
    /// 本地单节点不得把副本数伪装成3。
    /// </summary>
    public async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        await client.ConnectAsync();
        var streamConfig = new StreamConfig(
            PlatformEventSubjects.StreamName,
            PlatformEventSubjects.All
                .Append(PlatformEventSubjects.DeadLetterSubject)
                .ToArray())
        {
            Description = "贵阳麻将首批版本化平台事件与人工失败流",
            Storage = StreamConfigStorage.File,
            NumReplicas = options.StreamReplicas,
            MaxAge = TimeSpan.FromDays(options.StreamRetentionDays),
            MaxBytes = options.StreamMaxBytes,
            MaxMsgSize = 1024 * 1024,
            DuplicateWindow = TimeSpan.FromMinutes(10)
        };
        var stream = await jetStream.CreateOrUpdateStreamAsync(
            streamConfig,
            cancellationToken);
        foreach (var definition in WorkerConsumers.All)
        {
            await stream.CreateOrUpdateConsumerAsync(
                new ConsumerConfig(definition.Name)
                {
                    DurableName = definition.Name,
                    FilterSubjects = definition.Subjects.ToList(),
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    AckWait = TimeSpan.FromSeconds(30),
                    MaxDeliver = options.MaximumPublishAttempts,
                    MaxAckPending = 1000
                },
                cancellationToken);
        }
        initialized = true;
        logger.LogInformation(
            "JetStream 基础设施已就绪，Stream={StreamName}，Replicas={Replicas}",
            PlatformEventSubjects.StreamName,
            options.StreamReplicas);
    }

    /// <summary>执行 NATS PING；健康检查不读取或记录任何认证配置。</summary>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await client.PingAsync(cancellationToken);
            return initialized;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// 持续消费一个 Durable Consumer。处理成功后显式 ACK；临时失败 NAK 并指数延迟；
    /// 不支持的 Schema、Subject 伪装和达到最大次数的毒消息写入人工失败表后 TERM。
    /// </summary>
    public async Task ConsumeAsync(
        ConsumerDefinition definition,
        CancellationToken cancellationToken)
    {
        var consumer = await jetStream.CreateOrUpdateConsumerAsync(
            PlatformEventSubjects.StreamName,
            new ConsumerConfig(definition.Name)
            {
                DurableName = definition.Name,
                FilterSubjects = definition.Subjects.ToList(),
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = TimeSpan.FromSeconds(30),
                MaxDeliver = options.MaximumPublishAttempts,
                MaxAckPending = 1000
            },
            cancellationToken);
        await foreach (var message in consumer
                           .ConsumeAsync<byte[]>(cancellationToken: cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            var started = Stopwatch.GetTimestamp();
            var rawDelivered = message.Metadata?.NumDelivered ?? 1;
            var delivered = rawDelivered > long.MaxValue
                ? long.MaxValue
                : (long)rawDelivered;
            EventEnvelope? envelope = null;
            try
            {
                message.EnsureSuccess();
                envelope = JsonSerializer.Deserialize<EventEnvelope>(
                    message.Data,
                    JsonOptions)
                    ?? throw new InvalidDataException("事件信封为空。");
                if (envelope.SchemaVersion != 1)
                {
                    throw new UnsupportedEventSchemaException(
                        envelope.EventType,
                        envelope.SchemaVersion);
                }
                if (!PlatformEventSubjects.Matches(
                        message.Subject,
                        envelope.EventType,
                        envelope.SchemaVersion))
                {
                    throw new InvalidDataException("Subject 与事件信封不一致。");
                }

                using var activity = StartConsumerActivity(
                    definition,
                    message.Subject,
                    envelope);
                var result = await storage.ApplyAsync(
                    definition.Name,
                    message.Subject,
                    definition.ProjectionKind,
                    envelope,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                await message.AckAsync(cancellationToken: cancellationToken);
                Consumed.Add(1, Tags(definition.Name, message.Subject));
                if (result == ProjectionResult.Duplicate)
                {
                    Duplicates.Add(1, Tags(definition.Name, message.Subject));
                }
            }
            catch (Exception exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                var terminal = exception is UnsupportedEventSchemaException
                    or InvalidDataException
                    or JsonException
                    || delivered >= options.MaximumPublishAttempts;
                if (terminal)
                {
                    await storage.RecordFailureAsync(
                        definition.Name,
                        message.Subject,
                        envelope,
                        delivered,
                        FailureCode(exception),
                        exception.Message,
                        timeProvider.GetUtcNow(),
                        cancellationToken);
                    await message.AckTerminateAsync(cancellationToken: cancellationToken);
                    DeadLetters.Add(1, Tags(definition.Name, message.Subject));
                    logger.LogError(
                        "事件进入人工失败处理，Consumer={Consumer} Subject={Subject} EventId={EventId} Deliveries={Deliveries} Code={Code}",
                        definition.Name,
                        message.Subject,
                        envelope?.EventId.Value,
                        delivered,
                        FailureCode(exception));
                }
                else
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, delivered)));
                    await message.NakAsync(delay, cancellationToken);
                    logger.LogWarning(
                        "事件处理暂时失败，Consumer={Consumer} Subject={Subject} EventId={EventId} Deliveries={Deliveries} RetrySeconds={RetrySeconds}",
                        definition.Name,
                        message.Subject,
                        envelope?.EventId.Value,
                        delivered,
                        delay.TotalSeconds);
                }
            }
            finally
            {
                HandlerDuration.Record(
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Tags(definition.Name, message.Subject));
            }
        }
    }

    /// <summary>读取 Durable Consumer 的服务端 Pending 数量，用于 Lag 指标和告警。</summary>
    public async Task<long> GetLagAsync(
        ConsumerDefinition definition,
        CancellationToken cancellationToken)
    {
        var consumer = await jetStream.GetConsumerAsync(
            PlatformEventSubjects.StreamName,
            definition.Name,
            cancellationToken);
        await consumer.RefreshAsync(cancellationToken);
        return consumer.Info.NumPending > long.MaxValue
            ? long.MaxValue
            : (long)consumer.Info.NumPending;
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();

    private static Activity? StartConsumerActivity(
        ConsumerDefinition definition,
        string subject,
        EventEnvelope envelope)
    {
        ActivityContext parent = default;
        if (envelope.TraceId.Length == 32
            && ActivityContext.TryParse(
                $"00-{envelope.TraceId}-0000000000000001-01",
                null,
                out var parsedParent))
        {
            parent = parsedParent;
        }
        var activity = ActivitySource.StartActivity(
            "jetstream consume",
            ActivityKind.Consumer,
            parent);
        activity?.SetTag("messaging.system", "nats");
        activity?.SetTag("messaging.destination.name", subject);
        activity?.SetTag("messaging.consumer.name", definition.Name);
        activity?.SetTag("messaging.message.id", envelope.EventId.Value);
        activity?.SetTag("mahjong.correlation_id", envelope.CorrelationId.Value);
        return activity;
    }

    private static TagList Tags(string consumer, string subject) =>
        new() { { "consumer", consumer }, { "subject", subject } };

    private static string FailureCode(Exception exception) => exception switch
    {
        UnsupportedEventSchemaException => "UNSUPPORTED_SCHEMA",
        JsonException => "INVALID_JSON",
        InvalidDataException => "INVALID_ENVELOPE",
        _ => "MAX_DELIVERY_EXCEEDED"
    };
}

/// <summary>消费者无法理解的未来 Schema；必须进入人工处理，不能无限重试。</summary>
public sealed class UnsupportedEventSchemaException(
    string eventType,
    int schemaVersion)
    : Exception($"不支持事件 Schema：{eventType} v{schemaVersion}。")
{
}
