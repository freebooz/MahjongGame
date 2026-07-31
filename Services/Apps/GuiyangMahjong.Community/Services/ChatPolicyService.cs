using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Community.Domain;
using GuiyangMahjong.Community.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Community.Services;

/// <summary>聊天发送策略边界；当前消费 Identity 禁言权威状态，后续可替换为事件投影而不改变入口契约。</summary>
public interface IChatPolicyService
{
    Task<ChatPolicyResult> AuthorizeAsync(AuthorizeChatMessageRequest request, CancellationToken cancellationToken);
}

/// <summary>通过受控只读 API 查询 Identity；依赖故障一律失败关闭，禁止默认放行聊天。</summary>
public sealed class AuthBackedChatPolicyService(
    IHttpClientFactory httpClientFactory,
    IOptions<CommunityOptions> options,
    TimeProvider timeProvider,
    ILogger<AuthBackedChatPolicyService> logger) : IChatPolicyService
{
    private readonly CommunityOptions settings = options.Value;

    public async Task<ChatPolicyResult> AuthorizeAsync(
        AuthorizeChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(settings.AuthBaseUrl),
                $"/internal/monitoring/players/{Uri.EscapeDataString(request.PlayerId)}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AuthMonitoringToken);
        try
        {
            using var response = await httpClientFactory.CreateClient(nameof(AuthBackedChatPolicyService))
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(request.PlayerId, false, null, "Player account was not found.");
            if (!response.IsSuccessStatusCode)
                throw new CommunityOperationException("CHAT_POLICY_UNAVAILABLE",
                    "Chat policy is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            if (!body.TryGetProperty("player", out var player))
                throw new CommunityOperationException("CHAT_POLICY_RESPONSE_INVALID",
                    "Chat policy is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
            var mutedUntil = player.TryGetProperty("mutedUntilUtc", out var muted)
                && muted.ValueKind == JsonValueKind.String ? muted.GetDateTimeOffset() : (DateTimeOffset?)null;
            return mutedUntil > timeProvider.GetUtcNow()
                ? new(request.PlayerId, false, mutedUntil, "Player is muted by an approved sanction.")
                : new(request.PlayerId, true, null, "Player may send chat messages.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Identity 聊天策略依赖不可用。ErrorType={ErrorType}", exception.GetType().Name);
            throw new CommunityOperationException("CHAT_POLICY_UNAVAILABLE",
                "Chat policy is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Identity 聊天策略查询超时。ErrorType={ErrorType}", exception.GetType().Name);
            throw new CommunityOperationException("CHAT_POLICY_TIMEOUT",
                "Chat policy is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
    }
}
