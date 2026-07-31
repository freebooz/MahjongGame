using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Options;
using GuiyangMahjong.PlayerData.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.PlayerData.Services;

/// <summary>阶段 8.4 旧聊天 URL 的窄客户端；PlayerData 不再读取 Identity 或自行判定禁言。</summary>
public interface ILegacyCommunityChatClient
{
    Task<ChatPolicyResult> AuthorizeAsync(AuthorizeChatMessageRequest request, CancellationToken cancellationToken);
}

/// <summary>将已校验请求转发给 Community；POST 不透明重试，网络或协议失败采用失败关闭。</summary>
public sealed class HttpLegacyCommunityChatClient(
    IHttpClientFactory httpClientFactory,
    IOptions<PlayerDataOptions> options,
    ILogger<HttpLegacyCommunityChatClient> logger) : ILegacyCommunityChatClient
{
    public async Task<ChatPolicyResult> AuthorizeAsync(
        AuthorizeChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(options.Value.CommunityBaseUrl), "/internal/chat/messages/authorize"))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.CommunityLegacyChatToken);
        try
        {
            using var response = await httpClientFactory.CreateClient(nameof(HttpLegacyCommunityChatClient))
                .SendAsync(message, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.OK or (System.Net.HttpStatusCode)423)
                return await response.Content.ReadFromJsonAsync<ChatPolicyResult>(cancellationToken: cancellationToken)
                    ?? throw Unavailable("Community returned an invalid chat policy response.");
            throw Unavailable("Community rejected the chat policy request.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Community 聊天授权依赖不可用。ErrorType={ErrorType}", exception.GetType().Name);
            throw Unavailable("Community chat policy is temporarily unavailable.");
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Community 聊天授权请求超时。ErrorType={ErrorType}", exception.GetType().Name);
            throw Unavailable("Community chat policy is temporarily unavailable.");
        }
    }

    private static PlayerDataOperationException Unavailable(string message) =>
        new("COMMUNITY_CHAT_UNAVAILABLE", message, StatusCodes.Status503ServiceUnavailable);
}
