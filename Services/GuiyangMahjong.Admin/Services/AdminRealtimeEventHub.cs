using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>Admin SSE 增量事件，Sequence 在单实例生命周期内严格递增，EntityKey 用于客户端幂等覆盖。</summary>
public sealed record AdminRealtimeEvent(
    long Sequence,
    string EventType,
    string EntityKey,
    JsonElement Payload,
    DateTimeOffset OccurredAtUtc);

/// <summary>SSE 订阅句柄；需要重同步表示断点早于服务端环形窗口，客户端不得猜测缺失状态。</summary>
public sealed record AdminRealtimeSubscription(
    ChannelReader<AdminRealtimeEvent> Reader,
    AdminRealtimeEvent[] Backlog,
    bool RequiresResync,
    long CurrentSequence,
    Action Dispose);

/// <summary>
/// 有界 SSE 事件中心。环形窗口支持断线续传；慢订阅者队列满时会被关闭，避免拖垮管理服务。
/// </summary>
public sealed class AdminRealtimeEventHub : IDisposable
{
    private static readonly Meter Meter = new(
        "GuiyangMahjong.Admin.Realtime",
        "1.0.0");
    private static readonly Counter<long> PublishedCounter =
        Meter.CreateCounter<long>("mahjong.admin.realtime.events.published");
    private static readonly Counter<long> ResyncCounter =
        Meter.CreateCounter<long>("mahjong.admin.realtime.resync");
    private static readonly UpDownCounter<long> Connections =
        Meter.CreateUpDownCounter<long>("mahjong.admin.realtime.connections");

    private readonly object gate = new();
    private readonly int backlogLimit;
    private readonly int subscriberQueueLimit;
    private readonly LinkedList<AdminRealtimeEvent> backlog = [];
    private readonly Dictionary<Guid, Channel<AdminRealtimeEvent>> subscribers = [];
    private long sequence;
    private readonly string instanceId = Guid.NewGuid().ToString("N");

    public AdminRealtimeEventHub(IOptions<AdminOptions> options)
    {
        backlogLimit = options.Value.RealtimeCapacity.EventBacklogLimit;
        subscriberQueueLimit =
            options.Value.RealtimeCapacity.SubscriberQueueLimit;
    }

    /// <summary>当前已发布序列，用于客户端在初始分页快照前建立可续传水位。</summary>
    public long CurrentSequence => Volatile.Read(ref sequence);

    /// <summary>当前进程事件水位；包含实例前缀，跨副本重连时可确定触发受控重同步。</summary>
    public string CurrentEventId => FormatEventId(CurrentSequence);

    /// <summary>把进程内序列编码为 SSE Last-Event-ID，不暴露节点地址或部署拓扑。</summary>
    public string FormatEventId(long value) => $"{instanceId}:{value}";

    /// <summary>解析当前实例水位；来自其他副本或损坏的 ID 返回 false，由调用方发出 resync。</summary>
    public bool TryParseEventId(string value, out long sequenceValue)
    {
        sequenceValue = 0;
        var separator = value.IndexOf(':');
        return separator > 0
            && string.Equals(
                value[..separator],
                instanceId,
                StringComparison.Ordinal)
            && long.TryParse(value[(separator + 1)..], out sequenceValue)
            && sequenceValue >= 0;
    }

    /// <summary>发布已脱敏实体增量；写入环形窗口后再分发，保证断线客户端可从同一序列恢复。</summary>
    public AdminRealtimeEvent Publish(
        string eventType,
        string entityKey,
        object payload,
        DateTimeOffset occurredAtUtc)
    {
        var realtimeEvent = new AdminRealtimeEvent(
            Interlocked.Increment(ref sequence),
            eventType,
            entityKey,
            JsonSerializer.SerializeToElement(payload),
            occurredAtUtc);
        lock (gate)
        {
            backlog.AddLast(realtimeEvent);
            while (backlog.Count > backlogLimit) backlog.RemoveFirst();
            foreach (var subscriber in subscribers.ToArray())
            {
                if (subscriber.Value.Writer.TryWrite(realtimeEvent)) continue;
                // 队列已满说明客户端处理速度无法跟上；关闭后由 Last-Event-ID 续传或受控重同步。
                subscriber.Value.Writer.TryComplete();
                subscribers.Remove(subscriber.Key);
                Connections.Add(-1);
            }
        }
        PublishedCounter.Add(1, new KeyValuePair<string, object?>(
            "event.type", eventType));
        return realtimeEvent;
    }

