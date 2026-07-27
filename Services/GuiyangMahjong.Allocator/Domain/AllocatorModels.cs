using System.Text.Json.Serialization;

namespace GuiyangMahjong.Allocator.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<GameServerInstanceState>))]
public enum GameServerInstanceState
{
    Starting,
    Ready,
    Allocated,
    Draining,
    Stopped,
    Failed
}

public sealed record AllocationRequest(string RoomId, string MatchId, string BuildVersion);

public sealed record AllocationResponse(
    string RequestId,
    string RoomId,
    string ServerInstanceId,
    int Port,
    GameServerInstanceState State);

public sealed record ConfirmRegistrationRequest(
    string RoomId,
    string ListenIp,
    int ListenPort,
    string BuildVersion,
    string RegistrationCredential);

public sealed record ConfirmRegistrationResponse(
    string RequestId,
    string ServerInstanceId,
    bool Accepted,
    int HeartbeatIntervalSeconds,
    string HeartbeatCredential);

public sealed record InstanceHeartbeatRequest(
    string RoomId,
    string HeartbeatCredential,
    int ConnectedPlayers,
    string RoomLifecycle,
    int RoundId,
    string BuildVersion,
    DateTimeOffset SentAtUtc);

public sealed record GameServerInstanceSnapshot(
    string ServerInstanceId,
    string RoomId,
    string MatchId,
    int Port,
    string AdvertisedIp,
    int? ProcessId,
    GameServerInstanceState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? RegisteredAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    string BuildVersion,
    string? FailureReason);

public sealed record InstanceFailureNotification(
    string ServerInstanceId,
    string RoomId,
    string Reason);

public sealed record AdminTerminateInstanceRequest(
    string ExpectedState,
    string Reason,
    string TraceId);

public sealed record AdminTerminateInstanceResult(
    string CommandId,
    GameServerInstanceSnapshot Instance,
    bool AlreadyStopped);

internal sealed class GameServerInstance
{
    public required string ServerInstanceId { get; init; }
    public required string RoomId { get; init; }
    public required string MatchId { get; init; }
    public required int Port { get; init; }
    public required string AdvertisedIp { get; init; }
    public required byte[] RegistrationCredentialHash { get; set; }
    public byte[]? HeartbeatCredentialHash { get; set; }
    public required DateTimeOffset RegistrationExpireAtUtc { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? RegisteredAtUtc { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public required string BuildVersion { get; init; }
    public GameServerInstanceState State { get; set; } = GameServerInstanceState.Starting;
    public IManagedGameServerProcess? Process { get; set; }
    public DateTimeOffset? ProcessStartedAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public bool FailureNotified { get; set; }
    public DateTimeOffset? FailureNotificationAttemptedAtUtc { get; set; }
    public bool PortReleased { get; set; }
    public string? OrchestratorResourceName { get; set; }

    public GameServerInstanceSnapshot Snapshot() => new(
        ServerInstanceId,
        RoomId,
        MatchId,
        Port,
        AdvertisedIp,
        Process?.ProcessId,
        State,
        StartedAtUtc,
        RegisteredAtUtc,
        LastHeartbeatAtUtc,
        BuildVersion,
        FailureReason);
}

public sealed class AllocatorOperationException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
