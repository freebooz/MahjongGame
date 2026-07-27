using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

public interface ILobbyMonitoringClient
{
    Task<IReadOnlyList<RoomMonitorSnapshot>> ListRoomsAsync(CancellationToken cancellationToken);
    Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken);
    Task<RoomTimelineEvent[]> ListEventsAsync(
        string roomId, CancellationToken cancellationToken);
    Task<PlayerPresenceSnapshot[]> GetPlayerPresenceAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken);
}

public interface IAllocatorMonitoringClient
{
    Task<IReadOnlyList<MonitoredInstance>> ListInstancesAsync(CancellationToken cancellationToken);
}

public sealed class HttpLobbyMonitoringClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : ILobbyMonitoringClient
{
    public async Task<IReadOnlyList<RoomMonitorSnapshot>> ListRoomsAsync(
        CancellationToken cancellationToken)
    {
        var source = options.Value.Lobby;
        if (!source.Enabled) return [];
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{source.BaseUrl.TrimEnd('/')}/internal/monitoring/rooms?limit=5000");
        AddHeaders(request, source.MonitoringToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds));
        using var response = await httpClientFactory.CreateClient(nameof(HttpLobbyMonitoringClient))
            .SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RoomMonitorSnapshot[]>(
            cancellationToken: timeout.Token) ?? [];
    }

    public async Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken)
    {
        if (!options.Value.Lobby.Enabled) return null;
        var response = await GetAsync(
            $"/internal/monitoring/rooms/{Uri.EscapeDataString(roomId)}/runtime",
            cancellationToken);
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<RoomRuntimeTelemetry>(
                cancellationToken: cancellationToken);
        }
    }

    public async Task<RoomTimelineEvent[]> ListEventsAsync(
        string roomId, CancellationToken cancellationToken)
    {
        if (!options.Value.Lobby.Enabled) return [];
        using var response = await GetAsync(
            $"/internal/monitoring/rooms/{Uri.EscapeDataString(roomId)}/events?limit=200",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RoomTimelineEvent[]>(
            cancellationToken: cancellationToken) ?? [];
    }

    public async Task<PlayerPresenceSnapshot[]> GetPlayerPresenceAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken)
    {
        if (!options.Value.Lobby.Enabled || playerIds.Count == 0) return [];
        var joined = Uri.EscapeDataString(string.Join(',', playerIds.Take(500)));
        using var response = await GetAsync(
            $"/internal/monitoring/player-presence?playerIds={joined}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlayerPresenceSnapshot[]>(
            cancellationToken: cancellationToken) ?? [];
    }

    private async Task<HttpResponseMessage> GetAsync(
        string path, CancellationToken cancellationToken)
    {
        var source = options.Value.Lobby;
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{source.BaseUrl.TrimEnd('/')}{path}");
        AddHeaders(request, source.MonitoringToken);
        return await httpClientFactory.CreateClient(nameof(HttpLobbyMonitoringClient))
            .SendAsync(request, cancellationToken);
    }

    private static void AddHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
    }
}

public sealed class HttpAllocatorMonitoringClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : IAllocatorMonitoringClient
{
    public async Task<IReadOnlyList<MonitoredInstance>> ListInstancesAsync(
        CancellationToken cancellationToken)
    {
        var tasks = options.Value.Allocators.Where(source => source.Enabled).Select(source =>
            ListSourceAsync(source, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(item => item).ToArray();
    }

    private async Task<IReadOnlyList<MonitoredInstance>> ListSourceAsync(
        AllocatorMonitoringOptions source,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{source.BaseUrl.TrimEnd('/')}/internal/instances");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.MonitoringToken);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds));
        using var response = await httpClientFactory.CreateClient(nameof(HttpAllocatorMonitoringClient))
            .SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var instances = await response.Content.ReadFromJsonAsync<GameServerInstanceSnapshot[]>(
            cancellationToken: timeout.Token) ?? [];
        return instances.Select(instance =>
            new MonitoredInstance(source.ClusterId, source.NodeId, instance)).ToArray();
    }
}
