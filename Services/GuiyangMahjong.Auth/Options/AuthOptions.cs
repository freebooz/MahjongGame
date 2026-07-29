using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Auth.Options;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [MinLength(32)] public string TokenSigningKey { get; init; } = string.Empty;
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
