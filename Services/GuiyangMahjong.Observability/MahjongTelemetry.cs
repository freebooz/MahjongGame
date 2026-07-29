using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;

namespace GuiyangMahjong.Observability;

/// <summary>
/// 贵阳麻将服务端统一的低基数 ActivitySource 与 Meter；禁止把 PlayerId、RoomId 放入指标标签。
/// ServerInstanceId 只允许用于带淘汰上限的“最后心跳”Gauge，以支持单实例丢失告警。
/// </summary>
public static class MahjongTelemetry
{
    /// <summary>OpenTelemetry ActivitySource 名称。</summary>
    public const string ActivitySourceName = "GuiyangMahjong.Services";

    /// <summary>OpenTelemetry Meter 名称。</summary>
    public const string MeterName = "GuiyangMahjong.Services";

    /// <summary>供关键内部操作创建子跨度。</summary>
    public static readonly ActivitySource ActivitySource =
        new(ActivitySourceName, "1.0.0");

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> RequestCounter =
        Meter.CreateCounter<long>("mahjong_http_server_requests");
    private static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(
            "mahjong_http_server_duration",
            "ms");
    private static readonly Counter<long> RequestFailureCounter =
        Meter.CreateCounter<long>("mahjong_http_server_failures");
    private static readonly Counter<long> HeartbeatCounter =
        Meter.CreateCounter<long>("mahjong_room_heartbeat_received");
    private static readonly ConcurrentDictionary<string, InstanceHeartbeatState>
        InstanceHeartbeats = new(StringComparer.Ordinal);
    private static long lastInstanceHeartbeatPruneUnixSeconds;
    private static readonly ObservableGauge<double> InstanceHeartbeatLastSeen =
        Meter.CreateObservableGauge(
            "mahjong_room_heartbeat_last_seen_seconds",
            ObserveInstanceHeartbeats,
            "s");
    private static readonly ObservableGauge<long> ActiveRooms =
        Meter.CreateObservableGauge(
            "mahjong_rooms_active",
            ObserveActiveRooms);
    private static readonly ObservableGauge<long> CurrentConnectedPlayers =
        Meter.CreateObservableGauge(
            "mahjong_players_connected",
            ObserveConnectedPlayers);
    private static readonly Histogram<double> ServerTickDuration =
        Meter.CreateHistogram<double>(
            "mahjong_dedicated_server_tick",
            "ms");
    private static readonly Histogram<double> ServerFramesPerSecond =
        Meter.CreateHistogram<double>("mahjong_dedicated_server_fps");
    private static readonly Histogram<double> ServerCpuPercent =
        Meter.CreateHistogram<double>("mahjong_dedicated_server_cpu_percent");
    private static readonly Histogram<long> ServerMemoryBytes =
        Meter.CreateHistogram<long>("mahjong_dedicated_server_memory_bytes");
    private static readonly Histogram<double> ServerNetworkIngress =
        Meter.CreateHistogram<double>(
            "mahjong_dedicated_server_network_ingress_bytes_per_second");
    private static readonly Histogram<double> ServerNetworkEgress =
        Meter.CreateHistogram<double>(
            "mahjong_dedicated_server_network_egress_bytes_per_second");
    private static readonly Histogram<int> ConnectedPlayers =
        Meter.CreateHistogram<int>("mahjong_room_connected_players");
    private static readonly Counter<long> RpcCounter =
        Meter.CreateCounter<long>("mahjong_dedicated_server_rpc_received");
    private static readonly Counter<long> DisconnectCounter =
        Meter.CreateCounter<long>("mahjong_player_disconnects");
    private static readonly Histogram<int> AdminCommandBatch =
        Meter.CreateHistogram<int>("mahjong_admin_command_claimed_batch");
    private static readonly Counter<long> AdminCommandOutcome =
        Meter.CreateCounter<long>("mahjong_admin_command_outcomes");
    private static readonly Histogram<int> AuditArchiveBatch =
        Meter.CreateHistogram<int>("mahjong_audit_archive_claimed_batch");
    private static readonly Counter<long> AuditArchiveOutcome =
        Meter.CreateCounter<long>("mahjong_audit_archive_outcomes");
    private static readonly Histogram<double> TelemetryFreshness =
        Meter.CreateHistogram<double>(
            "mahjong_telemetry_freshness",
            "s");
    private static readonly Histogram<double> AdminApprovalToStart =
        Meter.CreateHistogram<double>(
            "mahjong_admin_command_approval_to_start",
            "s");
    private static readonly Histogram<double> AuditArchiveLatency =
        Meter.CreateHistogram<double>(
            "mahjong_audit_archive_latency",
            "s");
    private static readonly Counter<long> AuditChainAnchorOutcome =
        Meter.CreateCounter<long>("mahjong_audit_chain_anchor_outcomes");
    private static readonly Counter<long> AdminAbacDecision =
        Meter.CreateCounter<long>("mahjong_admin_abac_decisions");
    private static readonly Counter<long> AdminBreakGlass =
        Meter.CreateCounter<long>("mahjong_admin_break_glass_uses");

