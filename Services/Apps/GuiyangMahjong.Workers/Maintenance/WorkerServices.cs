using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using GuiyangMahjong.Workers.Messaging;
using GuiyangMahjong.Workers.Options;
using GuiyangMahjong.Workers.Outbox;
using GuiyangMahjong.Workers.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Workers.Maintenance;

/// <summary>启动时声明 Stream 和 Durable Consumer；短期 NATS 中断按指数退避，不终止进程。</summary>
public sealed class JetStreamBootstrapWorker(
    JetStreamRuntime runtime,
    ILogger<JetStreamBootstrapWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested && !runtime.Initialized)
        {
            try
            {
                await runtime.EnsureInfrastructureAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                attempt++;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
                logger.LogWarning(
                    "JetStream 尚未就绪，Attempt={Attempt} RetrySeconds={RetrySeconds} ErrorType={ErrorType}",
                    attempt,
                    delay.TotalSeconds,
                    exception.GetType().Name);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}

/// <summary>并行运行三个独立 Durable Consumer；同一 Durable 可由多个 Pod 共享并水平扩展。</summary>
public sealed class ProjectionConsumersWorker(
    JetStreamRuntime runtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!runtime.Initialized)
        {
            await Task.Delay(250, stoppingToken);
        }
        await Task.WhenAll(WorkerConsumers.All.Select(
            definition => runtime.ConsumeAsync(definition, stoppingToken)));
    }
}

/// <summary>周期读取 Consumer Pending 并记录 Lag；超过阈值时输出结构化告警日志。</summary>
public sealed class MessageBacklogMonitorWorker(
    JetStreamRuntime runtime,
    IOptions<WorkersOptions> options,
    ILogger<MessageBacklogMonitorWorker> logger) : BackgroundService
{
    private static readonly Meter Meter = new("GuiyangMahjong.Workers", "1.0.0");
    // ObservableGauge 的回调与后台刷新并发执行，因此使用无锁快照安全的并发字典。
    private static readonly ConcurrentDictionary<string, long> Lag =
        new(StringComparer.Ordinal);
    private static readonly ObservableGauge<long> LagGauge = Meter.CreateObservableGauge(
        "mahjong_jetstream_consumer_lag",
        () => Lag.Select(item => new Measurement<long>(
            item.Value,
            new KeyValuePair<string, object?>("consumer", item.Key))));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = LagGauge;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (runtime.Initialized)
            {
                foreach (var consumer in WorkerConsumers.All)
                {
                    try
                    {
                        var lag = await runtime.GetLagAsync(consumer, stoppingToken);
                        Lag[consumer.Name] = lag;
                        if (lag >= options.Value.ConsumerLagWarningThreshold)
                        {
                            logger.LogWarning(
                                "JetStream Consumer 积压超过阈值，Consumer={Consumer} Lag={Lag} Threshold={Threshold}",
                                consumer.Name,
                                lag,
                                options.Value.ConsumerLagWarningThreshold);
                        }
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(
                            "Consumer Lag 查询失败，Consumer={Consumer} ErrorType={ErrorType}",
                            consumer.Name,
                            exception.GetType().Name);
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}

/// <summary>清理 Inbox 并归档已发布 Outbox；只处理超过显式保留期的数据。</summary>
public sealed class MessagingRetentionWorker(
    WorkerStorage storage,
    OutboxSourceRegistry sources,
    IOptions<WorkersOptions> options,
    TimeProvider timeProvider,
    ILogger<MessagingRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var inboxDeleted = await storage.DeleteCompletedBeforeAsync(
                now.AddDays(-options.Value.InboxRetentionDays),
                1000,
                stoppingToken);
            foreach (var source in sources.Sources)
            {
                var archived = await source.Store.ArchivePublishedAsync(
                    now.AddDays(-options.Value.OutboxArchiveAfterDays),
                    1000,
                    stoppingToken);
                if (archived > 0)
                {
                    logger.LogInformation(
                        "已归档 Outbox，Source={Source} Count={Count}",
                        source.Name,
                        archived);
                }
            }
            if (inboxDeleted > 0)
            {
                logger.LogInformation("已清理过期 Inbox，Count={Count}", inboxDeleted);
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

/// <summary>
/// 过期 Session 和房间清理调度器。它只调用数据所有者的幂等维护端点，
/// 不持有 Auth/Lobby 数据库账号；POST 失败不会在同一调用中执行无条件透明重试。
/// </summary>
public sealed class OwnershipMaintenanceWorker(
    IHttpClientFactory clients,
    IOptions<WorkersOptions> options,
    TimeProvider timeProvider,
    ILogger<OwnershipMaintenanceWorker> logger) : BackgroundService
{
    private DateTimeOffset nextSessionRun;
    private DateTimeOffset nextRoomRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            if (options.Value.SessionCleanup.Enabled && now >= nextSessionRun)
            {
                await InvokeAsync("session", options.Value.SessionCleanup, now, stoppingToken);
                nextSessionRun = now.AddSeconds(options.Value.SessionCleanup.IntervalSeconds);
            }
            if (options.Value.RoomCleanup.Enabled && now >= nextRoomRun)
            {
                await InvokeAsync("room", options.Value.RoomCleanup, now, stoppingToken);
                nextRoomRun = now.AddSeconds(options.Value.RoomCleanup.IntervalSeconds);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task InvokeAsync(
        string owner,
        MaintenanceOptions maintenance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, maintenance.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", maintenance.Token);
        // 同一时间窗固定幂等键，调度器崩溃恢复后不会重复清理或重复发出撤销事件。
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            $"maintenance-{owner}-{now.ToUnixTimeSeconds() / maintenance.IntervalSeconds}");
        try
        {
            using var response = await clients.CreateClient("maintenance")
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "数据所有者维护端点返回失败，Owner={Owner} StatusCode={StatusCode}",
                    owner,
                    (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "数据所有者维护调用失败，Owner={Owner} ErrorType={ErrorType}",
                owner,
                exception.GetType().Name);
        }
    }
}
