using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// Allocator 动态拓扑租约刷新器；仅公布只读监控地址，注册失败不会扩大权限或停止现有游戏实例。
/// </summary>
public sealed class AllocatorTopologyRegistrationService(
    IHttpClientFactory httpClientFactory,
    IOptions<AllocatorOptions> options,
    TimeProvider timeProvider,
    ILogger<AllocatorTopologyRegistrationService> logger)
    : BackgroundService
{
    private readonly AllocatorOptions allocator = options.Value;
    private readonly string registrationId = Guid.NewGuid().ToString();

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var registration = allocator.TopologyRegistration;
        if (!registration.Enabled) return;
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(registration.RefreshSeconds));
        do
        {
            await RegisterOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>刷新一次租约；失败仅记录受控拓扑属性，绝不输出注册凭据。</summary>
    private async Task RegisterOnceAsync(CancellationToken cancellationToken)
    {
        var registration = allocator.TopologyRegistration;
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
                kind = "Allocator",
                regionId = allocator.RegionId,
                clusterId = allocator.ClusterId,
                lobbyId = allocator.AllocatorId,
                nodeId = allocator.NodeId,
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
                "AllocatorTopologyRegistrationFailed SourceId={SourceId} RegionId={RegionId} ClusterId={ClusterId}",
                registration.SourceId,
                allocator.RegionId,
                allocator.ClusterId);
        }
    }
}
