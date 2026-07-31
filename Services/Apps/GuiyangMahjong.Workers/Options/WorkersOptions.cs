using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Workers.Options;

/// <summary>
/// BackgroundWorkers 的完整运行配置。连接字符串和 NATS 凭据只允许由环境或 Secret 注入；
/// 日志只能输出 Source 名称和 Schema，不得输出连接字符串。
/// </summary>
public sealed class WorkersOptions
{
    public const string SectionName = "Workers";

    /// <summary>Worker 自有数据库连接；保存 Inbox、DLQ、检查点和只读投影。</summary>
    public string PostgresConnectionString { get; init; } = string.Empty;

    /// <summary>本地开发是否由进程执行 Schema；生产必须为 false 并使用迁移身份。</summary>
    public bool ApplyDatabaseMigrations { get; init; }

    /// <summary>JetStream 客户端连接地址；认证信息不得直接写进仓库配置。</summary>
    [Required]
    public string NatsUrl { get; init; } = "nats://nats:4222";

    /// <summary>NATS 工作负载用户名；生产值必须由 Secret 注入，禁止拼进连接 URL 或日志。</summary>
    public string NatsUsername { get; init; } = string.Empty;

    /// <summary>NATS 工作负载密码；仅在创建连接时使用，不参与配置摘要和健康响应。</summary>
    public string NatsPassword { get; init; } = string.Empty;

    /// <summary>Stream 副本数；本地单节点为1，生产三节点必须为3。</summary>
    [Range(1, 5)]
    public int StreamReplicas { get; init; } = 1;

    /// <summary>仅供本地 Compose 显式放宽为单副本；生产集群不得启用该开关。</summary>
    public bool AllowSingleNodeStream { get; init; }

    /// <summary>Stream 最大保留天数，避免无界占用磁盘。</summary>
    [Range(1, 365)]
    public int StreamRetentionDays { get; init; } = 14;

    /// <summary>Stream 最大字节数；超过后按最旧消息淘汰并告警。</summary>
    [Range(1048576, long.MaxValue)]
    public long StreamMaxBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    /// <summary>单次从一个 Outbox Source 领取的上限。</summary>
    [Range(1, 1000)]
    public int OutboxBatchSize { get; init; } = 100;

    /// <summary>Outbox 领取租约秒数；必须长于一次发布超时。</summary>
    [Range(5, 300)]
    public int OutboxLeaseSeconds { get; init; } = 30;

    /// <summary>单条 Outbox 达到该次数后进入 Failed，等待人工处理。</summary>
    [Range(1, 100)]
    public int MaximumPublishAttempts { get; init; } = 12;

    /// <summary>无可发布消息时的轮询间隔。</summary>
    [Range(50, 60000)]
    public int PollIntervalMilliseconds { get; init; } = 500;

    /// <summary>Inbox 已完成记录保留天数；必须覆盖 JetStream 最大重投窗口。</summary>
    [Range(7, 3650)]
    public int InboxRetentionDays { get; init; } = 90;

    /// <summary>已发布 Outbox 进入归档的延迟天数。</summary>
    [Range(1, 365)]
    public int OutboxArchiveAfterDays { get; init; } = 7;

    /// <summary>Consumer Lag 超过该值时触发指标告警。</summary>
    [Range(1, long.MaxValue)]
    public long ConsumerLagWarningThreshold { get; init; } = 1000;

    /// <summary>需要发布的服务自有标准 Outbox；每个 Source 只允许更新其 integration 表。</summary>
    public List<OutboxSourceOptions> OutboxSources { get; init; } = [];

    /// <summary>由 Worker 定时调用的数据所有者维护端点；默认关闭，不直接访问业务表。</summary>
    public MaintenanceOptions SessionCleanup { get; init; } = new();
    public MaintenanceOptions RoomCleanup { get; init; } = new();
}

/// <summary>一个服务自有标准 Outbox 的受控连接配置。</summary>
public sealed class OutboxSourceOptions
{
    /// <summary>低基数来源名，用于日志和指标。</summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>来源服务独占的 integration Schema，必须通过安全标识校验。</summary>
    [Required]
    public string Schema { get; init; } = string.Empty;

    /// <summary>仅具备该 Outbox 领取、标记和归档权限的数据库连接。</summary>
    [Required]
    public string ConnectionString { get; init; } = string.Empty;
}

/// <summary>
/// 数据所有者维护端点配置。Worker 只负责调度，不共享 Auth 或 Lobby 数据库账号；
/// Token 必须由 Secret 注入且不会记录。
/// </summary>
public sealed class MaintenanceOptions
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    [Range(10, 86400)] public int IntervalSeconds { get; init; } = 300;
}
