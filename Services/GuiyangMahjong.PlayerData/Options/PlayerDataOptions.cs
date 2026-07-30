using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.PlayerData.Options;

/// <summary>
/// PlayerData 根配置。
/// 来源接入、Admin 钱包命令、聊天网关、监控和证据投影使用独立凭据；
/// 生产 PostgreSQL 身份遵循最小权限且不执行 DDL。
/// </summary>
public sealed class PlayerDataOptions
{
    public const string SectionName = "PlayerData";

    [Required] public string PersistenceMode { get; init; } = "InMemory";
    /// <summary>是否允许运行进程执行建表；生产环境必须关闭并使用独立迁移身份。</summary>
    public bool ApplyDatabaseMigrations { get; init; } = true;
    public string PostgresConnectionString { get; init; } = string.Empty;
    public string SourceIngestionToken { get; init; } = string.Empty;
    public string AdminCommandToken { get; init; } = string.Empty;
    public string ChatGatewayToken { get; init; } = string.Empty;
    public string MonitoringToken { get; init; } = string.Empty;
    [Required, Url] public string AuthBaseUrl { get; init; } =
        "http://127.0.0.1:18082";
    public string AuthMonitoringToken { get; init; } = string.Empty;
    [Required, Url] public string AdminProjectionBaseUrl { get; init; } =
        "http://127.0.0.1:18083";
    public string AdminEvidenceIngestionToken { get; init; } = string.Empty;
    public bool ProjectionEnabled { get; init; }
    [Range(100, 60000)] public int ProjectionPollMilliseconds { get; init; } =
        1000;
    [Range(1, 50)] public int ProjectionMaxAttempts { get; init; } = 10;
}
