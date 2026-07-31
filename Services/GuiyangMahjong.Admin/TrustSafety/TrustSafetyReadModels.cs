using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Storage;

namespace GuiyangMahjong.Admin.TrustSafety;

/// <summary>风险模块的只读信号；来源事件必须脱敏且不能单独触发处罚。</summary>
public sealed record RiskSignal(string Code, string Severity, DateTimeOffset ObservedAtUtc, string Source, string TraceId);

/// <summary>反作弊模块的有界摘要；非法动作计数来自 DS 权威遥测，不包含私有手牌或动作正文。</summary>
public sealed record AntiCheatSummary(long? IllegalActionCount, double? PacketLossPercent, string Assessment);

/// <summary>调查模块的案件摘要；只暴露数量和工单关联，不返回证据正文。</summary>
public sealed record InvestigationSummary(int OpenCaseCount, string[] TicketIds);

/// <summary>处罚模块的当前摘要；控制历史仍通过既有案件授权端点读取。</summary>
public sealed record SanctionSummary(string AccountStatus, DateTimeOffset? FrozenUntilUtc, DateTimeOffset? MutedUntilUtc, string[] RiskLabels);

/// <summary>
/// RoomMonitoring 模块的规范视图，显式呈现 Epoch、快照年龄、风险、来源和最后更新时间；
/// Detail 中的规则、座位、DS 性能及事件时间线均来自受控读模型和遥测查询。
/// </summary>
public sealed record TrustSafetyRoomView(
    RoomDetail Detail,
    long StateVersion,
    long RoomEpoch,
    string RuleSetVersion,
    string BuildVersion,
    string? Fleet,
    string Provider,
    double? SnapshotAgeSeconds,
    RiskSignal[] RiskSignals,
    string DataSource,
    DateTimeOffset LastUpdatedAtUtc);

/// <summary>
/// PlayerMonitoring 模块的默认脱敏视图；不包含完整 IP、手机号、第三方标识、会话凭据或聊天正文。
/// </summary>
public sealed record TrustSafetyPlayerView(
    PlayerMonitorListItem Player,
    AntiCheatSummary AntiCheat,
    SanctionSummary Sanctions,
    InvestigationSummary Investigations,
    string DataSource,
    DateTimeOffset LastUpdatedAtUtc,
    double DataAgeSeconds);

/// <summary>
/// TrustSafety 聚合门面明确 Risk、AntiCheat、Monitoring、Investigations 和 Sanctions 的只读边界。
/// 它只组合现有受控查询和 Admin 自有案件读模型，不写入业务表，也不轮询 Dedicated Server 内存。
/// </summary>
public sealed class TrustSafetyReadModelService(
    MonitoringAggregationService roomMonitoring,
    PlayerMonitoringService playerMonitoring,
    AdminDataRedactionService redaction,
    IAdminCaseStore investigations,
    TimeProvider timeProvider)
{
    /// <summary>组合房间权威元数据、遥测和风险事件；任一数据源降级信息保留在 Detail.Reliability。</summary>
    public async Task<TrustSafetyRoomView?> GetRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        var detail = await roomMonitoring.GetRoomAsync(roomId, cancellationToken: cancellationToken);
        if (detail is null) return null;
        var now = timeProvider.GetUtcNow();
        var risks = detail.Timeline
            .Where(item => item.EventType.Contains("Risk", StringComparison.OrdinalIgnoreCase)
                || item.EventType.Contains("Illegal", StringComparison.OrdinalIgnoreCase)
                || item.EventType.Contains("Crash", StringComparison.OrdinalIgnoreCase)
                || item.EventType.Contains("Recover", StringComparison.OrdinalIgnoreCase))
            .Select(item => new RiskSignal(
                item.EventType,
                item.EventType.Contains("Crash", StringComparison.OrdinalIgnoreCase) ? "High" : "Medium",
                item.OccurredAtUtc,
                detail.Summary.SourceId,
                item.TraceId))
            .TakeLast(100)
            .ToArray();
        var runtime = detail.Runtime;
        return new TrustSafetyRoomView(
            detail,
            runtime?.StateVersion ?? detail.Summary.StateSequence,
            runtime?.RoomEpoch ?? detail.RoomEpoch,
            detail.RuleSetVersion,
            runtime?.BuildVersion ?? detail.DedicatedServer?.Instance.BuildVersion ?? detail.BuildVersion,
            detail.DedicatedServer?.Instance.Fleet,
            detail.DedicatedServer?.Instance.Provider ?? "unassigned",
            runtime?.SnapshotCreatedAtUtc is { } snapshotAt
                ? Math.Max(0, (now - snapshotAt).TotalSeconds)
                : null,
            risks,
            detail.Summary.SourceId,
            detail.Runtime?.ObservedAtUtc ?? detail.Summary.UpdatedAtUtc);
    }

    /// <summary>返回默认脱敏玩家视图；完整调查历史仍要求既有工单、ABAC 和读取审计。</summary>
    public async Task<TrustSafetyPlayerView?> GetPlayerAsync(
        string playerId,
        string? authorizedCaseId,
        CancellationToken cancellationToken)
    {
        var detail = await playerMonitoring.GetPlayerAsync(playerId, cancellationToken);
        if (detail is null) return null;
        // 默认监控只返回脱敏状态；工单关联必须由 API 层完成案件 ABAC 后显式传入，防止枚举其他调查。
        var cases = string.IsNullOrWhiteSpace(authorizedCaseId)
            ? []
            : (await investigations.ListAsync(500, cancellationToken))
                .Where(item => item.CaseId == authorizedCaseId.Trim()
                    && item.TargetType == "Player"
                    && item.TargetId == playerId
                    && item.Status == "Open")
                .ToArray();
        var now = timeProvider.GetUtcNow();
        var lastUpdated = detail.Summary.LastSeenAtUtc ?? detail.Summary.LastLoginAtUtc ?? now;
        return new TrustSafetyPlayerView(
            redaction.RedactPlayer(detail.Summary, false),
            new AntiCheatSummary(
                detail.Summary.IllegalActionCount,
                detail.Summary.PacketLossPercent,
                detail.Summary.IllegalActionCount is > 0 ? "ReviewRequired" : "NoCurrentSignal"),
            new SanctionSummary(
                detail.Summary.AccountStatus,
                detail.Summary.FrozenUntilUtc,
                detail.Summary.MutedUntilUtc,
                detail.Summary.RiskLabels),
            new InvestigationSummary(cases.Length, cases.Select(item => item.TicketId).Distinct(StringComparer.Ordinal).ToArray()),
            "Auth+Lobby+AdminReadModel",
            lastUpdated,
            Math.Max(0, (now - lastUpdated).TotalSeconds));
    }
}
