using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

/// <summary>Admin 从 Lobby 获得的 Dedicated Server 路由投影；不包含加入票据或服务凭据。</summary>
public sealed record GameServerRouteSnapshot(
    string ServerInstanceId,
    string MatchId,
    string ServerIp,
    int ServerPort,
    long RoomEpoch = 1,
    string BuildVersion = "",
    string RuleSetVersion = "",
    int ProtocolVersion = 1);

/// <summary>房间座位的只读投影；座位索引在当前 RoomEpoch 内稳定，不包含私有手牌。</summary>
public sealed record RoomSeatSnapshot(string PlayerId, int SeatIndex, DateTimeOffset JoinedAtUtc);

/// <summary>
/// Admin 使用的房间配置与控制快照。
/// StateSequence 是管理操作的乐观并发依据；来源/区域/集群/节点字段标识多集群归属，
/// RuleSnapshot 和玩家数组在聚合边界复制后只读使用。
/// </summary>
public sealed record RoomMonitorSnapshot
{
    // 房间标识、短码、房主及冻结规则来自 Lobby 权威状态。
    public required string RoomId { get; init; }
    public required string RoomCode { get; init; }
    public required string OwnerPlayerId { get; init; }
    public required int RoundCount { get; init; }
    public required bool PublicRoom { get; init; }
    public required bool AutoStart { get; init; }
    public required int MaximumPlayers { get; init; }
    public required Dictionary<string, JsonElement> RuleSnapshot { get; init; }

    // 生命周期、玩家和实例路由描述当前业务状态；Route 不携带客户端加入票据。
    public required string Lifecycle { get; init; }
    public required string[] PlayerIds { get; init; }
    public GameServerRouteSnapshot? Route { get; init; }
    public string? LastServerInstanceId { get; init; }
    public string? PendingServerInstanceId { get; init; }