    /// <summary>
    /// 获取当前请求的业务 TraceId；下游 HTTP 客户端用它填充 X-Trace-Id，
    /// 技术 traceparent 则由 OpenTelemetry HttpClient instrumentation 自动传播。
    /// </summary>
    public static string CurrentBusinessTraceId =>
        Activity.Current?.GetBaggageItem("mahjong.business_trace_id")
        ?? Activity.Current?.TraceId.ToString()
        ?? Guid.NewGuid().ToString();

    /// <summary>
    /// 记录 HTTP RED 指标；标签只包含服务、方法、受控路由和状态码类别，避免业务 ID 导致高基数。
    /// </summary>
    public static void RecordHttpRequest(
        string service,
        string method,
        string route,
        int statusCode,
        double durationMilliseconds)
    {
        var tags = new TagList
        {
            { "service", service },
            { "method", method },
            { "route", route },
            { "status_class", $"{statusCode / 100}xx" }
        };
        RequestCounter.Add(1, tags);
        RequestDuration.Record(durationMilliseconds, tags);
        if (statusCode >= 500) RequestFailureCounter.Add(1, tags);
    }

    /// <summary>
    /// 记录一次已通过契约校验的 Dedicated Server 心跳。
    /// 业务标识只保留在当前跨度中供 exemplar/Trace 跳转，指标标签仅使用受控生命周期和构建版本。
    /// </summary>
    public static void RecordRoomHeartbeat(
        string serverInstanceId,
        string lifecycle,
        string buildVersion,
        int connectedPlayers,
        double? tickMilliseconds,
        double? framesPerSecond,
        double? cpuPercent,
        long? memoryBytes,
        double? ingressBytesPerSecond,
        double? egressBytesPerSecond,
        long rpcDelta,
        int disconnectDelta)
    {
        var now = DateTimeOffset.UtcNow;
        InstanceHeartbeats[serverInstanceId] = new InstanceHeartbeatState(
            now.ToUnixTimeSeconds(),
            NormalizeBoundedTag(lifecycle),
            NormalizeBoundedTag(buildVersion),
            connectedPlayers,
            now);
        PruneInstanceHeartbeats(now);
        var tags = new TagList
        {
            { "lifecycle", NormalizeBoundedTag(lifecycle) },
            { "build_version", NormalizeBoundedTag(buildVersion) }
        };
        HeartbeatCounter.Add(1, tags);
        ConnectedPlayers.Record(connectedPlayers, tags);
        if (tickMilliseconds is { } tick) ServerTickDuration.Record(tick, tags);
        if (framesPerSecond is { } fps) ServerFramesPerSecond.Record(fps, tags);
        if (cpuPercent is { } cpu) ServerCpuPercent.Record(cpu, tags);
        if (memoryBytes is { } memory) ServerMemoryBytes.Record(memory, tags);
        if (ingressBytesPerSecond is { } ingress) ServerNetworkIngress.Record(ingress, tags);
        if (egressBytesPerSecond is { } egress) ServerNetworkEgress.Record(egress, tags);
        if (rpcDelta > 0) RpcCounter.Add(rpcDelta, tags);
        if (disconnectDelta > 0) DisconnectCounter.Add(disconnectDelta, tags);
    }

    /// <summary>记录管理命令批次和结果；actionType 必须来自服务端枚举，禁止传入目标 ID。</summary>
    public static void RecordAdminCommandBatch(int count) =>
        AdminCommandBatch.Record(count);

    /// <summary>记录管理命令成功、重试或失败结果，供积压和失败率告警使用。</summary>
    public static void RecordAdminCommandOutcome(
        string actionType,
        string outcome) =>
        AdminCommandOutcome.Add(
            1,
            new TagList
            {
                { "action_type", NormalizeBoundedTag(actionType) },
                { "outcome", NormalizeBoundedTag(outcome) }
            });

    /// <summary>记录不可变审计归档领取批次，零值可用于识别空闲轮询。</summary>
    public static void RecordAuditArchiveBatch(int count) =>
        AuditArchiveBatch.Record(count);

