using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Economy.Options;

/// <summary>Economy 启动配置；三类调用方使用互不复用的工作负载凭据。</summary>
public sealed class EconomyOptions
{
    public const string SectionName = "Economy";
    [Required] public string PersistenceMode { get; init; } = "InMemory";
    public bool ApplyDatabaseMigrations { get; init; } = true;
    public string PostgresConnectionString { get; init; } = string.Empty;
    public string SourceIngestionToken { get; init; } = string.Empty;
    public string AdminCommandToken { get; init; } = string.Empty;
    public string MonitoringToken { get; init; } = string.Empty;
}