    /// <summary>从 afterSequence 建立订阅；断点超出窗口时只返回重同步信号，不发送不完整积压。</summary>
    public AdminRealtimeSubscription Subscribe(
        long? afterSequence,
        bool forceResync = false)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<AdminRealtimeEvent>(
            new BoundedChannelOptions(subscriberQueueLimit)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        lock (gate)
        {
            var current = Volatile.Read(ref sequence);
            var oldest = backlog.First?.Value.Sequence ?? current + 1;
            var requiresResync = forceResync
                || (afterSequence.HasValue
                    && afterSequence.Value < oldest - 1);
            var replay = requiresResync
                ? []
                : backlog
                    .Where(item => item.Sequence > (afterSequence ?? current))
                    .ToArray();
            subscribers[id] = channel;
            Connections.Add(1);
            if (requiresResync) ResyncCounter.Add(1);
            return new AdminRealtimeSubscription(
                channel.Reader,
                replay,
                requiresResync,
                current,
                () => RemoveSubscriber(id));
        }
    }

    private void RemoveSubscriber(Guid id)
    {
        lock (gate)
        {
            if (!subscribers.Remove(id, out var channel)) return;
            channel.Writer.TryComplete();
            Connections.Add(-1);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var channel in subscribers.Values)
                channel.Writer.TryComplete();
            subscribers.Clear();
        }
    }
}