    /// <summary>记录审计归档成功、重试或终止结果。</summary>
    public static void RecordAuditArchiveOutcome(string outcome) =>
        AuditArchiveOutcome.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                NormalizeBoundedTag(outcome)));

    /// <summary>记录 Dedicated Server 观测时间到 Lobby 接收时间的新鲜度，负时钟偏差按零处理。</summary>
    public static void RecordTelemetryFreshness(
        DateTimeOffset observedAtUtc,
        DateTimeOffset receivedAtUtc) =>
        TelemetryFreshness.Record(
            Math.Max(0, (receivedAtUtc - observedAtUtc).TotalSeconds));

    /// <summary>记录独立审批完成到命令首次开始执行的延迟。</summary>
    public static void RecordAdminApprovalToStart(
        DateTimeOffset approvedAtUtc,
        DateTimeOffset startedAtUtc,
        string actionType) =>
        AdminApprovalToStart.Record(
            Math.Max(0, (startedAtUtc - approvedAtUtc).TotalSeconds),
            new KeyValuePair<string, object?>(
                "action_type",
                NormalizeBoundedTag(actionType)));

    /// <summary>记录审计发生到 WORM/SIEM 确认归档的端到端延迟。</summary>
    public static void RecordAuditArchiveLatency(
        DateTimeOffset occurredAtUtc,
        DateTimeOffset archivedAtUtc) =>
        AuditArchiveLatency.Record(
            Math.Max(0, (archivedAtUtc - occurredAtUtc).TotalSeconds));

    /// <summary>记录审计链校验及外部锚定结果；结果值来自受控枚举。</summary>
    public static void RecordAuditChainAnchorOutcome(string outcome) =>
        AuditChainAnchorOutcome.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                NormalizeBoundedTag(outcome)));

    /// <summary>记录 ABAC 策略决策；标签仅包含受控策略名和结果，不写入人员、地域或案件标识。</summary>
    public static void RecordAdminAbacDecision(string policy, string outcome) =>
        AdminAbacDecision.Add(
            1,
            new TagList
            {
                { "policy", NormalizeBoundedTag(policy) },
                { "outcome", NormalizeBoundedTag(outcome) }
            });

    /// <summary>记录紧急访问使用次数；详细人员、原因、TraceId 由结构化审计日志保存。</summary>
    public static void RecordAdminBreakGlass(string outcome) =>
        AdminBreakGlass.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                NormalizeBoundedTag(outcome)));

    private static string NormalizeBoundedTag(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : new string(value
                .Where(character =>
                    char.IsLetterOrDigit(character)
                    || character is '-' or '_' or '.')
                .Take(64)
                .ToArray());

    /// <summary>
    /// 暴露每个活跃实例的最后心跳秒数；实例标签只存在于该有界 Gauge，
    /// 便于 Prometheus 在没有新事件时仍能计算心跳年龄。
    /// </summary>
    private static IEnumerable<Measurement<double>> ObserveInstanceHeartbeats()
    {
        foreach (var item in InstanceHeartbeats)
        {
            yield return new Measurement<double>(
                item.Value.UnixSeconds,
                new TagList
                {
                    { "server_instance_id", item.Key },
                    { "lifecycle", item.Value.Lifecycle },
                    { "build_version", item.Value.BuildVersion }
                });
        }
    }

    /// <summary>按受控生命周期和构建版本聚合 30 秒内有心跳的房间数量。</summary>
    private static IEnumerable<Measurement<long>> ObserveActiveRooms() =>
        ObserveActiveGroups(group => group.LongCount());

    /// <summary>按受控生命周期和构建版本聚合当前连接玩家数。</summary>
    private static IEnumerable<Measurement<long>> ObserveConnectedPlayers() =>
        ObserveActiveGroups(group =>
            group.Sum(item => (long)item.ConnectedPlayers));

    private static IEnumerable<Measurement<long>> ObserveActiveGroups(
        Func<IEnumerable<InstanceHeartbeatState>, long> selector)
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        foreach (var group in InstanceHeartbeats.Values
                     .Where(item => item.ObservedAtUtc >= cutoff)
                     .GroupBy(item => new
                     {
                         item.Lifecycle,
                         item.BuildVersion
                     }))
        {
            yield return new Measurement<long>(
                selector(group),
                new TagList
                {
                    { "lifecycle", group.Key.Lifecycle },
                    { "build_version", group.Key.BuildVersion }
                });
        }
    }

    private static void PruneInstanceHeartbeats(DateTimeOffset now)
    {
        var nowSeconds = now.ToUnixTimeSeconds();
        var previousPrune = Interlocked.Read(
            ref lastInstanceHeartbeatPruneUnixSeconds);
        if (nowSeconds - previousPrune < 60
            || Interlocked.CompareExchange(
                ref lastInstanceHeartbeatPruneUnixSeconds,
                nowSeconds,
                previousPrune) != previousPrune)
            return;
        // 24 小时淘汰和 10000 实例硬上限防止异常实例 ID 把 Meter 变成无界缓存。
        if (InstanceHeartbeats.Count <= 10_000
            && InstanceHeartbeats.Values.All(
                value => now - value.ObservedAtUtc < TimeSpan.FromHours(24)))
            return;
        foreach (var stale in InstanceHeartbeats
                     .OrderBy(item => item.Value.ObservedAtUtc)
                     .Take(Math.Max(1, InstanceHeartbeats.Count - 10_000))
                     .Concat(InstanceHeartbeats.Where(
                         item => now - item.Value.ObservedAtUtc
                             >= TimeSpan.FromHours(24)))
                     .DistinctBy(item => item.Key))
            InstanceHeartbeats.TryRemove(stale.Key, out _);
    }

    private sealed record InstanceHeartbeatState(
        long UnixSeconds,
        string Lifecycle,
        string BuildVersion,
        int ConnectedPlayers,
        DateTimeOffset ObservedAtUtc);
}
