using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Allocator.Options;

public enum AllocatorBackendMode
{
    LocalProcess,
    Agones
}

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
}
