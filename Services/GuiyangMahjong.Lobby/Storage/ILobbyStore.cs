using GuiyangMahjong.Lobby.Domain;

namespace GuiyangMahjong.Lobby.Storage;

public enum CreateRoomStatus
{
    Created,
    RoomCodeConflict,
    PlayerAlreadyActive
}

public sealed record CreateRoomResult(CreateRoomStatus Status, LobbyRoom? ActiveRoom = null);

public enum AddPlayerStatus
{
    Added,
    AlreadyMember,
    RoomNotFound,
    RoomClosed,
    RoomFull,
    AlreadyInAnotherRoom
}

public sealed record AddPlayerResult(AddPlayerStatus Status, LobbyRoom? Room);

public enum FinalizeMatchStatus
{
    Accepted,
    Duplicate,
    Conflict
}

public interface ILobbyStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task<CreateRoomResult> TryCreateRoomAsync(LobbyRoom room, CancellationToken cancellationToken);
    Task<LobbyRoom?> GetRoomByCodeAsync(string roomCode, CancellationToken cancellationToken);
    Task<LobbyRoom?> GetRoomByIdAsync(string roomId, CancellationToken cancellationToken);
    Task<LobbyRoom?> GetActiveRoomByPlayerAsync(string playerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LobbyRoom>> ListPublicRoomsAsync(CancellationToken cancellationToken);
    Task<LobbyRoom?> ReconcileWaitingRoomMembersAsync(
        string roomCode,
        string prospectivePlayerId,
        DateTimeOffset staleBeforeUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
    Task RefreshConnectedPlayersAsync(
        string roomId,
        IReadOnlyCollection<string> connectedPlayerIds,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
    Task<AddPlayerResult> TryAddPlayerAsync(
        string roomCode, string playerId, CancellationToken cancellationToken);
    Task<bool> UpdateRoomAsync(LobbyRoom room, CancellationToken cancellationToken);
    Task<FinalizeMatchStatus> FinalizeMatchAsync(
        LobbyRoom closedRoom, MatchResultReport report, CancellationToken cancellationToken);
}
