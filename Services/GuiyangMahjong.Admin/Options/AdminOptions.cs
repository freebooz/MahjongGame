using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Admin.Options;

/// <summary>
/// Admin 根配置。
/// 聚合企业身份、RBAC/ABAC、审批执行、监控来源、归档和实时容量策略；
/// 所有秘密只从服务端安全配置注入，不能下发 Angular 或写入日志。
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    public string ReadOnlyAccessToken { get; init; } = string.Empty;
    public string EvidenceIngestionToken { get; init; } = string.Empty;
    public AdminPrincipalOptions[] Principals { get; init; } = [];
    [Required] public EnterpriseIdentityOptions EnterpriseIdentity { get; init; } = new();
    [Required] public AdminWebSecurityOptions WebSecurity { get; init; } = new();
    [Required] public AuditArchiveOptions AuditArchive { get; init; } = new();
    [Required] public AdminManagementOptions Management { get; init; } = new();
    [Required] public MonitoringReliabilityOptions MonitoringReliability { get; init; } = new();
    [Required] public RealtimeCapacityOptions RealtimeCapacity { get; init; } = new();
    [Required] public CentralLogOptions CentralLogs { get; init; } = new();
    [Required] public ChatArchiveOptions ChatArchive { get; init; } = new();
    [Required] public ReplayArchiveOptions ReplayArchive { get; init; } = new();
    [Required] public TopologyDiscoveryOptions TopologyDiscovery { get; init; } = new();
    [Required] public AdminAbacOptions Abac { get; init; } = new();
    [Required] public AuthMonitoringOptions Auth { get; init; } = new();
    [Required] public LobbyMonitoringOptions Lobby { get; init; } = new();
    [Required] public PlayerDataMonitoringOptions PlayerData { get; init; } = new();
    [Required] public WalletExecutionOptions Wallet { get; init; } = new();
    /// <summary>Configuration Service 受控命令代理；浏览器永远不能取得此服务端凭据。</summary>
    [Required] public ConfigurationManagementOptions Configuration { get; init; } = new();
    public AllocatorMonitoringOptions[] Allocators { get; init; } = [];
}

