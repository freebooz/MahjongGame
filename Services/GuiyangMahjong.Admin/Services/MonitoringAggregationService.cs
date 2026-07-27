using System.Text.Json;
using GuiyangMahjong.Admin.Domain;

namespace GuiyangMahjong.Admin.Services;

public sealed class MonitoringAggregationService(
    ILobbyMonitoringClient lobby,
    IAllocatorMonitoringClient allocator,
    TimeProvider timeProvider)
{
    public async Task<MonitoringOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var (rooms, instances) = await LoadAsync(cancellationToken);
        var instanceById = instances.ToDictionary(
            item => item.Instance.ServerInstanceId, StringComparer.Ordinal);
        var active = rooms.Count(room =>
            room.Lifecycle is "Allocating" or "Waiting" or "Playing" or "Settling");
        var abnormal = rooms.Count(room =>
                room.Lifecycle == "Failed" || room.MarkedAbnormal)
            + instances.Count(item => item.Instance.State == "Failed");
        return new MonitoringOverview(
            timeProvider.GetUtcNow(),
            rooms.Count,
            active,
            abnormal,
            rooms.Where(room => room.Lifecycle is not "Closed" and not "Failed")
                .Sum(room => room.PlayerIds.Length),
            instances.Count,
            Group(rooms.Select(GetGameMode)),
            Group(rooms.Select(room => room.Lifecycle)),
            Group(rooms.Select(room =>
            {
                var id = GetInstanceId(room);
                return id is not null && instanceById.TryGetValue(id, out var instance)
                    ? instance.ClusterId
                    : "Unassigned";
            })));
    }

    public async Task<IReadOnlyList<RoomListItem>> ListRoomsAsync(
        string? lifecycle,
        string? gameMode,
        string? search,
        CancellationToken cancellationToken)
    {
        var (rooms, instances) = await LoadAsync(cancellationToken);
        var instanceById = instances.ToDictionary(
            item => item.Instance.ServerInstanceId, StringComparer.Ordinal);
        return rooms
            .Where(room => string.IsNullOrWhiteSpace(lifecycle)
                || room.Lifecycle.Equals(lifecycle, StringComparison.OrdinalIgnoreCase))
            .Where(room => string.IsNullOrWhiteSpace(gameMode)
                || GetGameMode(room).Equals(gameMode, StringComparison.OrdinalIgnoreCase))
            .Where(room => string.IsNullOrWhiteSpace(search)
                || room.RoomId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || room.RoomCode.Contains(search, StringComparison.OrdinalIgnoreCase)
                || room.MatchId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || room.PlayerIds.Any(player =>
                    player.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(room => room.UpdatedAtUtc)
            .Select(room => MapRoom(room, instanceById))
            .ToArray();
    }

    public async Task<RoomDetail?> GetRoomAsync(
        string roomId, CancellationToken cancellationToken)
    {
        var (rooms, instances) = await LoadAsync(cancellationToken);
        var room = rooms.FirstOrDefault(item =>
            item.RoomId.Equals(roomId, StringComparison.Ordinal)
            || item.RoomCode.Equals(roomId, StringComparison.Ordinal));
        if (room is null) return null;
        var instanceById = instances.ToDictionary(
            item => item.Instance.ServerInstanceId, StringComparer.Ordinal);
        var serverId = GetInstanceId(room);
        var server = serverId is not null && instanceById.TryGetValue(serverId, out var found)
            ? found
            : null;
        var runtimeTask = lobby.GetRuntimeAsync(room.RoomId, cancellationToken);
        var timelineTask = lobby.ListEventsAsync(room.RoomId, cancellationToken);
        await Task.WhenAll(runtimeTask, timelineTask);
        var runtime = await runtimeTask;
        return new RoomDetail(
            MapRoom(room, instanceById),
            room.RuleSnapshot,
            room.OwnerPlayerId,
            room.PlayerIds,
            room.PublicRoom,
            room.AutoStart,
            room.NewPlayersProhibited,
            room.MarkedAbnormal,
            server,
            runtime,
            await timelineTask,
            runtime is null ? "AwaitingHeartbeat" : "Realtime");
    }

    public Task<IReadOnlyList<MonitoredInstance>> ListInstancesAsync(
        CancellationToken cancellationToken) => allocator.ListInstancesAsync(cancellationToken);

    private async Task<(IReadOnlyList<RoomMonitorSnapshot> Rooms,
        IReadOnlyList<MonitoredInstance> Instances)> LoadAsync(CancellationToken cancellationToken)
    {
        var roomsTask = lobby.ListRoomsAsync(cancellationToken);
        var instancesTask = allocator.ListInstancesAsync(cancellationToken);
        await Task.WhenAll(roomsTask, instancesTask);
        return (await roomsTask, await instancesTask);
    }

    private static CountGroup[] Group(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CountGroup(group.Key, group.Count()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

    private static RoomListItem MapRoom(
        RoomMonitorSnapshot room,
        IReadOnlyDictionary<string, MonitoredInstance> instances)
    {
        var serverId = GetInstanceId(room);
        instances.TryGetValue(serverId ?? string.Empty, out var server);
        return new RoomListItem(
            room.RoomId,
            room.RoomCode,
            room.MatchId,
            GetGameMode(room),
            room.Lifecycle,
            room.PlayerIds.Length,
            room.MaximumPlayers,
            GetInt32(room.RuleSnapshot, "currentRound"),
            room.RoundCount,
            server?.ClusterId,
            server?.NodeId,
            serverId,
            room.CreatedAtUtc,
            room.UpdatedAtUtc,
            room.StateSequence);
    }

    private static string? GetInstanceId(RoomMonitorSnapshot room) =>
        room.Route?.ServerInstanceId ?? room.PendingServerInstanceId ?? room.LastServerInstanceId;

    private static string GetGameMode(RoomMonitorSnapshot room)
    {
        foreach (var key in new[] { "gameMode", "playMode", "variant" })
        {
            if (room.RuleSnapshot.TryGetValue(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "Standard";
            }
        }
        return "Standard";
    }

    private static int GetInt32(
        IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
