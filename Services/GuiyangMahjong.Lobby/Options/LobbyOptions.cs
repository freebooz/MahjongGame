using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Lobby.Options;

/// <summary>大厅服务配置。生产环境必须从密钥服务注入 TokenSigningKey。</summary>
public sealed class LobbyOptions
{
    public const string SectionName = "Lobby";

    [Range(1, 1)] public int ProtocolVersion { get; init; } = 1;
    [Range(10, 1000)] public int RoomCodeRetryLimit { get; init; } = 100;
    [Range(1, 4)] public int MaximumPlayersPerRoom { get; init; } = 4;
    [Range(1, 20)] public int PasswordFailureLimit { get; init; } = 5;
    [Range(30, 3600)] public int PasswordFailureWindowSeconds { get; init; } = 300;
    [Range(15, 600)] public int PresenceTimeoutSeconds { get; init; } = 90;
    [Range(15, 600)] public int PlayerReservationTimeoutSeconds { get; init; } = 90;
    [Range(15, 600)] public int EmptyRoomTimeoutSeconds { get; init; } = 90;
    [Range(60, 604800)] public int IdempotencyTtlSeconds { get; init; } = 86400;
    [Range(5, 120)] public int IdempotencyLockSeconds { get; init; } = 30;
    [MinLength(32)] public string TokenSigningKey { get; init; } = string.Empty;
    [MinLength(32)] public string JoinTicketSigningKey { get; init; } = string.Empty;
    [MinLength(32)] public string InternalServiceToken { get; init; } = string.Empty;
    public string MonitoringReadOnlyToken { get; init; } = string.Empty;
    public string ManagementCommandToken { get; init; } = string.Empty;
    [Range(15, 1440)] public int AccessRevocationTtlMinutes { get; init; } = 120;
    public bool EnableHttpsRedirection { get; init; }
    public string[] Announcements { get; init; } = [];
    [Required] public LobbyPersistenceOptions Persistence { get; init; } = new();
    [Required] public AllocatorClientOptions Allocator { get; init; } = new();
}

public sealed class AllocatorClientOptions
{
    public bool Enabled { get; init; }
    [Required, Url] public string BaseUrl { get; init; } = "http://127.0.0.1:18081";
    public string ServiceToken { get; init; } = string.Empty;
    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 5;
    [Required] public string GameServerBuildVersion { get; init; } = "unreal-linux";
}

public sealed class LobbyPersistenceOptions
{
    [Required] public string Mode { get; init; } = "InMemory";
    public string RedisConnectionString { get; init; } = string.Empty;
    public string PostgresConnectionString { get; init; } = string.Empty;
    [Required] public string RedisKeyPrefix { get; init; } = "guiyang:lobby:v1";
}
