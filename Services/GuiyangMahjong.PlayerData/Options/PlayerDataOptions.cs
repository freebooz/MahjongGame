using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.PlayerData.Options;

public sealed class PlayerDataOptions
{
    public const string SectionName = "PlayerData";

    [Required] public string PersistenceMode { get; init; } = "InMemory";
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
