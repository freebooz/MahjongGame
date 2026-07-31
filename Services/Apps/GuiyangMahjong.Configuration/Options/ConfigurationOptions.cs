using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Configuration.Options;

/// <summary>
/// 配置中心启动配置。数据库、签名密钥和服务凭据属于静态敏感配置，只允许由 Secret 或环境变量注入，
/// 绝不能作为动态配置载荷发布给客户端或业务服务。
/// </summary>
public sealed class ConfigurationOptions
{
    public const string SectionName = "ConfigurationService";

    /// <summary>本地测试可使用 InMemory；生产必须使用 PostgreSQL 权威持久化。</summary>
    [Required] public string PersistenceMode { get; init; } = "InMemory";
    /// <summary>配置中心独立运行身份连接串；生产运行身份不得具有 DDL 权限。</summary>
    public string PostgresConnectionString { get; init; } = string.Empty;
    /// <summary>仅本地开发允许执行幂等 Schema；生产由独立 migration Job 执行。</summary>
    public bool ApplyDatabaseMigrations { get; init; } = true;
    /// <summary>配置签名 HMAC 密钥；仅用于增量阶段的服务间验签，不进入配置正文、日志或指标。</summary>
    [MinLength(32)] public string SigningKey { get; init; } = string.Empty;
    /// <summary>Admin 发布命令专用凭据，与服务拉取凭据用途隔离。</summary>
    [MinLength(32)] public string AdminCommandToken { get; init; } = string.Empty;
    /// <summary>服务拉取已发布配置和上报应用结果的只读/回执凭据。</summary>
    [MinLength(32)] public string ServiceReadToken { get; init; } = string.Empty;
    /// <summary>单配置键保留的最近不可变版本数；历史审计不删除，仅控制 LKG 返回窗口。</summary>
    [Range(2, 100)] public int RetainedVersions { get; init; } = 10;
}