/// <summary>
/// 将聚合快照转换成 upsert/remove 增量。它集中承担下游分页扫描，浏览器数量增加不会线性放大全量轮询。
/// </summary>
public sealed class AdminRealtimeSnapshotPublisher(
    AdminRealtimeEventHub hub,
    MonitoringAggregationService rooms,
    PlayerMonitoringService players,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminRealtimeSnapshotPublisher> logger) : BackgroundService
{
    private readonly Dictionary<string, string> roomHashes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> playerHashes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> instanceHashes =
        new(StringComparer.Ordinal);
    private string? overviewHash;
    private bool roomSnapshotInitialized;
    private bool playerSnapshotInitialized;
    private bool instanceSnapshotInitialized;
    private string? playerScanCursor;
    private readonly HashSet<string> playerSweepKeys =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> playerScanCursors =
        new(StringComparer.Ordinal);

    /// <summary>按配置间隔生成快照；单轮失败只记录并重试，不清空已有客户端状态。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.RealtimeCapacity;
        if (!settings.SseEnabled) return;
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(settings.SnapshotIntervalSeconds));
        do
        {
            try
            {
                var roomItems = await rooms.ListRoomsForRealtimeAsync(
                    stoppingToken);
                var instanceItems = await rooms.ListInstancesForRealtimeAsync(
                    stoppingToken);
                PublishDiff(
                    "room",
                    roomItems,
                    item => item.RoomId,
                    roomHashes,
                    ref roomSnapshotInitialized);
                PublishDiff(
                    "instance",
                    instanceItems,
                    item => item.Instance.ServerInstanceId,
                    instanceHashes,
                    ref instanceSnapshotInitialized);
                // 概览只依赖房间和实例，优先发布可避免大玩家目录扫描阻塞 SSE 首个事件。
                var overview = new
                {
                    totalRooms = roomItems.Count,
                    activeRooms = roomItems.Count(item =>
                        item.Lifecycle is
                            "Allocating" or "Waiting" or "Playing" or "Settling"),
                    abnormalRooms = roomItems.Count(item =>
                        item.Lifecycle == "Failed")
                        + instanceItems.Count(item =>
                            item.Instance.State == "Failed"),
                    totalConnectedPlayers = roomItems
                        .Where(item => item.Lifecycle is not "Closed" and not "Failed")
                        .Sum(item => item.PlayerCount),
                    dedicatedServerInstances = instanceItems.Count
                };
                var nextOverviewHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(overview)));
                if (!string.Equals(
                        overviewHash,
                        nextOverviewHash,
                        StringComparison.Ordinal))
                {
                    overviewHash = nextOverviewHash;
                    hub.Publish(
                        "overview.upsert",
                        "overview",
                        overview,
                        timeProvider.GetUtcNow());
                }
                await PublishNextPlayerSliceAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Admin 实时增量快照生成失败，将在下一周期重试。");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void PublishDiff<T>(
        string entityType,
        IReadOnlyCollection<T> items,
        Func<T, string> keySelector,
        Dictionary<string, string> hashes,
        ref bool snapshotInitialized)
    {
        var now = timeProvider.GetUtcNow();
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = keySelector(item);
            currentKeys.Add(key);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(item)));
            if (hashes.TryGetValue(key, out var previous)
                && string.Equals(previous, hash, StringComparison.Ordinal))
                continue;
            hashes[key] = hash;
            // 首轮仅建立比较基线；浏览器已经通过分页快照获得当前状态，禁止向每个订阅者倾倒全量事件。
            if (snapshotInitialized)
                hub.Publish($"{entityType}.upsert", key, item!, now);
        }
        if (snapshotInitialized)
        {
            foreach (var removed in hashes.Keys
                         .Where(key => !currentKeys.Contains(key))
                         .ToArray())
            {
                hashes.Remove(removed);
                hub.Publish(
                    $"{entityType}.remove",
                    removed,
                    new { id = removed },
                    now);
            }
        }
        snapshotInitialized = true;
    }

    /// <summary>
    /// 以固定页预算推进玩家目录扫描，平滑 10 万玩家规模下的 CPU、内存和内部网络流量。
    /// 只有完整遍历结束才判断删除，避免把尚未扫描到的玩家误报为离线或删除。
    /// </summary>
    private async Task PublishNextPlayerSliceAsync(
        RealtimeCapacityOptions settings,
        CancellationToken cancellationToken)
    {
        for (var pageIndex = 0;
             pageIndex < settings.PlayerPagesPerSnapshotCycle;
             pageIndex++)
        {
            var page = await players.ListPlayersAsync(
                null,
                playerScanCursor,
                settings.MaximumPageSize,
                cancellationToken);
            var now = timeProvider.GetUtcNow();
            foreach (var item in page.Items)
            {
                playerSweepKeys.Add(item.PlayerId);
                var hash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(item)));
                var changed = !playerHashes.TryGetValue(
                    item.PlayerId,
                    out var previous)
                    || !string.Equals(previous, hash, StringComparison.Ordinal);
                playerHashes[item.PlayerId] = hash;
                if (playerSnapshotInitialized && changed)
                    hub.Publish("player.upsert", item.PlayerId, item, now);
            }

            var nextCursor = page.NextCursor;
            if (nextCursor is not null
                && !playerScanCursors.Add(nextCursor))
            {
                throw new InvalidOperationException(
                    "Auth returned a repeated player cursor during realtime scan.");
            }
            playerScanCursor = nextCursor;
            if (playerScanCursor is not null)
                continue;

            if (playerSnapshotInitialized)
            {
                foreach (var removed in playerHashes.Keys
                             .Where(key => !playerSweepKeys.Contains(key))
                             .ToArray())
                {
                    playerHashes.Remove(removed);
                    hub.Publish(
                        "player.remove",
                        removed,
                        new { id = removed },
                        now);
                }
            }
            playerSnapshotInitialized = true;
            playerSweepKeys.Clear();
            playerScanCursors.Clear();
            break;
        }
    }
}
