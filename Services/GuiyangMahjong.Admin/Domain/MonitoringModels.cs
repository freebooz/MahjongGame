using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

public sealed record GameServerRouteSnapshot(
    string ServerInstanceId,
    string MatchId,
    string ServerIp,
    int ServerPort);

public sealed record RoomMonitorSnapshot
{
    public required string RoomId { get; init; }
    public required string RoomCode { get; init; }
    public required string OwnerPlayerId { get; init; }
    public required int RoundCount { get; init; }
    public required bool PublicRoom { get; init; }
    public required bool AutoStart { get; init; }
    public required int MaximumPlayers { get; init; }
    public required Dictionary<string, JsonElement> RuleSnapshot { get; init; }
    public required string Lifecycle { get; init; }
    public required string[] PlayerIds { get; init; }
    public GameServerRouteSnapshot? Route { get; init; }
    public string? LastServerInstanceId { get; init; }
    public string? PendingServerInstanceId { get; init; }
    public required string MatchId { get; init; }
    public required long StateSequence { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public bool NewPlayersProhibited { get; init; }
    public bool MarkedAbnormal { get; init; }
}

public sealed record GameServerInstanceSnapshot(
    string ServerInstanceId,
    string RoomId,
    string MatchId,
    int Port,
    string AdvertisedIp,
    int? ProcessId,
    string State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? RegisteredAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    string BuildVersion,
    string? FailureReason);

public sealed record MonitoredInstance(
    string ClusterId,
    string NodeId,
    GameServerInstanceSnapshot Instance);

public sealed record CountGroup(string Key, int Count);

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
    Dictionary<string, JsonElement> Data);

public sealed record MonitoringOverview(
    DateTimeOffset ObservedAtUtc,
    int TotalRooms,
    int ActiveRooms,
    int AbnormalRooms,
    int TotalConnectedPlayers,
    int DedicatedServerInstances,
    CountGroup[] RoomsByGameMode,
    CountGroup[] RoomsByState,
    CountGroup[] RoomsByCluster);

public sealed record RoomListItem(
    string RoomId,
    string RoomCode,
    string MatchId,
    string GameMode,
    string Lifecycle,
    int PlayerCount,
    int MaximumPlayers,
    int CurrentRound,
    int RoundCount,
    string? ClusterId,
    string? NodeId,
    string? ServerInstanceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long StateSequence);

public sealed record RoomDetail(
    RoomListItem Summary,
    Dictionary<string, JsonElement> Rules,
    string OwnerPlayerId,
    string[] PlayerIds,
    bool PublicRoom,
    bool AutoStart,
    bool NewPlayersProhibited,
    bool MarkedAbnormal,
    MonitoredInstance? DedicatedServer,
    RoomRuntimeTelemetry? Runtime,
    RoomTimelineEvent[] Timeline,
    string TelemetryStatus);

public sealed record AuthPlayerDirectoryItem(
    string PlayerId,
    string DisplayName,
    string Provider,
    string AccountStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    string? CurrentDeviceId,
    string? CurrentMaskedIp,
    int ActiveSessionCount,
    long ControlVersion,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels);

public sealed record AuthSessionMonitor(
    string SessionReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    bool Active);

public sealed record AuthLoginEvent(
    string EventId,
    string PlayerId,
    string DeviceId,
    string MaskedIp,
    string ClientSummary,
    string Outcome,
    DateTimeOffset OccurredAtUtc);

public sealed record AuthPlayerDirectoryDetail(
    AuthPlayerDirectoryItem Player,
    AuthSessionMonitor[] Sessions,
    AuthLoginEvent[] LoginHistory,
    string[] KnownDeviceIds,
    PlayerControlEvent[] ControlHistory);

public sealed record PlayerControlState(
    long Version,
    string AccountStatus,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels,
    DateTimeOffset? RiskLabelsExpireAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PlayerControlEvent(
    string CommandId,
    string PlayerId,
    string ActionType,
    string Reason,
    string TraceId,
    string TicketId,
    string RequestedBy,
    string ApprovedBy,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? RiskLabel,
    int RevokedSessionCount,
    PlayerControlState BeforeState,
    PlayerControlState AfterState);

public sealed record PlayerPresenceSnapshot(
    string PlayerId,
    bool Online,
    DateTimeOffset? LastSeenAtUtc,
    string LobbyId);

public sealed record PlayerMonitorListItem(
    string PlayerId,
    string DisplayName,
    string Provider,
    string AccountStatus,
    bool Online,
    string? CurrentDeviceId,
    string? CurrentMaskedIp,
    string? LobbyId,
    string? RoomId,
    string? RoomCode,
    string? ServerInstanceId,
    double? LatencyMilliseconds,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    int ActiveSessionCount,
    long ControlVersion,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels);

public sealed record PlayerMonitorDetail(
    PlayerMonitorListItem Summary,
    AuthSessionMonitor[] Sessions,
    AuthLoginEvent[] LoginHistory,
    string[] KnownDeviceIds,
    RoomListItem[] RoomHistory,
    RoomTimelineEvent[] DisconnectHistory,
    PlayerControlEvent[] ControlHistory,
    string DataScope);