    // MatchId、序号和 UTC 时间用于跨源关联、并发控制与调查排序。
    public required string MatchId { get; init; }
    public required long StateSequence { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    // 管理控制标记为只读投影，改变它们必须走二次确认与审批工作流。
    public bool NewPlayersProhibited { get; init; }
    public bool MaintenanceMode { get; init; }
    public bool MarkedAbnormal { get; init; }

    // 拓扑来源字段具有稳定默认值以兼容旧单集群数据，生产聚合应填入实际身份。
    public string RegionId { get; init; } = "local";
    public string ClusterId { get; init; } = "local";
    public string LobbyId { get; init; } = "lobby-local-1";
    public string NodeId { get; init; } = "node-local-1";
    public string SourceId { get; init; } = "legacy-lobby";

    // 阶段4之后的权威并发、路由和版本字段；默认值仅用于读取旧快照，不能用于执行高风险命令。
    public long StateVersion { get; init; }
    public long RoomEpoch { get; init; } = 1;
    public string RuleSetVersion { get; init; } = "legacy-v1";
    public string BuildVersion { get; init; } = "unassigned";
    public RoomSeatSnapshot[] Seats { get; init; } = [];
}

/// <summary>
/// Admin 使用的 Allocator 实例快照。
/// ProcessId 仅在所属节点有意义，时间均为 UTC，FailureReason 必须脱敏且不含启动参数。
/// </summary>
public sealed record GameServerInstanceSnapshot(
    string ServerInstanceId,
    string RoomId,
    string MatchId,
    int Port,
    string AdvertisedIp,
    int? ProcessId,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? RegisteredAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    string BuildVersion,
    string? FailureReason,
    long RoomEpoch = 1,
    string? AllocationId = null,
    string Provider = "LocalProcess",
    long FencingToken = 1,
    string? Fleet = null);

/// <summary>带集群、节点、区域和数据源身份的实例投影，用于多集群聚合与命令路由。</summary>
public sealed record MonitoredInstance(
    string ClusterId,
    string NodeId,
    GameServerInstanceSnapshot Instance,
    string RegionId = "local",
    string SourceId = "legacy-allocator");

/// <summary>概览中的稳定分组计数；Key 由受控玩法、状态或集群枚举产生，不能使用玩家标识。</summary>
public sealed record CountGroup(string Key, int Count);

/// <summary>
/// Admin 从 Lobby 接收的玩家运行遥测；null 表示数据源未提供，不能转换为零值或 false。
/// </summary>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="SeatIndex">座位索引 0～3，未知座位为 -1。</param>
/// <param name="ConnectionState">连接状态：Connected、Disconnected 或 Reconnecting。</param>
/// <param name="LatencyMilliseconds">服务端观测延迟，单位毫秒。</param>
/// <param name="DisconnectedAtUtc">当前掉线开始时间。</param>
/// <param name="Trustee">当前托管状态；数据源不支持时为 null。</param>
/// <param name="TrusteeChangedAtUtc">最近一次托管状态变化时间。</param>
/// <param name="ConnectionChangedAtUtc">最近一次连接状态变化时间。</param>
/// <param name="ReconnectedAtUtc">最近一次成功重连时间。</param>
/// <param name="DisconnectReason">受控掉线原因；连接正常或未知时为 null。</param>
/// <param name="ConnectionStateSequence">连接状态单调序号，用于识别重复心跳。</param>
/// <param name="ConnectionEventId">最近连接状态事件的幂等标识。</param>
/// <param name="PacketLossPercent">服务端观测丢包率百分比，范围 0～100。</param>
/// <param name="IllegalActionCount">本实例累计拒绝的非法动作数。</param>
/// <param name="ReconnectCount">本实例观察到的成功重连次数。</param>
public sealed record PlayerRuntimeTelemetry(
    string PlayerId,
    int SeatIndex,
    string ConnectionState,
    double? LatencyMilliseconds,
    DateTimeOffset? DisconnectedAtUtc,
    bool? Trustee,
    DateTimeOffset? TrusteeChangedAtUtc = null,
    DateTimeOffset? ConnectionChangedAtUtc = null,
    DateTimeOffset? ReconnectedAtUtc = null,
    string? DisconnectReason = null,
    long? ConnectionStateSequence = null,
    string? ConnectionEventId = null,
    double? PacketLossPercent = null,
    long? IllegalActionCount = null,
    int? ReconnectCount = null);

/// <summary>
/// 固定方法白名单的 RPC 累计指标；方法名不得含玩家、房间或请求参数，避免高基数。
/// </summary>
/// <param name="MethodName">稳定 RPC 方法标识。</param>
/// <param name="ReceivedCount">累计接收次数。</param>
/// <param name="RejectedCount">累计本地拒绝次数。</param>
/// <param name="FailedCount">累计业务失败次数。</param>
/// <param name="TimeoutCount">累计处理超时次数。</param>
/// <param name="P95DurationMilliseconds">最近有界样本窗口 P95 耗时，单位毫秒。</param>
/// <param name="P99DurationMilliseconds">最近有界样本窗口 P99 耗时，单位毫秒。</param>
public sealed record RpcMethodTelemetry(
    string MethodName,
    long ReceivedCount,
    long RejectedCount,
    long FailedCount,
    long TimeoutCount,
    double? P95DurationMilliseconds,
    double? P99DurationMilliseconds);

/// <summary>
/// 只读结算链路投影；该模型不暴露修改比赛结果的任何接口。
/// </summary>
/// <param name="Status">Calculating、Submitted、Accepted、Failed、Compensating 或 Completed。</param>
/// <param name="MatchId">比赛唯一标识。</param>
/// <param name="ResultSequence">服务端单调结算序号。</param>
/// <param name="ResultHash">结算正文 SHA-256。</param>
/// <param name="SubmittedAtUtc">首次提交时间。</param>
/// <param name="ConfirmedAtUtc">Lobby 确认时间。</param>
/// <param name="FailureReason">安全失败摘要。</param>
public sealed record SettlementRuntimeTelemetry(
    string Status,
    string MatchId,
    long? ResultSequence,
    string? ResultHash,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    string? FailureReason);

/// <summary>
/// Admin 使用的房间运行快照线协议模型，字段名称、类型和默认值必须与 Lobby v1 保持一致。
/// </summary>
/// <param name="RoomId">房间唯一标识。</param>
/// <param name="ServerInstanceId">Dedicated Server 实例标识。</param>
/// <param name="ObservedAtUtc">Lobby 接收时刻，是监控新鲜度的权威时间。</param>
/// <param name="GameStartedAtUtc">游戏首次开局时刻。</param>
/// <param name="Lifecycle">房间生命周期。</param>
/// <param name="CurrentRound">当前局序号。</param>
/// <param name="ConnectedPlayers">当前服务器会话玩家数。</param>
/// <param name="ServerTickMilliseconds">服务器 Tick 耗时，单位毫秒。</param>
/// <param name="ServerFramesPerSecond">服务器帧率，单位帧/秒。</param>
/// <param name="RpcReceivedCount">进程启动以来接收的 RPC 累计数量。</param>
/// <param name="ProcessMemoryBytes">进程常驻内存目标口径，单位字节。</param>
/// <param name="ProcessCpuPercent">按节点总 CPU 容量归一化的进程占用百分比。</param>
/// <param name="NetworkIngressBytes">进程启动以来累计接收的网络字节数。</param>
/// <param name="NetworkEgressBytes">进程启动以来累计发送的网络字节数。</param>
/// <param name="BuildVersion">Dedicated Server 构建版本。</param>
/// <param name="Players">玩家运行状态快照。</param>
/// <param name="TelemetrySchemaVersion">指标单位、空值和兼容规则的遥测主版本。</param>
/// <param name="ProcessCpuSampleWindowMilliseconds">CPU 最近采样窗口，单位毫秒。</param>
/// <param name="NetworkIngressBytesPerSecond">相邻有效样本计算的入站字节速率。</param>
/// <param name="NetworkEgressBytesPerSecond">相邻有效样本计算的出站字节速率。</param>
/// <param name="RpcMethods">固定方法白名单的 RPC 分类指标。</param>
/// <param name="Settlement">显式结算状态投影。</param>
/// <param name="ActionSequence">当前权威动作单调序号。</param>
/// <param name="StateVersion">当前权威状态版本。</param>
/// <param name="RoomEpoch">当前房间路由代际。</param>
/// <param name="SnapshotVersion">最近有效快照版本。</param>
/// <param name="SnapshotCreatedAtUtc">最近快照创建时间。</param>
/// <param name="RecoveryState">崩溃恢复状态摘要。</param>
/// <param name="LastTraceId">最近权威变化的跨服务 TraceId。</param>
public sealed record RoomRuntimeTelemetry(
    string RoomId,
    string ServerInstanceId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? GameStartedAtUtc,
    string Lifecycle,
    int CurrentRound,
    int ConnectedPlayers,
    double? ServerTickMilliseconds,
    double? ServerFramesPerSecond,
    long? RpcReceivedCount,
    long? ProcessMemoryBytes,
    double? ProcessCpuPercent,
    long? NetworkIngressBytes,
    long? NetworkEgressBytes,
    string BuildVersion,
    PlayerRuntimeTelemetry[] Players,
    int TelemetrySchemaVersion = 1,
    double? ProcessCpuSampleWindowMilliseconds = null,
    double? NetworkIngressBytesPerSecond = null,
    double? NetworkEgressBytesPerSecond = null,
    RpcMethodTelemetry[]? RpcMethods = null,
    SettlementRuntimeTelemetry? Settlement = null,
    long? ActionSequence = null,
    long? StateVersion = null,
    long? RoomEpoch = null,
    int? SnapshotVersion = null,
    DateTimeOffset? SnapshotCreatedAtUtc = null,
    string? RecoveryState = null,
    string? LastTraceId = null);

/// <summary>
/// Admin 房间事件时间线条目。
/// EventId/StateSequence 用于去重排序，TraceId 关联跨服务调用，Data 是脱敏 JSON 字段集合。
/// </summary>
public sealed record RoomTimelineEvent(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    long StateSequence,
    string TraceId,
    Dictionary<string, JsonElement> Data);

/// <summary>
/// 监控总览的单次聚合快照。
/// 所有计数来自同一聚合时刻，Reliability 描述各来源新鲜度和降级状态。
/// </summary>
public sealed record MonitoringOverview(
    DateTimeOffset ObservedAtUtc,
    int TotalRooms,
    int ActiveRooms,
    int AbnormalRooms,
    int TotalConnectedPlayers,
    int DedicatedServerInstances,
    CountGroup[] RoomsByGameMode,
    CountGroup[] RoomsByState,
    CountGroup[] RoomsByCluster,
    MonitoringReliabilityMetadata? Reliability = null);

/// <summary>
/// 房间列表的分页行模型。
/// 玩家数、局数和控制序号是查询时点值；拓扑字段决定详情与管理命令应路由到哪个来源。
/// </summary>
public sealed record RoomListItem(
    string RoomId,
    string RoomCode,
    string MatchId,
    string GameMode,
    string Lifecycle,
    int PlayerCount,
    int MaximumPlayers,
    int CurrentRound,
    int RoundCount,
    string? ClusterId,
    string? NodeId,
    string? ServerInstanceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long StateSequence,
    string RegionId = "local",
    string LobbyId = "lobby-local-1",
    string SourceId = "legacy-lobby");

/// <summary>
/// 房间详情聚合。
/// 配置、实例、运行遥测和时间线可独立降级；TelemetryStatus/Reliability 明示缺失或陈旧来源，
/// 该模型不提供修改结算结果的字段。
/// </summary>
public sealed record RoomDetail(
    RoomListItem Summary,
    Dictionary<string, JsonElement> Rules,
    string OwnerPlayerId,
    string[] PlayerIds,
    bool PublicRoom,
    bool AutoStart,
    bool NewPlayersProhibited,
    bool MaintenanceMode,
    bool MarkedAbnormal,
    MonitoredInstance? DedicatedServer,
    RoomRuntimeTelemetry? Runtime,
    RoomTimelineEvent[] Timeline,
    string TelemetryStatus,
    MonitoringReliabilityMetadata? Reliability = null,
    long RoomEpoch = 1,
    string RuleSetVersion = "legacy-v1",
    string BuildVersion = "unassigned",
    RoomSeatSnapshot[]? Seats = null);

/// <summary>Auth 玩家目录的 Admin 线协议模型；IP 已脱敏，风险标签和控制时间来自 Auth 权威状态。</summary>
public sealed record AuthPlayerDirectoryItem(
    string PlayerId,
    string DisplayName,
    string Provider,
    string AccountStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    string? CurrentDeviceId,
    string? CurrentMaskedIp,
    int ActiveSessionCount,
    long ControlVersion,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels);

/// <summary>玩家刷新会话的只读监控投影；SessionReference 不能用于认证或换取令牌。</summary>
public sealed record AuthSessionMonitor(
    string SessionReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    bool Active);

/// <summary>脱敏登录事件；设备为内部引用，MaskedIp 不得还原完整地址。</summary>
public sealed record AuthLoginEvent(
    string EventId,
    string PlayerId,
    string DeviceId,
    string MaskedIp,
    string ClientSummary,
    string Outcome,
    DateTimeOffset OccurredAtUtc);

/// <summary>Auth 玩家详情线协议；历史数组有界，返回前仍按 RBAC 执行字段级脱敏。</summary>
public sealed record AuthPlayerDirectoryDetail(
    AuthPlayerDirectoryItem Player,
    AuthSessionMonitor[] Sessions,
    AuthLoginEvent[] LoginHistory,
    string[] KnownDeviceIds,
    PlayerControlEvent[] ControlHistory);

/// <summary>玩家封禁、冻结、禁言和风险标签的版本化只读状态。</summary>
public sealed record PlayerControlState(
    long Version,
    string AccountStatus,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels,
    DateTimeOffset? RiskLabelsExpireAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 玩家控制审计事件；保存双人身份、工单/TraceId、前后状态和撤销会话数量，
/// 普通运营只能读取其授权范围，不能重写历史。
/// </summary>
public sealed record PlayerControlEvent(
    string CommandId,
    string PlayerId,
    string ActionType,
    string Reason,
    string TraceId,
    string TicketId,
    string RequestedBy,
    string ApprovedBy,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? RiskLabel,
    int RevokedSessionCount,
    PlayerControlState BeforeState,
    PlayerControlState AfterState);

/// <summary>Lobby 玩家在线位置投影；LastSeenAtUtc 为空表示无可信在线观察。</summary>
public sealed record PlayerPresenceSnapshot(
    string PlayerId,
    bool Online,
    DateTimeOffset? LastSeenAtUtc,
    string LobbyId,
    string? RoomId = null,
    string? RoomCode = null,
    string? ServerInstanceId = null);

/// <summary>
/// 玩家监控列表行，合并 Auth 身份/控制和 Lobby 在线/房间状态。
/// 延迟单位毫秒，IP 已脱敏，任何来源陈旧性在详情可靠性元数据中解释。
/// </summary>
public sealed record PlayerMonitorListItem(
    string PlayerId,
    string DisplayName,
    string Provider,
    string AccountStatus,
    bool Online,
    string? CurrentDeviceId,
    string? CurrentMaskedIp,
    string? LobbyId,
    string? RoomId,
    string? RoomCode,
    string? ServerInstanceId,
    double? LatencyMilliseconds,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    int ActiveSessionCount,
    long ControlVersion,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels,
    double? PacketLossPercent = null,
    int? ReconnectCount = null,
    bool? Trustee = null,
    long? IllegalActionCount = null,
    string? ConnectionState = null,
    DateTimeOffset? DisconnectedAtUtc = null);

/// <summary>
/// 玩家监控详情聚合。
/// 登录、设备、房间、掉线和 GM 控制历史均为授权后的有界投影；
/// DataScope 与 Reliability 明确字段范围、来源和降级情况。
/// </summary>
public sealed record PlayerMonitorDetail(
    PlayerMonitorListItem Summary,
    AuthSessionMonitor[] Sessions,
    AuthLoginEvent[] LoginHistory,
    string[] KnownDeviceIds,
    RoomListItem[] RoomHistory,
    RoomTimelineEvent[] DisconnectHistory,
    PlayerControlEvent[] ControlHistory,
    string DataScope,
    MonitoringReliabilityMetadata? Reliability = null);
