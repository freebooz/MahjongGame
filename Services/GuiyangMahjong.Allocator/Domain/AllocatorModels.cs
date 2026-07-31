// Allocator 领域模型：定义游戏服实例、端口租约、分配请求、心跳和监控快照。
// 时间统一使用 UTC，端口和状态枚举必须经过服务端校验，客户端输入不能直接成为最终实例状态。
using System.Text.Json.Serialization;

namespace GuiyangMahjong.Allocator.Domain;

/// <summary>
/// Dedicated Server 实例生命周期；只有管理器可以迁移状态，
/// 持久化与 API 使用稳定字符串值以便跨版本调查。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GameServerInstanceState>))]
public enum GameServerInstanceState
{
    Starting,
    Ready,
    Allocated,
    Draining,
    Stopped,
    Failed
}

/// <summary>
/// 房间申请游戏服的幂等业务输入。
/// RoomEpoch 是 Lobby 生成的路由 fencing token，Allocator 只负责原样传递给目标 DS。
/// </summary>
public sealed record AllocationRequest(
    string RoomId,
    string MatchId,
    string BuildVersion,
    long RoomEpoch = 1,
    string? AllocationId = null,
    string GameType = "guiyang-zhua-ji",
    string Region = "local",
    string RuleSetVersion = "guiyang-zhuoji-v1",
    string ProtocolVersion = "1",
    int RequestedCapacity = 4,
    string? IdempotencyKey = null);

/// <summary>分配结果；端口是主机监听端口，状态通常为 Starting，需等待实例注册后才能加入。</summary>
public sealed record AllocationResponse(
    string RequestId,
    string RoomId,
    string ServerInstanceId,
    int Port,
    GameServerInstanceState State,
    long RoomEpoch = 1,
    string? AllocationId = null,
    long FencingToken = 1);

/// <summary>
/// Dedicated Server 启动后的单次注册请求。
/// 注册凭据仅在进程启动通道传递，Allocator 校验后以心跳凭据替换。
/// </summary>
public sealed record ConfirmRegistrationRequest(
    string RoomId,
    string ListenIp,
    int ListenPort,
    string BuildVersion,
    string RegistrationCredential,
    long RoomEpoch = 0,
    long FencingToken = 0);

/// <summary>注册结果；心跳间隔单位为秒，心跳凭据只返回给已验证的目标实例。</summary>
public sealed record ConfirmRegistrationResponse(
    string RequestId,
    string ServerInstanceId,
    bool Accepted,
    int HeartbeatIntervalSeconds,
    string HeartbeatCredential,
    long RoomEpoch = 1,
    long FencingToken = 1);

/// <summary>
/// 实例周期心跳；玩家数和局号为非负计数，SentAtUtc 用于识别陈旧或乱序样本，
/// 生命周期字符串仍需由 Allocator 按允许值验证。
/// </summary>
public sealed record InstanceHeartbeatRequest(
    string RoomId,
    string HeartbeatCredential,
    int ConnectedPlayers,
    string RoomLifecycle,
    int RoundId,
    string BuildVersion,
    DateTimeOffset SentAtUtc,
    long RoomEpoch = 0,
    long FencingToken = 0);

/// <summary>
/// 对外可见的实例只读快照。
/// 不暴露注册/心跳凭据哈希；端口、进程号和 UTC 时间用于监控、终止前置校验和事故调查。
/// </summary>
public sealed record GameServerInstanceSnapshot(
    string ServerInstanceId,
    string RoomId,
    string MatchId,
    int Port,
    string AdvertisedIp,
    int? ProcessId,
    GameServerInstanceState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? RegisteredAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    string BuildVersion,
    string? FailureReason,
    long RoomEpoch = 1,
    string? AllocationId = null,
    string Provider = "LocalProcess",
    long FencingToken = 1);

/// <summary>发送给 Lobby 的实例失败通知；原因必须脱敏且不包含启动参数中的秘密。</summary>
public sealed record InstanceFailureNotification(
    string ServerInstanceId,
    string RoomId,
    string Reason,
    long RoomEpoch = 1);

/// <summary>Admin 终止实例命令；ExpectedState 防止基于陈旧监控页面误杀已复用实例。</summary>
public sealed record AdminTerminateInstanceRequest(
    string ExpectedState,
    string Reason,
    string TraceId);