/// <summary>配置治理上游设置；Admin 只调用业务 API，不连接 Configuration Schema。</summary>
public sealed class ConfigurationManagementOptions
{
    /// <summary>显式启用后才展示和执行发布流程，便于一键回滚到阶段 10 行为。</summary>
    public bool Enabled { get; init; }
    /// <summary>集群内 Configuration Service 地址，不允许由浏览器覆盖。</summary>
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18088";
    /// <summary>Admin BFF 专用命令凭据，只能从 Secret 注入且不得记录。</summary>
    public string CommandToken { get; init; } = string.Empty;
    /// <summary>单次上游调用硬超时秒数；POST 不执行无条件透明重试。</summary>
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>
/// 管理端属性访问控制策略；RBAC 负责授予能力，ABAC 继续按地域、班次、案件归属和金额收窄数据及操作范围。
/// </summary>
public sealed class AdminAbacOptions
{
    /// <summary>是否启用属性访问控制；生产环境必须启用，非生产环境可关闭以兼容本地联调。</summary>
    public bool Enabled { get; init; }
    /// <summary>企业身份中声明可管理地域的 Claim；支持逗号或空格分隔，星号表示全部地域。</summary>
    [Required] public string RegionClaim { get; init; } = "mahjong_regions";
    /// <summary>企业身份中的当前班次 Claim；启用 ABAC 后必须存在，避免离班账号继续执行管理操作。</summary>
    [Required] public string ShiftClaim { get; init; } = "mahjong_shift";
    /// <summary>企业身份中已分派案件的 Claim；读取敏感历史和证据时必须命中案件归属。</summary>
    [Required] public string CaseClaim { get; init; } = "mahjong_cases";
    /// <summary>紧急访问授权的 UTC 截止时间 Claim；授权窗口仍受服务端最大时长限制。</summary>
    [Required] public string BreakGlassUntilClaim { get; init; } = "mahjong_break_glass_until";
    /// <summary>高额补偿阈值，单位为游戏内最小资产单位；达到阈值后要求高级治理审批角色。</summary>
    [Range(1, long.MaxValue)] public long HighValueCompensationThreshold { get; init; } = 100_000;
    /// <summary>紧急访问单次允许的最长时间，单位分钟；用于限制身份提供方误签发的超长授权。</summary>
    [Range(1, 30)] public int BreakGlassMaximumMinutes { get; init; } = 15;
}

/// <summary>分页、SSE 续传窗口与目标容量保护配置；所有上限均在服务端强制执行。</summary>
public sealed class RealtimeCapacityOptions
{
    /// <summary>房间、玩家和实例列表的默认页大小。</summary>
    [Range(10, 200)] public int DefaultPageSize { get; init; } = 100;
    /// <summary>任一列表允许的最大页大小，客户端无法绕过。</summary>
    [Range(10, 500)] public int MaximumPageSize { get; init; } = 200;
    /// <summary>单次聚合允许载入的房间上限，对应首期 1 万房间目标。</summary>
    [Range(1000, 50000)] public int MaximumRooms { get; init; } = 10_000;
    /// <summary>玩家目录容量上限，对应首期 10 万在线/注册玩家目标。</summary>
    [Range(10000, 500000)] public int MaximumPlayers { get; init; } = 100_000;
    /// <summary>跨集群 Dedicated Server 实例聚合上限。</summary>
    [Range(100, 50000)] public int MaximumInstances { get; init; } = 20_000;
    /// <summary>SSE 环形续传窗口内保存的最大事件数，超窗后客户端必须重新同步。</summary>
    [Range(1000, 100000)] public int EventBacklogLimit { get; init; } = 20_000;
    /// <summary>每个 SSE 连接的有界发送队列；满载时断开慢客户端并要求重同步。</summary>
    [Range(16, 4096)] public int SubscriberQueueLimit { get; init; } = 256;
    /// <summary>服务端生成增量快照的间隔，单位秒。</summary>
    [Range(1, 30)] public int SnapshotIntervalSeconds { get; init; } = 2;
    /// <summary>
    /// 每个快照周期最多扫描的玩家游标页数；用于限制后台 CPU、内存和内部网络峰值。
    /// 完整扫描可跨多个周期，删除事件仅在完整扫描结束后判定。
    /// </summary>
    [Range(1, 100)] public int PlayerPagesPerSnapshotCycle { get; init; } = 20;
    /// <summary>是否启用 SSE；关闭时前端按兼容轮询间隔工作。</summary>
    public bool SseEnabled { get; init; } = true;
    /// <summary>SSE 不可用时是否允许 5 秒全量轮询兼容模式。</summary>
    public bool LegacyPollingEnabled { get; init; } = true;
}

/// <summary>
/// Admin 到集中日志查询网关的只读配置；查询凭据只存在服务端，不返回浏览器。
/// </summary>
public sealed class CentralLogOptions
{
    /// <summary>是否启用 Loki 集中日志检索；禁用时批准导出仍仅包含审批快照。</summary>
    public bool Enabled { get; init; }

    /// <summary>Loki 或受认证反向代理的服务端根地址，不得直接暴露到前端配置。</summary>
    [Required, Url]
    public string BaseUrl { get; init; } = "http://127.0.0.1:3100";

    /// <summary>集中日志只读查询凭据；不得使用 Loki 管理员或写入凭据。</summary>
    public string QueryToken { get; init; } = string.Empty;

    /// <summary>单次查询硬超时，单位秒。</summary>
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;

    /// <summary>批准导出的默认回溯窗口，单位小时。</summary>
    [Range(1, 720)] public int LookbackHours { get; init; } = 24;

