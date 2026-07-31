using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.EdgeGateway.Options;

/// <summary>
/// Player EdgeGateway 的安全、兼容与容量配置。
/// 所有敏感值只允许通过环境变量或密钥注入，配置对象不得写入日志。
/// </summary>
public sealed class EdgeGatewayOptions
{
    /// <summary>ASP.NET Core 配置节名称。</summary>
    public const string SectionName = "EdgeGateway";

    /// <summary>单个请求体上限，单位字节；Kestrel 和网关前置检查共同执行。</summary>
    [Range(1024, 16 * 1024 * 1024)]
    public long MaximumRequestBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>YARP 路由超时，单位毫秒；不触发 POST 自动重试。</summary>
    [Range(100, 120_000)]
    public int RouteTimeoutMilliseconds { get; init; } = 10_000;

    /// <summary>
    /// 玩家入口允许的 Host，使用分号分隔；由网关中间件校验并返回统一 JSON 错误。
    /// ASP.NET Core 顶层 AllowedHosts 固定为通配符，避免框架先返回不可控的 HTML 错误。
    /// </summary>
    [Required]
    public string AllowedHosts { get; init; } =
        "localhost;127.0.0.1;[::1]";

    /// <summary>允许接收 Forwarded Headers 的显式代理 IP；未列入者的相关头会被删除。</summary>
    public string[] TrustedProxies { get; init; } = ["127.0.0.1", "::1"];

    /// <summary>允许接收 Forwarded Headers 的 CIDR 网络；生产必须按实际入口网络收窄。</summary>
    public string[] TrustedProxyNetworks { get; init; } = [];

    /// <summary>客户端契约与发布门禁配置。</summary>
    [Required]
    public ClientContractOptions ClientContract { get; init; } = new();

    /// <summary>玩家访问令牌本地验证配置。</summary>
    [Required]
    public PlayerTokenOptions PlayerTokens { get; init; } = new();

    /// <summary>本机 ASP.NET Core 限流配置。</summary>
    [Required]
    public LocalRateLimitOptions LocalRateLimit { get; init; } = new();

    /// <summary>Redis 分布式限流配置；仅保存可丢失的短期计数。</summary>
    [Required]
    public DistributedRateLimitOptions DistributedRateLimit { get; init; } = new();
}

/// <summary>UE 客户端版本、协议、平台和渠道白名单。</summary>
public sealed class ClientContractOptions
{
    /// <summary>允许访问网关的最低语义版本。</summary>
    [Required]
    public string MinimumClientVersion { get; init; } = "1.0.0";

    /// <summary>网关接受的 API/游戏控制面协议版本。</summary>
    [MinLength(1)]
    public string[] SupportedProtocolVersions { get; init; } = ["1"];

    /// <summary>允许的平台标识，比较忽略大小写。</summary>
    [MinLength(1)]
    public string[] AllowedPlatforms { get; init; } =
        ["Windows", "Android", "IOS", "Linux", "Mac"];

    /// <summary>允许的发行渠道，比较忽略大小写。</summary>
    [MinLength(1)]
    public string[] AllowedChannels { get; init; } =
        ["default", "development", "appstore", "googleplay"];
}

/// <summary>
/// Access Token 双格式验证配置。
/// LegacySigningKey 兼容当前 Auth 两段式令牌；JwtSigningKey 为后续标准 JWT 兼容入口。
/// </summary>
public sealed class PlayerTokenOptions
{
    /// <summary>当前 Auth/Lobby 共享 HMAC 密钥；至少 32 字符。</summary>
    [MinLength(32)]
    public string LegacySigningKey { get; init; } = string.Empty;

    /// <summary>标准 JWT HMAC 密钥；为空时使用 LegacySigningKey，避免要求 Auth 本阶段改签发格式。</summary>
    public string JwtSigningKey { get; init; } = string.Empty;

    /// <summary>JWT 预期签发者；为空表示兼容期不校验 issuer。</summary>
    public string JwtIssuer { get; init; } = string.Empty;

    /// <summary>JWT 预期受众；为空表示兼容期不校验 audience。</summary>
    public string JwtAudience { get; init; } = string.Empty;

    /// <summary>允许的时钟偏差秒数，避免多节点轻微偏差造成误拒绝。</summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 30;
}

/// <summary>进程内固定窗口限流参数，按匿名 IP 或已认证玩家分区。</summary>
public sealed class LocalRateLimitOptions
{
    /// <summary>匿名请求每窗口许可数。</summary>
    [Range(1, 100_000)]
    public int AnonymousPermitLimit { get; init; } = 60;

    /// <summary>玩家请求每窗口许可数。</summary>
    [Range(1, 100_000)]
    public int PlayerPermitLimit { get; init; } = 300;

    /// <summary>固定窗口秒数。</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; init; } = 60;
}

/// <summary>Redis 分布式固定窗口限流参数。</summary>
public sealed class DistributedRateLimitOptions
{
    /// <summary>是否启用跨实例计数；本地测试可关闭，生产建议开启。</summary>
    public bool Enabled { get; init; }

    /// <summary>Redis 连接字符串；启用时必须由部署环境注入。</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>独立限流键前缀，不得与 Lobby 权威或缓存键混用。</summary>
    [Required]
    public string KeyPrefix { get; init; } = "guiyang:edge:ratelimit:v1";

    /// <summary>单窗口许可数。</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; init; } = 1000;

    /// <summary>窗口秒数；键 TTL 会略长于该窗口。</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; init; } = 60;

    /// <summary>Redis 故障时是否失败关闭；生产必须为 true，避免无限流放行。</summary>
    public bool FailClosed { get; init; } = true;
}
