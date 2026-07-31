using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Lobby.Options;

/// <summary>大厅服务配置。生产环境必须从密钥服务注入 TokenSigningKey。</summary>
public sealed class LobbyOptions
{
    public const string SectionName = "Lobby";

    [Range(1, 1)] public int ProtocolVersion { get; init; } = 1;
    [Range(10, 1000)] public int RoomCodeRetryLimit { get; init; } = 100;
    [Range(1, 4)] public int MaximumPlayersPerRoom { get; init; } = 4;
    [Range(1, 20)] public int PasswordFailureLimit { get; init; } = 5;
    [Range(30, 3600)] public int PasswordFailureWindowSeconds { get; init; } = 300;
    [Range(15, 600)] public int PresenceTimeoutSeconds { get; init; } = 90;
    [Range(15, 600)] public int PlayerReservationTimeoutSeconds { get; init; } = 90;
    [Range(15, 600)] public int EmptyRoomTimeoutSeconds { get; init; } = 90;
    [Range(60, 604800)] public int IdempotencyTtlSeconds { get; init; } = 86400;
    [Range(5, 120)] public int IdempotencyLockSeconds { get; init; } = 30;
    [MinLength(32)] public string TokenSigningKey { get; init; } = string.Empty;
    /// <summary>
    /// Auth 密钥轮换期间仍可验证的旧 HMAC 密钥。数组只通过密钥管理系统或环境变量注入，
    /// 不得写入日志；完成最长 Access Token 生命周期的重叠窗口后应删除。
    /// </summary>
    public string[] PreviousTokenValidationKeys { get; init; } = [];
    [MinLength(32)] public string JoinTicketSigningKey { get; init; } = string.Empty;
    [MinLength(32)] public string InternalServiceToken { get; init; } = string.Empty;
    public string MonitoringReadOnlyToken { get; init; } = string.Empty;
    public string ManagementCommandToken { get; init; } = string.Empty;
    [Range(15, 1440)] public int AccessRevocationTtlMinutes { get; init; } = 120;
    public bool EnableHttpsRedirection { get; init; }
    /// <summary>部署地域唯一标识，用于跨地域监控、策略约束和故障隔离。</summary>
    [Required] public string RegionId { get; init; } = "local";
    /// <summary>所属集群标识；同一地域内必须唯一且部署期间保持稳定。</summary>
    [Required] public string ClusterId { get; init; } = "local";
    /// <summary>大厅实例逻辑标识，禁止再使用固定 primary 代替真实拓扑。</summary>
    [Required] public string LobbyId { get; init; } = "lobby-local-1";
    /// <summary>运行节点标识；用于节点级筛选和故障定位。</summary>
    [Required] public string NodeId { get; init; } = "node-local-1";
    public string[] Announcements { get; init; } = [];
    [Required] public LobbyPersistenceOptions Persistence { get; init; } = new();
    [Required] public AllocatorClientOptions Allocator { get; init; } = new();
    [Required] public TopologyRegistrationOptions TopologyRegistration { get; init; } = new();
    /// <summary>基础匹配与重连窗口配置；环境变量使用 Lobby__Matchmaking__* 覆盖。</summary>
    [Required] public MatchmakingOptions Matchmaking { get; init; } = new();
}

/// <summary>
/// LobbyControl 内置基础匹配配置。
/// 当前只支持单地域普通队列；复杂段位、赛事和跨区扩圈不属于阶段 4。
/// </summary>
public sealed class MatchmakingOptions
{
    /// <summary>活动匹配票据有效期，单位秒；过期后 PostgreSQL 权威记录转为 Expired。</summary>
    [Range(10, 3600)] public int TicketTtlSeconds { get; init; } = 120;

    /// <summary>DS 路由丢失后允许玩家查询恢复上下文的窗口，单位秒。</summary>
    [Range(15, 600)] public int ReconnectionWindowSeconds { get; init; } = 120;

    /// <summary>
    /// 是否允许初始 RoomEpoch 接受未携带 Epoch 的旧版 DS。
    /// 重新分配后的 Epoch 始终要求精确匹配，此开关不能放宽 fencing。
    /// </summary>
    public bool AllowLegacyInitialEpoch { get; init; } = true;
}

/// <summary>Lobby 向 Admin 动态拓扑目录刷新短租约所需的最小配置；注册凭据不得复用监控读取凭据。</summary>
public sealed class TopologyRegistrationOptions
{
    public bool Enabled { get; init; }
    [Required, Url] public string AdminBaseUrl { get; init; } = "http://127.0.0.1:18083";
    [Required, Url] public string PublicBaseUrl { get; init; } = "http://127.0.0.1:18080";
    public string RegistrationToken { get; init; } = string.Empty;
    [Required] public string SourceId { get; init; } = "lobby-local-1";
    [Range(1, long.MaxValue)] public long Generation { get; init; } = 1;
    [Range(5, 120)] public int RefreshSeconds { get; init; } = 20;
}

/// <summary>
/// Lobby 调用 Allocator 的服务配置。
/// ServiceToken 只授权分配/注册/心跳流程，超时覆盖完整 HTTP 响应读取。
/// </summary>
public sealed class AllocatorClientOptions
{
    public bool Enabled { get; init; }
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18081";
    public string ServiceToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
    [Required] public string GameServerBuildVersion { get; init; } = "unreal-linux";
    /// <summary>Allocator Fleet/Provider 调度使用的稳定游戏类型，不得来自客户端输入。</summary>
    [Required] public string GameType { get; init; } = "guiyang-zhua-ji";
    /// <summary>期望部署地域；必须与 Allocation Service 的可用 Provider 容量标签一致。</summary>
    [Required] public string Region { get; init; } = "local";
    /// <summary>服务端规则集版本，用于阻止错误规则镜像承载房间。</summary>
    [Required] public string RuleSetVersion { get; init; } = "guiyang-zhuoji-v1";
    /// <summary>Dedicated Server 网络协议版本；不等同于 HTTP API 版本。</summary>
    [Required] public string ProtocolVersion { get; init; } = "1";
    /// <summary>单实例请求席位容量；当前贵阳麻将有效范围为 1 至 4。</summary>
    [Range(1, 4)] public int RequestedCapacity { get; init; } = 4;
}

/// <summary>
/// Lobby 权威持久化配置。
/// Redis 仅保存热状态，PostgreSQL 保存权威房间/历史；生产连接使用独立最小权限身份。
/// </summary>
public sealed class LobbyPersistenceOptions
{
    [Required] public string Mode { get; init; } = "InMemory";
    /// <summary>是否允许运行进程执行建表；生产环境必须关闭并由独立 migration 身份预部署。</summary>
    public bool ApplyDatabaseMigrations { get; init; } = true;
    public string RedisConnectionString { get; init; } = string.Empty;
    public string PostgresConnectionString { get; init; } = string.Empty;
    [Required] public string RedisKeyPrefix { get; init; } = "guiyang:lobby:v1";
}