    /// <summary>单次导出最大日志行数，防止查询放大和内存失控。</summary>
    [Range(1, 5000)] public int MaxEntries { get; init; } = 1000;
}

/// <summary>
/// 合规聊天归档查询网关配置；Admin 只持有受限查询凭据，不能写入、删除或绕过案件授权。
/// </summary>
public sealed class ChatArchiveOptions
{
    /// <summary>是否连接独立聊天归档；关闭时内容查询默认拒绝。</summary>
    public bool Enabled { get; init; }
    /// <summary>归档只读网关地址，仅在服务端使用，不下发浏览器。</summary>
    [Required, Url]
    public string BaseUrl { get; init; } = "http://127.0.0.1:18086";
    /// <summary>最小权限查询令牌，不得是聊天库管理员凭据。</summary>
    public string QueryToken { get; init; } = string.Empty;
    /// <summary>单次查询超时，单位秒。</summary>
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
    /// <summary>单页最大消息数，防止批量抓取。</summary>
    [Range(1, 500)] public int MaxEntries { get; init; } = 100;
}

/// <summary>回放对象读取网关配置；浏览器只获得绑定案件与操作者的短时 Admin URL。</summary>
public sealed class ReplayArchiveOptions
{
    public bool Enabled { get; init; }
    [Required, Url]
    public string BaseUrl { get; init; } = "http://127.0.0.1:18087";
    public string ReadToken { get; init; } = string.Empty;
    public string SigningKey { get; init; } = string.Empty;
    [Range(30, 600)] public int AccessTtlSeconds { get; init; } = 300;
    [Range(1, 512)] public int MaxObjectMegabytes { get; init; } = 64;
}

/// <summary>
/// 多地域监控来源注册配置；注册凭据与各来源只读监控凭据必须分离。
/// </summary>
public sealed class TopologyDiscoveryOptions
{
    public bool Enabled { get; init; }
    /// <summary>Lobby/Allocator 注册 Admin 拓扑目录时使用的专用凭据。</summary>
    public string RegistrationToken { get; init; } = string.Empty;
    /// <summary>动态 Lobby 来源统一使用的只读凭据，不允许携带管理权限。</summary>
    public string LobbyMonitoringToken { get; init; } = string.Empty;
    /// <summary>动态 Allocator 来源统一使用的只读凭据，不允许携带终止实例权限。</summary>
    public string AllocatorMonitoringToken { get; init; } = string.Empty;
    /// <summary>注册租约秒数；来源必须在半租期前刷新。</summary>
    [Range(15, 300)] public int LeaseSeconds { get; init; } = 60;
    /// <summary>单次动态来源请求硬超时。</summary>
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>
/// Admin 监控聚合的统一可靠性策略；所有时间单位均为秒，快照仅保存在当前进程内存中。
/// </summary>
public sealed class MonitoringReliabilityOptions
{
    /// <summary>连续失败达到此次数后打开来源级熔断器，避免故障下游遭遇请求风暴。</summary>
    [Range(1, 20)] public int CircuitFailureThreshold { get; init; } = 3;

    /// <summary>首次熔断等待时间；重复打开时按指数退避，直到最大等待时间。</summary>
    [Range(1, 300)] public int CircuitBreakSeconds { get; init; } = 10;

    /// <summary>熔断指数退避的上限，防止恢复探测间隔无限增长。</summary>
    [Range(1, 3600)] public int CircuitMaxBreakSeconds { get; init; } = 120;

    /// <summary>数据超过此年龄后必须在页面醒目标记为陈旧，不得解释为实时值。</summary>
    [Range(1, 3600)] public int StaleAfterSeconds { get; init; } = 15;

    /// <summary>最后成功快照的最大生存时间；超期后直接返回来源不可用，不再使用缓存。</summary>
    [Range(1, 86400)] public int SnapshotTtlSeconds { get; init; } = 300;

