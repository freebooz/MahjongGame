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

public sealed class AllocatorClientOptions
{
    public bool Enabled { get; init; }
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18081";
    public string ServiceToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
    [Required] public string GameServerBuildVersion { get; init; } = "unreal-linux";
}

public sealed class LobbyPersistenceOptions
{
    [Required] public string Mode { get; init; } = "InMemory";
    /// <summary>是否允许运行进程执行建表；生产环境必须关闭并由独立 migration 身份预部署。</summary>
    public bool ApplyDatabaseMigrations { get; init; } = true;
    public string RedisConnectionString { get; init; } = string.Empty;
    public string PostgresConnectionString { get; init; } = string.Empty;
    [Required] public string RedisKeyPrefix { get; init; } = "guiyang:lobby:v1";
}
