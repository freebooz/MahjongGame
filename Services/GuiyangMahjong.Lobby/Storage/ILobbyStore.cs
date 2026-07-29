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
    AdmissionProhibited,
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
    /// <summary>
    /// 批量查询玩家当前活动房间；用于监控分页，避免逐玩家查询或扫描全部房间。
    /// 返回字典只包含当前确实位于活动房间中的玩家。
    /// </summary>
    Task<IReadOnlyDictionary<string, LobbyRoom>> GetActiveRoomsByPlayersAsync(
        IReadOnlyCollection<string> playerIds,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<LobbyRoom>> ListPublicRoomsAsync(CancellationToken cancellationToken);
    /// <summary>按不可变创建时间与 RoomId 执行键集分页，避免状态更新时间变化扰动翻页边界。</summary>
    Task<IReadOnlyList<LobbyRoom>> ListRoomsForMonitoringAsync(
        int limit,
        DateTimeOffset? afterCreatedAtUtc,
        string? afterRoomId,
        string? lifecycle,
        string? gameMode,
        string? search,
        CancellationToken cancellationToken);
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