    /// <summary>进程内最多保存的操作快照数，限制搜索条件等动态键造成的内存增长。</summary>
    [Range(8, 1024)] public int MaxSnapshotEntries { get; init; } = 128;
}

public sealed class EnterpriseIdentityOptions
{
    public bool Enabled { get; init; }
    public string Authority { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public bool RequireHttpsMetadata { get; init; } = true;
    public bool RequireMfa { get; init; } = true;
    /// <summary>企业令牌允许的最大签发时长（分钟）；用于保证离职和角色回收在 SLA 内生效。</summary>
    [Range(1, 30)] public int MaxTokenAgeMinutes { get; init; } = 10;
    /// <summary>身份与角色撤销最长生效时间（分钟），不得小于令牌最大年龄。</summary>
    [Range(1, 30)] public int RevocationSlaMinutes { get; init; } = 10;
    [Required] public string OperatorIdClaim { get; init; } = "sub";
    [Required] public string RoleClaim { get; init; } = "roles";
    [Required] public string AuthenticationMethodClaim { get; init; } = "amr";
    [Required] public string MfaValue { get; init; } = "mfa";
}

/// <summary>Admin 浏览器入口的传输、响应头与分级限流策略；生产环境不得关闭 HTTPS。</summary>
public sealed class AdminWebSecurityOptions
{
    /// <summary>是否强制 HTTPS 跳转并启用 HSTS；反向代理必须正确传递原始协议。</summary>
    public bool RequireHttps { get; init; }
    /// <summary>
    /// 可被信任的反向代理 IP；仅这些地址可以提供 X-Forwarded-For/Proto，
    /// 空数组表示 Kestrel 直接终止 TLS，不得使用不受限的转发头信任。
    /// </summary>
    public string[] TrustedProxyAddresses { get; init; } = [];
    /// <summary>普通只读请求每分钟、每个操作者的最大数量。</summary>
    [Range(10, 1000)] public int ReadRequestsPerMinute { get; init; } = 120;
    /// <summary>搜索请求每分钟、每个操作者的最大数量。</summary>
    [Range(1, 300)] public int SearchRequestsPerMinute { get; init; } = 30;
    /// <summary>敏感证据请求每分钟、每个操作者的最大数量。</summary>
    [Range(1, 100)] public int EvidenceRequestsPerMinute { get; init; } = 10;
    /// <summary>日志或证据导出每十分钟、每个操作者的最大数量。</summary>
    [Range(1, 30)] public int ExportRequestsPerTenMinutes { get; init; } = 3;
    /// <summary>是否启用浏览器 BFF 会话；关闭时保留原 Bearer 调用，便于紧急回滚。</summary>
    public bool BrowserSessionEnabled { get; init; } = true;
    /// <summary>不透明管理员会话 Cookie 名称；生产环境应保持 __Host- 前缀约束。</summary>
    [Required, MinLength(3), MaxLength(64)]
    public string SessionCookieName { get; init; } = "__Host-mahjong-admin";
    /// <summary>浏览器会话绝对有效期，单位分钟；不得超过企业身份撤销 SLA。</summary>
    [Range(1, 30)] public int SessionLifetimeMinutes { get; init; } = 10;
    /// <summary>CSRF 请求头名称；只对 Cookie 认证的非安全方法强制校验。</summary>
    [Required, MinLength(3), MaxLength(64)]
    public string CsrfHeaderName { get; init; } = "X-Admin-CSRF";
    /// <summary>是否将会话绑定到管理员设备摘要；生产环境必须启用。</summary>
    public bool BindDevice { get; init; } = true;
    /// <summary>是否将会话绑定到来源网络前缀摘要；生产环境必须启用。</summary>
    public bool BindIpNetwork { get; init; } = true;
}

public sealed class AuditArchiveOptions
{
    public bool Enabled { get; init; }
    public string AppendUrl { get; init; } = string.Empty;
    public string AppendToken { get; init; } = string.Empty;
    /// <summary>审计链根哈希锚定地址；应属于独立 WORM/SIEM 信任域。</summary>
    public string AnchorUrl { get; init; } = string.Empty;
    /// <summary>是否周期校验链并提交外部锚点。</summary>
    public bool AnchorEnabled { get; init; }
    /// <summary>锚定周期，单位秒。</summary>
    [Range(30, 3600)] public int AnchorIntervalSeconds { get; init; } = 300;
    /// <summary>
    /// 归档派发器专用 PostgreSQL 连接；生产启用归档时必须与管理运行连接分离，
    /// 对应仅能读取和更新 audit_archive_outbox 的数据库身份。
    /// </summary>
    public string PostgresConnectionString { get; init; } = string.Empty;
    [Range(100, 60000)] public int PollIntervalMilliseconds { get; init; } = 1000;
    [Range(1, 50)] public int MaxAttempts { get; init; } = 20;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>
/// 仅用于本地联调的静态 Admin 主体。
/// 生产启用企业身份后不得使用静态 AccessToken；角色和属性仍受白名单及 ABAC 校验。
/// </summary>
public sealed class AdminPrincipalOptions
{
    [Required, MinLength(3), MaxLength(128)] public string OperatorId { get; init; } = string.Empty;
    [MinLength(32)] public string AccessToken { get; init; } = string.Empty;
    public string[] Roles { get; init; } = [];
    /// <summary>本地联调身份允许访问的地域；空数组表示仅在 ABAC 关闭时可使用。</summary>
    public string[] Regions { get; init; } = [];
    /// <summary>本地联调身份被分派的案件编号；生产环境不允许使用静态身份。</summary>
    public string[] CaseIds { get; init; } = [];
    /// <summary>本地联调班次标识；启用 ABAC 时用于验证当前值班范围。</summary>
    public string ShiftId { get; init; } = string.Empty;
}

public sealed class AdminManagementOptions
{
    public bool Enabled { get; init; }
    [Required] public string PersistenceMode { get; init; } = "InMemory";
    public string PostgresConnectionString { get; init; } = string.Empty;
    /// <summary>是否允许运行进程执行 Admin 建表脚本；生产环境必须关闭并由 migration 身份执行。</summary>
    public bool ApplyDatabaseMigrations { get; init; } = true;
    public bool ExecutionEnabled { get; init; }
    [Range(100, 60000)] public int PollIntervalMilliseconds { get; init; } = 1000;
    [Range(5, 300)] public int LeaseSeconds { get; init; } = 30;
    [Range(1, 20)] public int MaxAttempts { get; init; } = 5;
    [Range(1, 300)] public int RetryBaseSeconds { get; init; } = 5;
    public string AuthCommandToken { get; init; } = string.Empty;
    public string LobbyCommandToken { get; init; } = string.Empty;
    [Range(1, 30)] public int CommandTimeoutSeconds { get; init; } = 5;
    [Range(1, 15)] public int ConfirmationTtlMinutes { get; init; } = 5;
    [Range(5, 1440)] public int ApprovalTtlMinutes { get; init; } = 60;
    [Range(1, 720)] public int TemporaryFreezeHours { get; init; } = 24;
    [Range(1, 720)] public int MuteHours { get; init; } = 24;
    [Range(1, 365)] public int RiskLabelTtlDays { get; init; } = 30;
}

/// <summary>Auth 只读玩家目录监控来源；MonitoringToken 不得复用 Auth 管理命令凭据。</summary>
public sealed class AuthMonitoringOptions
{
    public bool Enabled { get; init; } = true;
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18082";
    public string MonitoringToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>
/// 兼容单 Lobby 来源的只读监控配置。
/// Source/Region/Cluster/Lobby/Node 标识用于可靠性隔离和管理命令路由。
/// </summary>
public sealed class LobbyMonitoringOptions
{
    public bool Enabled { get; init; } = true;
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18080";
    public string MonitoringToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
    [Required] public string SourceId { get; init; } = "legacy-lobby";
    [Required] public string RegionId { get; init; } = "local";
    [Required] public string ClusterId { get; init; } = "local";
    [Required] public string LobbyId { get; init; } = "lobby-local-1";
    [Required] public string NodeId { get; init; } = "node-local-1";
}

/// <summary>
/// PlayerData 只读监控依赖配置；与 Wallet 管理命令凭据严格分离。
/// </summary>
public sealed class PlayerDataMonitoringOptions
{
    /// <summary>是否把 PlayerData 作为管理台只读监控来源；未启用时不会发起网络请求。</summary>
    public bool Enabled { get; init; }

    /// <summary>PlayerData 服务根地址，仅由服务端使用且不会出现在来源错误摘要中。</summary>
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18084";

    /// <summary>只读监控专用凭据；不得复用 Wallet 管理命令凭据。</summary>
    public string MonitoringToken { get; init; } = string.Empty;

    /// <summary>PlayerData 单次监控请求硬超时，包含响应正文读取时间。</summary>
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}

public sealed class WalletExecutionOptions
{
    public bool Enabled { get; init; }
    [Required, Url] public string BaseUrl { get; init; } =
        "http://127.0.0.1:18084";
    public string CommandToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>
/// 单个 Allocator 监控来源及拓扑身份。
/// 只读 MonitoringToken 与高风险 ManagementCommandToken 强制分离。
/// </summary>
public sealed class AllocatorMonitoringOptions
{
    public bool Enabled { get; init; } = true;
    [Required] public string ClusterId { get; init; } = "local";
    [Required] public string NodeId { get; init; } = "game-node";
    [Required] public string SourceId { get; init; } = "legacy-allocator";
    [Required] public string RegionId { get; init; } = "local";
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18081";
    public string MonitoringToken { get; init; } = string.Empty;
    public string ManagementCommandToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}
