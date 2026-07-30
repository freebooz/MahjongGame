using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>把 Dedicated Server 终态故障通知 Lobby 的内部回调边界。</summary>
public interface IInstanceFailureNotifier
{
    /// <summary>发送脱敏故障通知；失败抛出以便实例管理器保留重试状态。</summary>
    Task NotifyAsync(InstanceFailureNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// 使用独立 LobbyCallbackToken 调用 Lobby 故障入口的 HTTP 实现。
/// RequestId 每次发送唯一，失败不伪装成功；凭据不记录。
/// </summary>
public sealed class LobbyInstanceFailureNotifier(
    IHttpClientFactory httpClientFactory,
    IOptions<AllocatorOptions> options,
    ILogger<LobbyInstanceFailureNotifier> logger) : IInstanceFailureNotifier
{
    private readonly AllocatorOptions options = options.Value;

    /// <inheritdoc/>
    public async Task NotifyAsync(InstanceFailureNotification notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.LobbyInternalUrl)) return;
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.LobbyInternalUrl.TrimEnd('/')}/internal/gameservers/failure")
        {
            Content = JsonContent.Create(notification)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.LobbyCallbackToken);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        using var response = await httpClientFactory.CreateClient(nameof(LobbyInstanceFailureNotifier))
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "大厅拒绝实例失败回调 InstanceId={InstanceId} Status={Status}",
                notification.ServerInstanceId, (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }
    }
}
