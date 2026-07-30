using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Allocator.Options;

/// <summary>Allocator 实例启动后端；LocalProcess 管理本机进程，Agones 管理 Kubernetes 资源。</summary>
public enum AllocatorBackendMode
{
    LocalProcess,
    Agones
}

/// <summary>
/// Agones 分配 API 配置。
/// ServiceAccount 令牌/CA 路径来自 Pod 挂载，只由 Allocator 读取，不写入日志或响应。
/// </summary>
public sealed class AgonesAllocatorOptions
{
    [Required] public string Namespace { get; init; } = "guiyang-mahjong";
    [Required] public string FleetName { get; init; } = "guiyang-mahjong";
    [Required] public string ApiServer { get; init; } = "https://kubernetes.default.svc";
    [Required] public string ServiceAccountTokenPath { get; init; } =
        "/var/run/secrets/kubernetes.io/serviceaccount/token";
    [Required] public string ServiceAccountCaPath { get; init; } =
        "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
    [Range(1, 60)] public int RequestTimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// Allocator 根配置，定义端口容量、生命周期超时、进程/Agones 后端、状态恢复及服务身份。
/// 注册、监控、管理和 Lobby 回调凭据用途隔离，生产通过安全配置注入。
/// </summary>
public sealed class AllocatorOptions
{
    public const string SectionName = "Allocator";

    public AllocatorBackendMode Backend { get; init; } = AllocatorBackendMode.LocalProcess;
    [Required] public AgonesAllocatorOptions Agones { get; init; } = new();

    [Range(1024, 65535)] public int PortStart { get; init; } = 19000;
    [Range(1024, 65535)] public int PortEnd { get; init; } = 19099;
    [Range(5, 300)] public int RegistrationTimeoutSeconds { get; init; } = 30;
    [Range(3, 300)] public int HeartbeatTimeoutSeconds { get; init; } = 15;
    [Range(1, 60)] public int HeartbeatIntervalSeconds { get; init; } = 3;
    [Range(100, 60_000)] public int MonitorIntervalMilliseconds { get; init; } = 500;
    [Range(0, 60)] public int DrainGraceSeconds { get; init; } = 3;
    [Required] public string AdvertisedIp { get; init; } = "127.0.0.1";
    public string GameServerExecutablePath { get; init; } = string.Empty;
    public string GameServerWorkingDirectory { get; init; } = string.Empty;
    public string[] GameServerPrefixArguments { get; init; } = [];
    [Required] public string MatchResultOutboxDirectory { get; init; } = "match-result-outbox";
    [Required] public string StateFilePath { get; init; } = "allocator-state/instances.json";
    [Range(1, 60)] public int StateCheckpointSeconds { get; init; } = 5;
    [Range(1, 300)] public int FailureNotificationRetrySeconds { get; init; } = 5;
    [Range(1, 300)] public int MatchResultRecoveryDelaySeconds { get; init; } = 15;
    [Required, Url] public string LobbyInternalUrl { get; init; } = "http://127.0.0.1:18080";
    [MinLength(32)] public string ServiceToken { get; init; } = string.Empty;
    public string MonitoringReadOnlyToken { get; init; } = string.Empty;
    public string ManagementCommandToken { get; init; } = string.Empty;
    [MinLength(32)] public string LobbyCallbackToken { get; init; } = string.Empty;
    [MinLength(32)] public string JoinTicketSigningKey { get; init; } = string.Empty;
    /// <summary>Allocator 所属地域、集群、节点和逻辑实例标识；用于跨集群聚合与故障隔离。</summary>
    [Required] public string RegionId { get; init; } = "local";
    [Required] public string ClusterId { get; init; } = "local";
    [Required] public string NodeId { get; init; } = "game-node";
    [Required] public string AllocatorId { get; init; } = "allocator-local-1";
    [Required] public AllocatorTopologyRegistrationOptions TopologyRegistration { get; init; } = new();
}

/// <summary>Allocator 向 Admin 动态目录注册只读监控端点的配置；管理命令端点不会通过该目录下发。</summary>
public sealed class AllocatorTopologyRegistrationOptions
{
    public bool Enabled { get; init; }
    [Required, Url] public string AdminBaseUrl { get; init; } = "http://127.0.0.1:18083";
    [Required, Url] public string PublicBaseUrl { get; init; } = "http://127.0.0.1:18081";
    public string RegistrationToken { get; init; } = string.Empty;
    [Required] public string SourceId { get; init; } = "allocator-local-1";
    [Range(1, long.MaxValue)] public long Generation { get; init; } = 1;
    [Range(5, 120)] public int RefreshSeconds { get; init; } = 20;
}
