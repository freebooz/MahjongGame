using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

public interface IPlayerDirectoryClient
{
    Task<AuthPlayerDirectoryItem[]> ListPlayersAsync(
        string? search, CancellationToken cancellationToken);
    Task<AuthPlayerDirectoryDetail?> GetPlayerAsync(
        string playerId, CancellationToken cancellationToken);
}

public sealed class HttpPlayerDirectoryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : IPlayerDirectoryClient
{
    public async Task<AuthPlayerDirectoryItem[]> ListPlayersAsync(
        string? search, CancellationToken cancellationToken)
    {
        var source = options.Value.Auth;
        if (!source.Enabled) return [];
        var query = Uri.EscapeDataString(search?.Trim() ?? string.Empty);
        using var response = await SendAsync(
            $"/internal/monitoring/players?limit=2000&search={query}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthPlayerDirectoryItem[]>(
            cancellationToken: cancellationToken) ?? [];
    }

    public async Task<AuthPlayerDirectoryDetail?> GetPlayerAsync(
        string playerId, CancellationToken cancellationToken)
    {
        if (!options.Value.Auth.Enabled) return null;
        using var response = await SendAsync(
            $"/internal/monitoring/players/{Uri.EscapeDataString(playerId)}",
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthPlayerDirectoryDetail>(
            cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path, CancellationToken cancellationToken)
    {
        var source = options.Value.Auth;
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{source.BaseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", source.MonitoringToken);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds));
        return await httpClientFactory.CreateClient(nameof(HttpPlayerDirectoryClient))
            .SendAsync(request, timeout.Token);
    }
}

public sealed class PlayerMonitoringService(
    IPlayerDirectoryClient players,
    ILobbyMonitoringClient lobby)
{
    public async Task<IReadOnlyList<PlayerMonitorListItem>> ListPlayersAsync(
        string? search, CancellationToken cancellationToken)
    {
        var directory = await players.ListPlayersAsync(search, cancellationToken);
        var roomsTask = lobby.ListRoomsAsync(cancellationToken);
        var presenceTask = lobby.GetPlayerPresenceAsync(
            directory.Select(item => item.PlayerId).ToArray(), cancellationToken);
        await Task.WhenAll(roomsTask, presenceTask);
        var rooms = await roomsTask;
        var presence = (await presenceTask).ToDictionary(
            item => item.PlayerId, StringComparer.Ordinal);
        return directory.Select(player =>
            MapPlayer(player, presence.GetValueOrDefault(player.PlayerId),
                FindCurrentRoom(rooms, player.PlayerId), null))
            .OrderByDescending(item => item.Online)
            .ThenByDescending(item => item.LastSeenAtUtc)
            .ToArray();
    }

    public async Task<PlayerMonitorDetail?> GetPlayerAsync(
        string playerId, CancellationToken cancellationToken)
    {
        var directoryTask = players.GetPlayerAsync(playerId, cancellationToken);
        var roomsTask = lobby.ListRoomsAsync(cancellationToken);
        var presenceTask = lobby.GetPlayerPresenceAsync([playerId], cancellationToken);
        await Task.WhenAll(directoryTask, roomsTask, presenceTask);
        var directory = await directoryTask;
        if (directory is null) return null;
        var rooms = (await roomsTask)
            .Where(room => room.PlayerIds.Contains(playerId, StringComparer.Ordinal))
            .OrderByDescending(room => room.UpdatedAtUtc)
            .ToArray();
        var currentRoom = FindCurrentRoom(rooms, playerId);
        var runtime = currentRoom is null
            ? null
            : await lobby.GetRuntimeAsync(currentRoom.RoomId, cancellationToken);
        var playerRuntime = runtime?.Players.FirstOrDefault(item => item.PlayerId == playerId);
        var presence = (await presenceTask).FirstOrDefault();
        var history = rooms.Select(MapRoom).ToArray();
        var eventTasks = rooms.Take(20)
            .Select(room => lobby.ListEventsAsync(room.RoomId, cancellationToken));
        var timelines = await Task.WhenAll(eventTasks);
        var disconnects = timelines.SelectMany(items => items)
            .Where(item => item.EventType == "PlayerConnectionChanged"
                && EventPlayerMatches(item, playerId))
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(200)
            .ToArray();
        return new PlayerMonitorDetail(
            MapPlayer(directory.Player, presence, currentRoom, playerRuntime),
            directory.Sessions,
            directory.LoginHistory,
            directory.KnownDeviceIds,
            history,
            disconnects,
            directory.ControlHistory,
            "ReadOnlyMasked");
    }

    private static PlayerMonitorListItem MapPlayer(
        AuthPlayerDirectoryItem player,
        PlayerPresenceSnapshot? presence,
        RoomMonitorSnapshot? room,
        PlayerRuntimeTelemetry? runtime) =>
        new(
            player.PlayerId,
            player.DisplayName,
            player.Provider,
            player.AccountStatus,
            presence?.Online == true || runtime?.ConnectionState == "Connected",
            player.CurrentDeviceId,
            player.CurrentMaskedIp,
            presence?.LobbyId,
            room?.RoomId,
            room?.RoomCode,
            room is null ? null : GetInstanceId(room),
            runtime?.LatencyMilliseconds,
            player.LastLoginAtUtc,
            presence?.LastSeenAtUtc,
            player.ActiveSessionCount,
            player.ControlVersion,
            player.FrozenUntilUtc,
            player.MutedUntilUtc,
            player.RiskLabels);

    private static RoomMonitorSnapshot? FindCurrentRoom(
        IEnumerable<RoomMonitorSnapshot> rooms, string playerId) =>
        rooms.Where(room => room.PlayerIds.Contains(playerId, StringComparer.Ordinal)
                && room.Lifecycle is "Allocating" or "Waiting" or "Playing" or "Settling")
            .MaxBy(room => room.UpdatedAtUtc);

    private static RoomListItem MapRoom(RoomMonitorSnapshot room) =>
        new(
            room.RoomId,
            room.RoomCode,
            room.MatchId,
            GetGameMode(room),
            room.Lifecycle,
            room.PlayerIds.Length,
            room.MaximumPlayers,
            GetInt32(room.RuleSnapshot, "currentRound"),
            room.RoundCount,
            null,
            null,
            GetInstanceId(room),
            room.CreatedAtUtc,
            room.UpdatedAtUtc,
            room.StateSequence);

    private static string? GetInstanceId(RoomMonitorSnapshot room) =>
        room.Route?.ServerInstanceId ?? room.PendingServerInstanceId ?? room.LastServerInstanceId;

    private static string GetGameMode(RoomMonitorSnapshot room)
    {
        foreach (var key in new[] { "gameMode", "playMode", "variant" })
        {
            if (room.RuleSnapshot.TryGetValue(key, out var value)
                && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "Standard";
        }
        return "Standard";
    }

    private static int GetInt32(
        IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static bool EventPlayerMatches(RoomTimelineEvent roomEvent, string playerId) =>
        roomEvent.Data.TryGetValue("playerId", out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == playerId;
}
