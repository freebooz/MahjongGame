using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Admin.Options;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    [MinLength(32)] public string ReadOnlyAccessToken { get; init; } = string.Empty;
    public AdminPrincipalOptions[] Principals { get; init; } = [];
    [Required] public AdminManagementOptions Management { get; init; } = new();
    [Required] public AuthMonitoringOptions Auth { get; init; } = new();
    [Required] public LobbyMonitoringOptions Lobby { get; init; } = new();
    public AllocatorMonitoringOptions[] Allocators { get; init; } = [];
}

public sealed class AdminPrincipalOptions
{
    [Required, MinLength(3), MaxLength(128)] public string OperatorId { get; init; } = string.Empty;
    [MinLength(32)] public string AccessToken { get; init; } = string.Empty;
    public string[] Roles { get; init; } = [];
}

public sealed class AdminManagementOptions
{
    public bool Enabled { get; init; }
    [Required] public string PersistenceMode { get; init; } = "InMemory";
    public string PostgresConnectionString { get; init; } = string.Empty;
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
}

public sealed class AuthMonitoringOptions
{
    public bool Enabled { get; init; } = true;
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18082";
    public string MonitoringToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}

public sealed class LobbyMonitoringOptions
{
    public bool Enabled { get; init; } = true;
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18080";
    public string MonitoringToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}

public sealed class AllocatorMonitoringOptions
{
    public bool Enabled { get; init; } = true;
    [Required] public string ClusterId { get; init; } = "local";
    [Required] public string NodeId { get; init; } = "game-node";
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18081";
    public string MonitoringToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
}
