using System.Text.Json.Serialization;

namespace GuiyangMahjong.Lobby.Domain;

/// <summary>房间生命周期状态机的稳定线协议值；迁移规则由 RoomStateMachine 集中维护。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoomLifecycle>))]
public enum RoomLifecycle
{
    Creating,
    Allocating,
    Waiting,
    Playing,
    Settling,
    Closed,
    Failed
}

/// <summary>Lobby 可预期错误分类；API 通过稳定机器码映射，不能把内部异常直接替代该枚举。</summary>
public enum LobbyErrorCode
{
    InvalidRequest,
    SessionExpired,
    RequestInProgress,
    RoomNotFound,
    RoomFull,
    RoomClosed,
    PasswordRequired,
    WrongPassword,
    RateLimited,
    ServerUnavailable,
    TicketExpired,
    VersionMismatch,
    Timeout,
    Cancelled,
    BackendNotConfigured,
    InternalError
}

/// <summary>从 Auth 令牌解析出的最小玩家身份；不包含访问令牌、设备或网络地址。</summary>
public sealed record PlayerIdentity(string PlayerId, string DisplayName, string Provider);

/// <summary>房间密码的 PBKDF2 持久化值；Salt/Hash 使用 Base64，Iterations 是创建时冻结的迭代次数。</summary>
public sealed record ProtectedPassword(string SaltBase64, string HashBase64, int Iterations);

/// <summary>
/// 玩家加入 Dedicated Server 的短期路由。
/// JoinTicket 与玩家、房间、比赛和实例绑定，ServerPort 为客户端连接端口，过期后必须重新申请。
/// </summary>
public sealed record GameServerRoute(
    string RequestId,
    string PlayerId,
    string RoomId,
    string ServerInstanceId,
    string MatchId,
    string ServerIp,
    int ServerPort,
    string JoinTicket,
    DateTimeOffset TicketExpireAtUtc);

/// <summary>
/// Lobby 房间聚合的不可变快照。
/// 修改通过 with 创建新版本并递增 StateSequence；密码和结果凭据只保存不可逆值，
/// Route 中的短期票据不得进入长期监控历史。
/// </summary>
public sealed record LobbyRoom
{
    // 房间标识、短码和房主在房间生命周期内不可变；短码用于玩家输入但不是授权凭据。
    public required string RoomId { get; init; }
    public required string RoomCode { get; init; }
    public required string OwnerPlayerId { get; init; }

    // 基础规则在创建时冻结；RuleSnapshot 的值必须来自服务端白名单和范围校验。
    public required int RoundCount { get; init; }
    public required bool PublicRoom { get; init; }
    public required bool AutoStart { get; init; }
    public required int MaximumPlayers { get; init; }
    public required Dictionary<string, object?> RuleSnapshot { get; init; }

    // 生命周期和玩家数组共同表示当前权威房间状态；玩家标识去重且最多 MaximumPlayers 个。
    public required RoomLifecycle Lifecycle { get; init; }
    public required string[] PlayerIds { get; init; }

    // 敏感/路由字段：密码和结果凭据只保存哈希，Route 仅包含当前有效加入路由。
    public ProtectedPassword? Password { get; init; }
    public GameServerRoute? Route { get; init; }
    public string? LastServerInstanceId { get; init; }
    public string? ResultCredentialHash { get; init; }
    public string? PendingServerInstanceId { get; init; }