/// <summary>终止执行结果；AlreadyStopped 使同一 CommandId 的重放保持幂等。</summary>
public sealed record AdminTerminateInstanceResult(
    string CommandId,
    GameServerInstanceSnapshot Instance,
    bool AlreadyStopped);

/// <summary>
/// Allocator 内存中的可变实例聚合；所有写入必须在管理器同步边界内完成。
/// 凭据只保存哈希，Process 的所有权属于管理器，快照不得泄漏内部控制字段。
/// </summary>
internal sealed class GameServerInstance
{
    // 实例、房间和匹配标识在创建后不可变，三者共同用于幂等恢复与跨服务关联。
    public required string ServerInstanceId { get; init; }
    public required string RoomId { get; init; }
    public required string MatchId { get; init; }
    /// <summary>调用方分配标识和幂等键在一次分配生命周期内不可变，用于响应丢失后的稳定查询。</summary>
    public required string AllocationId { get; init; }
    public required string IdempotencyKey { get; init; }
    /// <summary>规范化请求指纹用于拒绝相同幂等键携带不同参数，禁止记录凭据。</summary>
    public required string RequestFingerprint { get; init; }
    /// <summary>Lobby 分配代际；实例生命周期内不可变，旧代际不得向新房间路由注册。</summary>
    public long RoomEpoch { get; init; } = 1;
    /// <summary>实例租约 fencing token；当前版本与 RoomEpoch 同值，单独持久化以支持后续独立演进。</summary>
    public long FencingToken { get; init; } = 1;

    // 调度约束在创建后冻结，同一房间 Epoch 不允许切换 Provider 或版本/容量条件。
    public required string Provider { get; init; }
    public required string GameType { get; init; }
    public required string Region { get; init; }
    public required string RuleSetVersion { get; init; }
    public required string ProtocolVersion { get; init; }
    public int RequestedCapacity { get; init; }

    // 端口单位为 TCP/UDP 端口号；AdvertisedIp 是客户端可达地址，不一定等于监听地址。
    public required int Port { get; init; }
    public required string AdvertisedIp { get; init; }

    // 凭据数组仅保存 SHA-256 等不可逆结果；注册成功后派生独立心跳凭据。
    public required byte[] RegistrationCredentialHash { get; set; }
    public byte[]? HeartbeatCredentialHash { get; set; }

    // 所有时间均为 UTC；注册截止时间控制僵尸启动回收，心跳时间控制失联检测。
    public required DateTimeOffset RegistrationExpireAtUtc { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? RegisteredAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }

    // BuildVersion 用于拒绝协议不兼容实例；状态只能按管理器定义的状态机迁移。
    public required string BuildVersion { get; init; }
    public GameServerInstanceState State { get; set; } = GameServerInstanceState.Starting;

    // 本机模式持有进程句柄和启动时间；Agones 模式改用编排资源名定位实例。
    public IManagedGameServerProcess? Process { get; set; }
    public DateTimeOffset? ProcessStartedAtUtc { get; set; }
    public string? OrchestratorResourceName { get; set; }

    // 失败通知与端口释放标记保证清理、通知可重试但只产生一次业务效果。
    public string? FailureReason { get; set; }
    public bool FailureNotified { get; set; }
    public DateTimeOffset? FailureNotificationAttemptedAtUtc { get; set; }
    public bool PortReleased { get; set; }

    /// <summary>创建不含凭据和进程控制句柄的监控快照；调用方取得独立值对象。</summary>
    public GameServerInstanceSnapshot Snapshot() => new(
        ServerInstanceId,
        RoomId,
        MatchId,
        Port,
        AdvertisedIp,
        Process?.ProcessId,
        State,
        StartedAtUtc,
        RegisteredAtUtc,
        LastHeartbeatAtUtc,
        BuildVersion,
        FailureReason,
        RoomEpoch,
        AllocationId,
        Provider,
        FencingToken);
}

/// <summary>Allocator 可安全映射为 HTTP 状态的领域异常；消息不得包含凭据或完整启动命令。</summary>
public sealed class AllocatorOperationException(string message, int statusCode) : Exception(message)
{
    /// <summary>建议返回给调用方的 HTTP 状态码，由异常中间件统一应用。</summary>
    public int StatusCode { get; } = statusCode;
}
