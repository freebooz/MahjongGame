using System.Text.Json.Serialization;

namespace GuiyangMahjong.Lobby.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<RoomLifecycle>))]
public enum RoomLifecycle
{
    Creating,
    Allocating,
    Waiting,
    Playing,
    Settling,
    Closed,
    Failed
}

public enum LobbyErrorCode
{
    InvalidRequest,
    SessionExpired,
    RequestInProgress,
    RoomNotFound,
    RoomFull,
    RoomClosed,
    PasswordRequired,
    WrongPassword,
    RateLimited,
    ServerUnavailable,
    TicketExpired,
    VersionMismatch,
    Timeout,
    Cancelled,
    BackendNotConfigured,
    InternalError
}

public sealed record PlayerIdentity(string PlayerId, string DisplayName, string Provider);

public sealed record ProtectedPassword(string SaltBase64, string HashBase64, int Iterations);

public sealed record GameServerRoute(
    string RequestId,
    string PlayerId,
    string RoomId,
    string ServerInstanceId,
    string MatchId,
    string ServerIp,
    int ServerPort,
    string JoinTicket,
    DateTimeOffset TicketExpireAtUtc);

public sealed record LobbyRoom
{
    public required string RoomId { get; init; }
    public required string RoomCode { get; init; }
    public required string OwnerPlayerId { get; init; }
    public required int RoundCount { get; init; }
    public required bool PublicRoom { get; init; }
    public required bool AutoStart { get; init; }
    public required int MaximumPlayers { get; init; }
    public required Dictionary<string, object?> RuleSnapshot { get; init; }
    public required RoomLifecycle Lifecycle { get; init; }
    public required string[] PlayerIds { get; init; }
    public ProtectedPassword? Password { get; init; }
    public GameServerRoute? Route { get; init; }
    public string? LastServerInstanceId { get; init; }
    public string? ResultCredentialHash { get; init; }
    public string? PendingServerInstanceId { get; init; }
    public string MatchId { get; init; } = Guid.Empty.ToString();
    public long StateSequence { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? EmptySinceUtc { get; init; }
}

public sealed record CreateRoomRequest(
    int RoundCount,
    bool PublicRoom,
    bool AutoStart,
    bool PasswordProtected,
    string? Password,
    Dictionary<string, object?> RuleSnapshot);

public sealed record JoinRoomRequest(string? Password, int ClientProtocolVersion);
public sealed record ReconnectRouteRequest(string? RoomId = null, string? MatchId = null);

public sealed record MatchPlayerResult(
    string PlayerId,
    int SeatIndex,
    int Rank,
    int TotalScore);

public sealed record MatchResultReport(
    string RoomId,
    string ServerInstanceId,
    long ResultSequence,
    int CompletedRounds,
    MatchPlayerResult[] Players);

public sealed record MatchResultAck(
    string RequestId,
    string MatchId,
    long ResultSequence,
    bool Accepted,
    bool Duplicate);

public sealed record GameServerRegistration(
    string ServerInstanceId,
    string RoomId,
    string MatchId,
    string ListenIp,
    int ListenPort,
    string BuildVersion,
    string RegistrationCredential);

public sealed record GameServerRegistrationAck(
    string RequestId,
    bool Accepted,
    int HeartbeatIntervalSeconds,
    string HeartbeatCredential,
    string ResultCredential,
    ManagedRoomBootstrap RoomBootstrap);

public sealed record ManagedRoomBootstrap(
    string RoomId,
    string RoomCode,
    string MatchId,
    string OwnerPlayerId,
    int RoundCount,
    int MaximumPlayers,
    bool PublicRoom,
    bool AutoStart,
    bool PasswordProtected,
    Dictionary<string, object?> RuleSnapshot);

public sealed record GameServerHeartbeat(
    string RoomId,
    string HeartbeatCredential,
    int ConnectedPlayers,
    string RoomLifecycle,
    int RoundId,
    string BuildVersion,
    DateTimeOffset SentAtUtc,
    string[]? ConnectedPlayerIds = null,
    DateTimeOffset? GameStartedAtUtc = null,
    double? ServerTickMilliseconds = null,
    double? ServerFramesPerSecond = null,
    long? RpcReceivedCount = null,
    long? ProcessMemoryBytes = null,
    double? ProcessCpuPercent = null,
    long? NetworkIngressBytes = null,
    long? NetworkEgressBytes = null,
    PlayerRuntimeTelemetry[]? Players = null);

public sealed record PlayerRuntimeTelemetry(
    string PlayerId,
    int SeatIndex,
    string ConnectionState,
    double? LatencyMilliseconds,
    DateTimeOffset? DisconnectedAtUtc,
    bool? Trustee);

public sealed record RoomRuntimeTelemetry(
    string RoomId,
    string ServerInstanceId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? GameStartedAtUtc,
    string Lifecycle,
    int CurrentRound,
    int ConnectedPlayers,
    double? ServerTickMilliseconds,
    double? ServerFramesPerSecond,
    long? RpcReceivedCount,
    long? ProcessMemoryBytes,
    double? ProcessCpuPercent,
    long? NetworkIngressBytes,
    long? NetworkEgressBytes,
    string BuildVersion,
    PlayerRuntimeTelemetry[] Players);

public sealed record RoomTimelineEvent(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    long StateSequence,
    string TraceId,
    Dictionary<string, object?> Data);

public sealed record PlayerPresenceSnapshot(
    string PlayerId,
    bool Online,
    DateTimeOffset? LastSeenAtUtc,
    string LobbyId);

public sealed record GameServerFailure(
    string ServerInstanceId,
    string RoomId,
    string Reason);

public sealed record RoomOperation(
    string RequestId,
    string RoomId,
    string RoomCode,
    RoomLifecycle Lifecycle,
    int RetryAfterMilliseconds = 1000);

public sealed record LobbyBootstrapResponse(
    string RequestId,
    string PlayerId,
    string DisplayName,
    int OnlinePlayerCount,
    string[] Announcements,
    int ProtocolVersion);

public sealed record RoomDirectoryItem(
    string RoomCode,
    RoomLifecycle Lifecycle,
    int PlayerCount,
    int MaximumPlayers,
    bool PasswordProtected,
    int RoundCount);

public sealed record ApiError(string RequestId, string Code, string Message, int? RetryAfterMilliseconds = null);

public sealed record LobbyEventEnvelope(
    string Type,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    object Data);