    // MatchId 每场分配唯一；StateSequence 单调递增，时间均使用服务端 UTC。
    public string MatchId { get; init; } = Guid.Empty.ToString();
    public long StateSequence { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? EmptySinceUtc { get; init; }

    // 三个管理控制标记只允许经审批后的内部端点修改，普通房间 API 不得清除或覆盖。
    public bool NewPlayersProhibited { get; init; }
    public bool MaintenanceMode { get; init; }
    public bool MarkedAbnormal { get; init; }
}

/// <summary>
/// 创建房间输入；规则、局数、人数和密码在事务前校验，
/// Password 仅在当前请求内使用并立即派生哈希，不得持久化明文。
/// </summary>
public sealed record CreateRoomRequest(
    int RoundCount,
    bool PublicRoom,
    bool AutoStart,
    bool PasswordProtected,
    string? Password,
    Dictionary<string, object?> RuleSnapshot);

/// <summary>加入房间输入；协议版本用于拒绝不兼容客户端，密码只在验证期间短暂使用。</summary>
public sealed record JoinRoomRequest(string? Password, int ClientProtocolVersion);

/// <summary>断线重连路由查询；RoomId 或 MatchId 至少提供一个且必须匹配当前玩家房间。</summary>
public sealed record ReconnectRouteRequest(string? RoomId = null, string? MatchId = null);

/// <summary>结算中的单个玩家结果；座位、名次和总分由 Dedicated Server 权威产生。</summary>
public sealed record MatchPlayerResult(
    string PlayerId,
    int SeatIndex,
    int Rank,
    int TotalScore);

/// <summary>
/// Dedicated Server 提交的整场结果。
/// ResultSequence 对同一比赛单调递增，Players 必须覆盖房间参与者且不能由普通运营修改。
/// </summary>
public sealed record MatchResultReport(
    string RoomId,
    string ServerInstanceId,
    long ResultSequence,
    int CompletedRounds,
    MatchPlayerResult[] Players);

/// <summary>结果接收回执；Duplicate 表示同序号同载荷的幂等重放，Accepted=false 不代表已结算。</summary>
public sealed record MatchResultAck(
    string RequestId,
    string MatchId,
    long ResultSequence,
    bool Accepted,
    bool Duplicate);

/// <summary>
/// Dedicated Server 启动后的注册载荷。
/// RegistrationCredential 是一次性秘密，监听地址/端口和版本必须与 Allocator 预期绑定。
/// </summary>
public sealed record GameServerRegistration(
    string ServerInstanceId,
    string RoomId,
    string MatchId,
    string ListenIp,
    int ListenPort,
    string BuildVersion,
    string RegistrationCredential);

/// <summary>
/// 注册成功回执。
/// 心跳与结果凭据用途隔离且只能返回目标实例；RoomBootstrap 是不含房间密码的只读启动配置。
/// </summary>
public sealed record GameServerRegistrationAck(
    string RequestId,
    bool Accepted,
    int HeartbeatIntervalSeconds,
    string HeartbeatCredential,
    string ResultCredential,
    ManagedRoomBootstrap RoomBootstrap);

/// <summary>游戏服启动所需房间规则快照；PasswordProtected 只表明策略，不下发密码或哈希。</summary>
public sealed record ManagedRoomBootstrap(
    string RoomId,
    string RoomCode,
    string MatchId,
    string OwnerPlayerId,
    int RoundCount,
    int MaximumPlayers,
    bool PublicRoom,
    bool AutoStart,
    bool PasswordProtected,
    Dictionary<string, object?> RuleSnapshot);

/// <summary>
/// Dedicated Server 发往 Lobby 的房间运行心跳。
/// 该载荷只接受服务端权威状态；可选指标缺失表示生产者尚不支持，不能解释为数值零。
/// </summary>
/// <param name="RoomId">房间唯一标识，生命周期内保持不变。</param>
/// <param name="HeartbeatCredential">当前实例专用心跳凭证，只用于内部传输，不得持久化或输出日志。</param>
/// <param name="ConnectedPlayers">当前仍由服务器会话管理的玩家数量，范围为 0～4。</param>
/// <param name="RoomLifecycle">服务器观察到的房间生命周期。</param>
/// <param name="RoundId">当前局序号；尚未开局时允许为 0。</param>
/// <param name="BuildVersion">Dedicated Server 构建版本，用于定位兼容性和回退版本。</param>
/// <param name="SentAtUtc">Dedicated Server 发送时刻；仅用于诊断，不作为监控新鲜度的权威时间。</param>
/// <param name="ConnectedPlayerIds">当前会话玩家标识，数量必须与 <paramref name="ConnectedPlayers"/> 一致。</param>
/// <param name="GameStartedAtUtc">本场游戏首次开局时间；后续心跳缺失时 Lobby 保留已有值。</param>
/// <param name="ServerTickMilliseconds">最近一次服务器 Tick 耗时，单位毫秒。</param>
/// <param name="ServerFramesPerSecond">最近一次服务器帧率，单位帧/秒。</param>
/// <param name="RpcReceivedCount">进程启动以来接收的服务器 RPC 累计数量。</param>
/// <param name="ProcessMemoryBytes">进程常驻内存目标口径，单位字节；当前生产者口径修正由工作流 B 完成。</param>
/// <param name="ProcessCpuPercent">按节点总 CPU 容量归一化的进程占用百分比，目标范围为 0～100。</param>
/// <param name="NetworkIngressBytes">进程启动以来累计接收的网络字节数。</param>
/// <param name="NetworkEgressBytes">进程启动以来累计发送的网络字节数。</param>
/// <param name="Players">玩家座位、连接、延迟、掉线时刻和托管状态的权威快照。</param>
/// <param name="TelemetrySchemaVersion">
/// 遥测主版本；未携带时按 v1 兼容，显式未知版本必须拒绝，防止单位或语义被错误解释。
/// </param>
/// <param name="ProcessCpuSampleWindowMilliseconds">CPU 利用率所覆盖的最近采样窗口，单位毫秒。</param>
/// <param name="RpcMethods">固定白名单 RPC 方法的累计调用、异常与延迟分位统计。</param>
/// <param name="Settlement">Dedicated Server 当前观察到的显式结算投影。</param>
public sealed record GameServerHeartbeat(
    string RoomId,
    string HeartbeatCredential,
    int ConnectedPlayers,
    string RoomLifecycle,
    int RoundId,
    string BuildVersion,
    DateTimeOffset SentAtUtc,
    string[]? ConnectedPlayerIds = null,
    DateTimeOffset? GameStartedAtUtc = null,
    double? ServerTickMilliseconds = null,
    double? ServerFramesPerSecond = null,
    long? RpcReceivedCount = null,
    long? ProcessMemoryBytes = null,
    double? ProcessCpuPercent = null,
    long? NetworkIngressBytes = null,
    long? NetworkEgressBytes = null,
    PlayerRuntimeTelemetry[]? Players = null,
    int TelemetrySchemaVersion = 1,
    double? ProcessCpuSampleWindowMilliseconds = null,
    RpcMethodTelemetry[]? RpcMethods = null,
    SettlementRuntimeTelemetry? Settlement = null);

/// <summary>
/// 单个固定 RPC 方法自进程启动以来的有界累计指标；MethodName 只能来自代码白名单，
/// 严禁使用 PlayerId、RoomId 或任意请求参数生成方法名，避免无限基数。
/// </summary>
/// <param name="MethodName">稳定的方法标识，例如 Server.RequestAction。</param>
/// <param name="ReceivedCount">进入服务端 RPC 实现的累计次数。</param>
/// <param name="RejectedCount">在本地参数、幂等或权限校验中被拒绝的累计次数。</param>
/// <param name="FailedCount">进入处理器后未完成预期业务动作的累计次数。</param>
/// <param name="TimeoutCount">超过 RPC 处理超时阈值的累计次数。</param>
/// <param name="P95DurationMilliseconds">最近有界样本窗口的 P95 处理耗时，单位毫秒。</param>
/// <param name="P99DurationMilliseconds">最近有界样本窗口的 P99 处理耗时，单位毫秒。</param>
public sealed record RpcMethodTelemetry(
    string MethodName,
    long ReceivedCount,
    long RejectedCount,
    long FailedCount,
    long TimeoutCount,
    double? P95DurationMilliseconds,
    double? P99DurationMilliseconds);

/// <summary>
/// 一场比赛的只读结算投影；它只描述提交链路状态，不包含可编辑的输赢结果。
/// 普通运营人员只能读取该投影，任何补偿仍必须经过既有审批与资产操作链路。
/// </summary>
/// <param name="Status">Calculating、Submitted、Accepted、Failed、Compensating 或 Completed。</param>
/// <param name="MatchId">与房间绑定的比赛标识。</param>
/// <param name="ResultSequence">服务端单调结算序号；尚未生成时为 null。</param>
/// <param name="ResultHash">结算请求正文的 SHA-256；尚未生成时为 null。</param>
/// <param name="SubmittedAtUtc">首次提交时间。</param>
/// <param name="ConfirmedAtUtc">Lobby 确认时间。</param>
/// <param name="FailureReason">失败类别摘要，不包含凭证或玩家敏感数据。</param>
public sealed record SettlementRuntimeTelemetry(
    string Status,
    string MatchId,
    long? ResultSequence,
    string? ResultHash,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    string? FailureReason);

/// <summary>
/// 单个玩家在某次房间遥测中的运行状态；该快照不包含身份密钥或完整网络地址。
/// </summary>
/// <param name="PlayerId">玩家唯一标识。</param>
/// <param name="SeatIndex">座位索引，0～3；未知座位使用 -1。</param>
/// <param name="ConnectionState">连接状态，只允许 Connected、Disconnected 或 Reconnecting。</param>
/// <param name="LatencyMilliseconds">最近一次服务端观测延迟，单位毫秒；未知时为 null。</param>
/// <param name="DisconnectedAtUtc">本次掉线开始时间；未掉线或生产者不支持时为 null。</param>
/// <param name="Trustee">是否处于托管；生产者不支持时为 null，不能解释为 false。</param>
/// <param name="TrusteeChangedAtUtc">最近一次托管状态变化时间。</param>
/// <param name="ConnectionChangedAtUtc">最近一次连接状态变化时间。</param>
/// <param name="ReconnectedAtUtc">最近一次成功重连时间。</param>
/// <param name="DisconnectReason">受控掉线原因；连接正常或未知时为 null。</param>
/// <param name="ConnectionStateSequence">玩家连接状态单调序号，用于对重复心跳去重。</param>
/// <param name="ConnectionEventId">本次连接状态变化的幂等事件标识。</param>
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
/// Lobby 写入监控存储并提供给 Admin 的房间运行快照。
/// ObservedAtUtc 使用 Lobby 接收时钟，确保数据新鲜度不受游戏服时钟漂移影响。
/// </summary>
/// <param name="RoomId">房间唯一标识。</param>
/// <param name="ServerInstanceId">产生该快照的 Dedicated Server 实例标识。</param>
/// <param name="ObservedAtUtc">Lobby 完成接收和校验的 UTC 时刻，是新鲜度计算的权威时间。</param>
/// <param name="GameStartedAtUtc">游戏首次开局 UTC 时刻。</param>
/// <param name="Lifecycle">房间生命周期。</param>
/// <param name="CurrentRound">当前局序号。</param>
/// <param name="ConnectedPlayers">当前服务器会话玩家数。</param>
/// <param name="ServerTickMilliseconds">最近一次服务器 Tick 耗时，单位毫秒。</param>
/// <param name="ServerFramesPerSecond">最近一次服务器帧率，单位帧/秒。</param>
/// <param name="RpcReceivedCount">进程启动以来接收的 RPC 累计数量。</param>
/// <param name="ProcessMemoryBytes">进程常驻内存目标口径，单位字节。</param>
/// <param name="ProcessCpuPercent">按节点总 CPU 容量归一化的进程占用百分比。</param>
/// <param name="NetworkIngressBytes">进程启动以来累计接收的网络字节数。</param>
/// <param name="NetworkEgressBytes">进程启动以来累计发送的网络字节数。</param>
/// <param name="BuildVersion">产生快照的 Dedicated Server 构建版本。</param>
/// <param name="Players">玩家运行状态快照，所有权随记录复制并持久化。</param>
/// <param name="TelemetrySchemaVersion">解释该快照字段单位与语义所需的遥测主版本。</param>
/// <param name="ProcessCpuSampleWindowMilliseconds">CPU 利用率最近采样窗口，单位毫秒。</param>
/// <param name="NetworkIngressBytesPerSecond">相邻有效心跳间的应用入站字节速率。</param>
/// <param name="NetworkEgressBytesPerSecond">相邻有效心跳间的应用出站字节速率。</param>
/// <param name="RpcMethods">固定方法白名单的 RPC 分类指标。</param>
/// <param name="Settlement">当前显式结算状态投影。</param>
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

/// <summary>
/// 房间事件时间线的持久化条目。
/// EventId 提供幂等，StateSequence 保证同房间排序，Data 只允许脱敏后的受控字段。
/// </summary>
public sealed record RoomTimelineEvent(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    long StateSequence,
    string TraceId,
    Dictionary<string, object?> Data);

/// <summary>玩家在 Lobby/房间/实例上的实时在线投影；LastSeenAtUtc 为空表示尚无可信观察。</summary>
public sealed record PlayerPresenceSnapshot(
    string PlayerId,
    bool Online,
    DateTimeOffset? LastSeenAtUtc,
    string LobbyId,
    string? RoomId = null,
    string? RoomCode = null,
    string? ServerInstanceId = null);

/// <summary>Admin 强制下线命令；原因和 TraceId 进入审计，生效时间为服务端认可的 UTC。</summary>
public sealed record AdminDisconnectPlayerRequest(
    string Reason,
    string TraceId,
    DateTimeOffset EffectiveAtUtc);

/// <summary>强制下线结果；RevokedBeforeUtc 是拒绝旧访问的时间界线，Duplicate 支持命令重放。</summary>
public sealed record AdminDisconnectPlayerResult(
    string PlayerId,
    DateTimeOffset RevokedBeforeUtc,
    bool Duplicate);

/// <summary>Admin 房间控制命令；ExpectedStateSequence 防止基于陈旧页面修改已变化房间。</summary>
public sealed record AdminUpdateRoomControlRequest(
    string ActionType,
    long ExpectedStateSequence,
    string Reason,
    string TraceId);

/// <summary>房间控制结果；返回最新序号、控制标志、生命周期和实例关联供审计记录后状态。</summary>
public sealed record AdminUpdateRoomControlResult(
    string RoomId,
    string ActionType,
    long StateSequence,
    bool NewPlayersProhibited,
    bool MaintenanceMode,
    bool MarkedAbnormal,
    RoomLifecycle Lifecycle,
    string? ServerInstanceId,
    bool AlreadyTerminal);

/// <summary>Allocator 通知的实例故障；原因必须脱敏，Lobby 据此迁移关联房间并写时间线。</summary>
public sealed record GameServerFailure(
    string ServerInstanceId,
    string RoomId,
    string Reason);

/// <summary>异步房间操作受理回执；RetryAfterMilliseconds 是轮询建议而非完成保证。</summary>
public sealed record RoomOperation(
    string RequestId,
    string RoomId,
    string RoomCode,
    RoomLifecycle Lifecycle,
    int RetryAfterMilliseconds = 1000);

/// <summary>大厅启动响应；公告为公开内容，协议版本决定客户端兼容性。</summary>
public sealed record LobbyBootstrapResponse(
    string RequestId,
    string PlayerId,
    string DisplayName,
    int OnlinePlayerCount,
    string[] Announcements,
    int ProtocolVersion);

/// <summary>公开房间目录的最小列表投影；不暴露玩家身份、规则细节或服务器路由。</summary>
public sealed record RoomDirectoryItem(
    string RoomCode,
    RoomLifecycle Lifecycle,
    int PlayerCount,
    int MaximumPlayers,
    bool PasswordProtected,
    int RoundCount);

/// <summary>Lobby 统一错误响应；RetryAfterMilliseconds 仅在可重试失败时提供。</summary>
public sealed record ApiError(string RequestId, string Code, string Message, int? RetryAfterMilliseconds = null);

/// <summary>Lobby 实时事件信封；Sequence 在发布源内单调递增，Data 必须是可序列化的脱敏投影。</summary>
public sealed record LobbyEventEnvelope(
    string Type,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    object Data);
