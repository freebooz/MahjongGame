using System.Collections.Concurrent;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Rooms;

namespace GuiyangMahjong.Lobby.Storage;

/// <summary>
/// 本地开发与自动化测试存储；生产环境不得使用。
/// mutationGate 把房间、反向索引、活动玩家及比赛结果的复合更新组成原子临界区，
/// 用于模拟生产唯一约束，但数据随进程退出丢失。
/// </summary>
public sealed class InMemoryLobbyStore : ILobbyStore
{
    // 并发字典承担只读索引；涉及多个集合的一致性变更始终在 mutationGate 内完成。
    private readonly ConcurrentDictionary<string, LobbyRoom> roomsByCode = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> codeById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> matchResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeRoomByPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> activePlayerObservedAtUtc = new(StringComparer.Ordinal);
    private readonly object mutationGate = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <inheritdoc/>
    public Task<CreateRoomResult> TryCreateRoomAsync(LobbyRoom room, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mutationGate)
        {
            if (roomsByCode.ContainsKey(room.RoomCode) || codeById.ContainsKey(room.RoomId))
            {
                return Task.FromResult(new CreateRoomResult(CreateRoomStatus.RoomCodeConflict));
            }

            if (IsActive(room.Lifecycle))
            {
                var conflictingPlayer = room.PlayerIds.FirstOrDefault(activeRoomByPlayer.ContainsKey);
                if (conflictingPlayer is not null)
                {
                    return Task.FromResult(new CreateRoomResult(
                        CreateRoomStatus.PlayerAlreadyActive,
                        GetRoomByIdUnsafe(activeRoomByPlayer[conflictingPlayer])));
                }
            }

            room = NormalizeSeats(room);
            roomsByCode[room.RoomCode] = room;
            codeById[room.RoomId] = room.RoomCode;
            SynchronizeActivePlayersUnsafe(room);
            return Task.FromResult(new CreateRoomResult(CreateRoomStatus.Created));
        }
    }

    /// <inheritdoc/>
    public Task<LobbyRoom?> GetRoomByCodeAsync(string roomCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        roomsByCode.TryGetValue(roomCode, out var room);
        return Task.FromResult(room);
    }

    /// <inheritdoc/>
    public Task<LobbyRoom?> GetRoomByIdAsync(string roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (codeById.TryGetValue(roomId, out var code) && roomsByCode.TryGetValue(code, out var room))
        {
            return Task.FromResult<LobbyRoom?>(room);
        }
        return Task.FromResult<LobbyRoom?>(null);
    }

    /// <inheritdoc/>
    public Task<LobbyRoom?> GetActiveRoomByPlayerAsync(string playerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mutationGate)
        {
            return Task.FromResult(activeRoomByPlayer.TryGetValue(playerId, out var roomId)
                ? GetRoomByIdUnsafe(roomId)
                : null);
        }
    }

    /// <summary>在同一锁内建立玩家到房间的稳定快照，避免监控读取观察到半完成的成员变更。</summary>
    public Task<IReadOnlyDictionary<string, LobbyRoom>> GetActiveRoomsByPlayersAsync(
        IReadOnlyCollection<string> playerIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mutationGate)
        {
            IReadOnlyDictionary<string, LobbyRoom> result = playerIds
                .Distinct(StringComparer.Ordinal)
                .Select(playerId => new
                {
                    PlayerId = playerId,
                    Room = activeRoomByPlayer.TryGetValue(playerId, out var roomId)
                        ? GetRoomByIdUnsafe(roomId)
                        : null
                })
                .Where(item => item.Room is not null)
                .ToDictionary(
                    item => item.PlayerId,
                    item => item.Room!,
                    StringComparer.Ordinal);
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LobbyRoom>> ListPublicRoomsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<LobbyRoom> rooms = roomsByCode.Values
            .Where(room => room.PublicRoom && room.Lifecycle is
                RoomLifecycle.Created
                or RoomLifecycle.Waiting
                or RoomLifecycle.Ready
                or RoomLifecycle.Allocating
                or RoomLifecycle.Starting)
            .OrderByDescending(room => room.CreatedAtUtc)
            .Take(100)
            .ToArray();
        return Task.FromResult(rooms);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LobbyRoom>> ListRoomsForMonitoringAsync(
        int limit,
        DateTimeOffset? afterCreatedAtUtc,
        string? afterRoomId,
        string? lifecycle,
        string? gameMode,
        string? search,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<LobbyRoom> rooms = roomsByCode.Values
            .Where(room => string.IsNullOrWhiteSpace(lifecycle)
                || room.Lifecycle.ToString().Equals(
                    lifecycle,
                    StringComparison.OrdinalIgnoreCase))
            .Where(room => string.IsNullOrWhiteSpace(gameMode)
                || GetMonitoringGameMode(room).Equals(
                    gameMode,
                    StringComparison.OrdinalIgnoreCase))
            .Where(room => string.IsNullOrWhiteSpace(search)
                || room.RoomId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || room.RoomCode.Contains(search, StringComparison.OrdinalIgnoreCase)
                || room.MatchId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || room.PlayerIds.Any(playerId =>
                    playerId.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Where(room => afterCreatedAtUtc is null
                || room.CreatedAtUtc < afterCreatedAtUtc
                || (room.CreatedAtUtc == afterCreatedAtUtc
                    && string.CompareOrdinal(room.RoomId, afterRoomId) < 0))
            .OrderByDescending(room => room.CreatedAtUtc)
            .ThenByDescending(room => room.RoomId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return Task.FromResult(rooms);
    }

    /// <summary>提取监控筛选使用的玩法标识，并兼容现有规则字段命名。</summary>
    private static string GetMonitoringGameMode(LobbyRoom room)
    {
        foreach (var key in new[] { "gameMode", "playMode", "variant" })
        {
            if (!room.RuleSnapshot.TryGetValue(key, out var value))
                continue;
            if (value is string text)
                return text;
            if (value is JsonElement
                {
                    ValueKind: JsonValueKind.String
                } element)
                return element.GetString() ?? "Standard";
        }
        return "Standard";
    }

    /// <inheritdoc/>
    public Task<LobbyRoom?> ReconcileWaitingRoomMembersAsync(
        string roomCode,
        string prospectivePlayerId,
        DateTimeOffset staleBeforeUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mutationGate)
        {
            if (!roomsByCode.TryGetValue(roomCode, out var room)
                || room.Lifecycle is not (
                    RoomLifecycle.Created
                    or RoomLifecycle.Waiting
                    or RoomLifecycle.Ready
                    or RoomLifecycle.Allocating
                    or RoomLifecycle.Starting))
            {
                return Task.FromResult(room);
            }

            var retained = room.PlayerIds.Where(playerId =>
                activeRoomByPlayer.TryGetValue(playerId, out var activeRoomId)
                && activeRoomId == room.RoomId
                && activePlayerObservedAtUtc.TryGetValue(playerId, out var lastObservedAtUtc)
                && lastObservedAtUtc >= staleBeforeUtc).ToArray();
            if (retained.Length == room.PlayerIds.Length) return Task.FromResult<LobbyRoom?>(room);

            foreach (var stalePlayerId in room.PlayerIds.Except(retained, StringComparer.Ordinal))
            {
                activeRoomByPlayer.Remove(stalePlayerId);
                activePlayerObservedAtUtc.Remove(stalePlayerId);
            }

            if (retained.Length == 0)
            {
                if (activeRoomByPlayer.TryGetValue(prospectivePlayerId, out var conflictingRoomId)
                    && conflictingRoomId != room.RoomId)
                {
                    return Task.FromResult<LobbyRoom?>(room);
                }
                retained = [prospectivePlayerId];
                activeRoomByPlayer[prospectivePlayerId] = room.RoomId;
                activePlayerObservedAtUtc[prospectivePlayerId] = observedAtUtc;
            }

            var updated = room with
            {
                OwnerPlayerId = retained.Contains(room.OwnerPlayerId, StringComparer.Ordinal)
                    ? room.OwnerPlayerId
                    : retained[0],
                PlayerIds = retained,
                Seats = NormalizeSeats(room with { PlayerIds = retained }).Seats,
                StateSequence = room.StateSequence + 1,
                UpdatedAtUtc = observedAtUtc
            };
            roomsByCode[roomCode] = updated;
            return Task.FromResult<LobbyRoom?>(updated);
        }
    }

    /// <inheritdoc/>
    public Task RefreshConnectedPlayersAsync(
        string roomId,
        IReadOnlyCollection<string> connectedPlayerIds,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mutationGate)
        {
            foreach (var playerId in connectedPlayerIds)
            {
                if (activeRoomByPlayer.TryGetValue(playerId, out var activeRoomId)
                    && activeRoomId == roomId)
                {
                    activePlayerObservedAtUtc[playerId] = observedAtUtc;
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<AddPlayerResult> TryAddPlayerAsync(
        string roomCode, string playerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mutationGate)
        {
            if (!roomsByCode.TryGetValue(roomCode, out var room))
            {
                return Task.FromResult(new AddPlayerResult(AddPlayerStatus.RoomNotFound, null));
            }
            if (room.PlayerIds.Contains(playerId, StringComparer.Ordinal))
            {
                return Task.FromResult(new AddPlayerResult(AddPlayerStatus.AlreadyMember, room));
            }
            if (activeRoomByPlayer.TryGetValue(playerId, out var activeRoomId)
                && activeRoomId != room.RoomId)
            {
                return Task.FromResult(new AddPlayerResult(
                    AddPlayerStatus.AlreadyInAnotherRoom,
                    GetRoomByIdUnsafe(activeRoomId)));
            }
            if (room.Lifecycle is not (
                RoomLifecycle.Created
                or RoomLifecycle.Waiting
                or RoomLifecycle.Ready
                or RoomLifecycle.Allocating
                or RoomLifecycle.Starting))
            {
                return Task.FromResult(new AddPlayerResult(AddPlayerStatus.RoomClosed, room));
            }
            if (room.NewPlayersProhibited)
            {
                return Task.FromResult(new AddPlayerResult(
                    AddPlayerStatus.AdmissionProhibited,
                    room));
            }
            if (room.PlayerIds.Length >= room.MaximumPlayers)
            {
                return Task.FromResult(new AddPlayerResult(AddPlayerStatus.RoomFull, room));
            }

            var updated = room with
            {
                PlayerIds = [.. room.PlayerIds, playerId],
                Seats =
                [
                    .. NormalizeSeats(room).Seats,
                    new RoomSeat(
                        playerId,
                        Enumerable.Range(0, room.MaximumPlayers)
                            .First(index => !NormalizeSeats(room).Seats
                                .Any(seat => seat.SeatIndex == index)),
                        DateTimeOffset.UtcNow)
                ],
                StateSequence = room.StateSequence + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            roomsByCode[roomCode] = updated;
            activeRoomByPlayer[playerId] = updated.RoomId;
            activePlayerObservedAtUtc[playerId] = updated.UpdatedAtUtc;
            return Task.FromResult(new AddPlayerResult(AddPlayerStatus.Added, updated));
        }
    }

    /// <inheritdoc/>
    public Task<bool> UpdateRoomAsync(LobbyRoom room, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mutationGate)
        {
            if (!roomsByCode.TryGetValue(room.RoomCode, out var current)
                || room.StateSequence != current.StateSequence + 1
                || room.RoomEpoch < current.RoomEpoch
                || HasActivePlayerConflictUnsafe(room))
            {
                return Task.FromResult(false);
            }
            room = NormalizeSeats(room);
            roomsByCode[room.RoomCode] = room;
            codeById[room.RoomId] = room.RoomCode;
            SynchronizeActivePlayersUnsafe(room);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public Task<FinalizeMatchStatus> FinalizeMatchAsync(
        LobbyRoom closedRoom, MatchResultReport report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resultKey = $"{closedRoom.MatchId}:{report.ResultSequence}";
        var payload = JsonSerializer.Serialize(report);
        lock (mutationGate)
        {
            if (matchResults.TryGetValue(resultKey, out var existing))
            {
                return Task.FromResult(existing == payload
                    ? FinalizeMatchStatus.Duplicate
                    : FinalizeMatchStatus.Conflict);
            }
            if (!roomsByCode.TryGetValue(closedRoom.RoomCode, out var current)
                || current.MatchId != closedRoom.MatchId
                || current.StateSequence >= closedRoom.StateSequence)
            {
                return Task.FromResult(FinalizeMatchStatus.Conflict);
            }
            matchResults[resultKey] = payload;
            roomsByCode[closedRoom.RoomCode] = closedRoom;
            codeById[closedRoom.RoomId] = closedRoom.RoomCode;
            SynchronizeActivePlayersUnsafe(closedRoom);
            return Task.FromResult(FinalizeMatchStatus.Accepted);
        }
    }

    private LobbyRoom? GetRoomByIdUnsafe(string roomId) =>
        codeById.TryGetValue(roomId, out var code) && roomsByCode.TryGetValue(code, out var room)
            ? room
            : null;

    private bool HasActivePlayerConflictUnsafe(LobbyRoom room) =>
        IsActive(room.Lifecycle) && room.PlayerIds.Any(playerId =>
            activeRoomByPlayer.TryGetValue(playerId, out var activeRoomId)
            && activeRoomId != room.RoomId);

    private void SynchronizeActivePlayersUnsafe(LobbyRoom room)
    {
        foreach (var playerId in activeRoomByPlayer
                     .Where(pair => pair.Value == room.RoomId && !room.PlayerIds.Contains(pair.Key, StringComparer.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            activeRoomByPlayer.Remove(playerId);
            activePlayerObservedAtUtc.Remove(playerId);
        }

        if (!IsActive(room.Lifecycle))
        {
            foreach (var playerId in activeRoomByPlayer
                         .Where(pair => pair.Value == room.RoomId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                activeRoomByPlayer.Remove(playerId);
                activePlayerObservedAtUtc.Remove(playerId);
            }
            return;
        }

        foreach (var playerId in room.PlayerIds)
        {
            activeRoomByPlayer[playerId] = room.RoomId;
            activePlayerObservedAtUtc.TryAdd(playerId, room.UpdatedAtUtc);
        }
    }

    private static bool IsActive(RoomLifecycle lifecycle) => lifecycle is
        RoomLifecycle.Created
        or RoomLifecycle.Waiting
        or RoomLifecycle.Ready
        or RoomLifecycle.Allocating
        or RoomLifecycle.Starting
        or RoomLifecycle.Playing
        or RoomLifecycle.Suspended
        or RoomLifecycle.Recovering
        or RoomLifecycle.Settling
        or RoomLifecycle.Terminating;

    /// <summary>
    /// 为旧快照补齐显式座位，并保留仍有效且不冲突的既有座位。
    /// 该方法只操作不可变副本，调用方仍负责在同一临界区提交玩家和座位。
    /// </summary>
    private static LobbyRoom NormalizeSeats(LobbyRoom room)
    {
        var assigned = room.Seats
            .Where(seat => room.PlayerIds.Contains(seat.PlayerId, StringComparer.Ordinal))
            .Where(seat => seat.SeatIndex >= 0 && seat.SeatIndex < room.MaximumPlayers)
            .GroupBy(seat => seat.PlayerId, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(seat => seat.SeatIndex)
            .Select(group => group.First())
            .ToList();
        foreach (var playerId in room.PlayerIds)
        {
            if (assigned.Any(seat => seat.PlayerId == playerId))
            {
                continue;
            }

            var seatIndex = Enumerable.Range(0, room.MaximumPlayers)
                .First(index => assigned.All(seat => seat.SeatIndex != index));
            assigned.Add(new RoomSeat(playerId, seatIndex, room.CreatedAtUtc));
        }

        return room with
        {
            Seats = assigned.OrderBy(seat => seat.SeatIndex).ToArray()
        };
    }
}
