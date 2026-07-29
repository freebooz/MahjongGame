using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// Lobby 动态拓扑租约刷新器；注册失败只降级监控发现，不阻断房间主链路，并按固定短周期自动恢复。
/// </summary>
public sealed class LobbyTopologyRegistrationService(
    IHttpClientFactory httpClientFactory,
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider,
    ILogger<LobbyTopologyRegistrationService> logger)
    : BackgroundService
{
    private readonly LobbyOptions lobby = options.Value;
    private readonly string registrationId = Guid.NewGuid().ToString();

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var registration = lobby.TopologyRegistration;
        if (!registration.Enabled) return;
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(registration.RefreshSeconds));
        do
        {
            await RegisterOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>刷新一次租约；使用进程生命周期内稳定的 RegistrationId 保证同代次幂等。</summary>
    private async Task RegisterOnceAsync(CancellationToken cancellationToken)
    {
        var registration = lobby.TopologyRegistration;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(
                    new Uri(registration.AdminBaseUrl.TrimEnd('/') + "/"),
                    "internal/topology/registrations"));
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                registration.RegistrationToken);
            request.Content = JsonContent.Create(new
            {
                registrationId,
                sourceId = registration.SourceId,
                kind = "Lobby",
                regionId = lobby.RegionId,
                clusterId = lobby.ClusterId,
                lobbyId = lobby.LobbyId,
                nodeId = lobby.NodeId,
                baseUrl = registration.PublicBaseUrl,
                generation = registration.Generation,
                registeredAtUtc = timeProvider.GetUtcNow()
            });
            using var response = await httpClientFactory
                .CreateClient()
                .SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                exception,
                "LobbyTopologyRegistrationFailed SourceId={SourceId} RegionId={RegionId} ClusterId={ClusterId}",
                registration.SourceId,
                lobby.RegionId,
                lobby.ClusterId);
        }
    }
}
