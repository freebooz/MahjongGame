using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

public sealed record GameServerRouteSnapshot(
    string ServerInstanceId,
    string MatchId,
    string ServerIp,
    int ServerPort);

public sealed record RoomMonitorSnapshot
{
    public required string RoomId { get; init; }
    public required string RoomCode { get; init; }
    public required string OwnerPlayerId { get; init; }
    public required int RoundCount { get; init; }
    public required bool PublicRoom { get; init; }
    public required bool AutoStart { get; init; }
    public required int MaximumPlayers { get; init; }
    public required Dictionary<string, JsonElement> RuleSnapshot { get; init; }
    public required string Lifecycle { get; init; }
    public required string[] PlayerIds { get; init; }
    public GameServerRouteSnapshot? Route { get; init; }
    public string? LastServerInstanceId { get; init; }
    public string? PendingServerInstanceId { get; init; }
    public required string MatchId { get; init; }
    public required long StateSequence { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public bool NewPlayersProhibited { get; init; }
    public bool MaintenanceMode { get; init; }
    public bool MarkedAbnormal { get; init; }
    public string RegionId { get; init; } = "local";
    public string ClusterId { get; init; } = "local";
    public string LobbyId { get; init; } = "lobby-local-1";
    public string NodeId { get; init; } = "node-local-1";
    public string SourceId { get; init; } = "legacy-lobby";
}

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
    string? FailureReason);

public sealed record MonitoredInstance(
    string ClusterId,
    string NodeId,
    GameServerInstanceSnapshot Instance,
    string RegionId = "local",
    string SourceId = "legacy-allocator");

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
    string? ConnectionEventId = null);

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
    SettlementRuntimeTelemetry? Settlement = null);

public sealed record RoomTimelineEvent(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    long StateSequence,
    string TraceId,
    Dictionary<string, JsonElement> Data);

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
    MonitoringReliabilityMetadata? Reliability = null);

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

public sealed record AuthSessionMonitor(
    string SessionReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    bool Active);

public sealed record AuthLoginEvent(
    string EventId,
    string PlayerId,
    string DeviceId,
    string MaskedIp,
    string ClientSummary,
    string Outcome,
    DateTimeOffset OccurredAtUtc);

public sealed record AuthPlayerDirectoryDetail(
    AuthPlayerDirectoryItem Player,
    AuthSessionMonitor[] Sessions,
    AuthLoginEvent[] LoginHistory,
    string[] KnownDeviceIds,
    PlayerControlEvent[] ControlHistory);

public sealed record PlayerControlState(
    long Version,
    string AccountStatus,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels,
    DateTimeOffset? RiskLabelsExpireAtUtc,
    DateTimeOffset UpdatedAtUtc);

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

public sealed record PlayerPresenceSnapshot(
    string PlayerId,
    bool Online,
    DateTimeOffset? LastSeenAtUtc,
    string LobbyId,
    string? RoomId = null,
    string? RoomCode = null,
    string? ServerInstanceId = null);

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
    string[] RiskLabels);

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
