using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Auth.Options;

/// <summary>
/// Auth 根配置。
/// 签名密钥和游客身份 Pepper 至少 32 字符且只驻留服务端；
/// 生产运行身份不执行 DDL，监控只读与管理命令凭据必须分离。
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [MinLength(32)] public string TokenSigningKey { get; init; } = string.Empty;
    /// <summary>
    /// 当前签名密钥的非敏感稳定标识，用于发布验证元数据和轮换审计；
    /// 该值不是密钥，也不得由客户端用于决定是否信任令牌。
    /// </summary>
    [Required] public string ActiveSigningKeyId { get; init; } = "primary";
    [MinLength(32)] public string GuestIdentityPepper { get; init; } = string.Empty;
    [Range(1, 60)] public int AccessTokenMinutes { get; init; } = 15;
    [Range(1, 90)] public int RefreshTokenDays { get; init; } = 30;
    [Required] public string PersistenceMode { get; init; } = "InMemory";
    /// <summary>
    /// 是否允许运行进程执行数据库结构迁移。仅用于本地开发和测试；生产环境必须关闭，
    /// 由独立 migration 身份在发布阶段执行，避免 Auth 运行身份获得 DDL 权限。
    /// </summary>
    public bool ApplyDatabaseMigrations { get; init; } = true;
    public string PostgresConnectionString { get; init; } = string.Empty;
    public string MonitoringReadOnlyToken { get; init; } = string.Empty;
    public string ManagementCommandToken { get; init; } = string.Empty;
    public bool EnableHttpsRedirection { get; init; }
}
