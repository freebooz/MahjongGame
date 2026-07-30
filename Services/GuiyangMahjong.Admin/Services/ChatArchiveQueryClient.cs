using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// 独立聊天归档的最小权限查询客户端；调用方必须先完成工单、审批窗口和 scope 校验。
/// </summary>
public interface IChatArchiveQueryClient
{
    /// <summary>
    /// 在已审批 UTC 窗口和字段 scopes 内查询玩家聊天归档。
    /// 返回数量受配置限制；调用前必须完成案件归属、双人审批和 RBAC/ABAC 校验。
    /// </summary>
    Task<JsonElement[]> QueryAsync(
        string playerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken);
}

/// <summary>
/// 通过服务端受限令牌代理聊天查询；不会向浏览器暴露归档地址或凭据。
/// </summary>
public sealed class HttpChatArchiveQueryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : IChatArchiveQueryClient
{
    /// <inheritdoc/>
    public async Task<JsonElement[]> QueryAsync(
        string playerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken)
    {
        var settings = options.Value.ChatArchive;
        if (!settings.Enabled)
            throw new ChatArchiveUnavailableException(
                "Chat archive querying is not configured.");
        var query = new Dictionary<string, object?>
        {
            ["playerId"] = playerId,
            ["fromUtc"] = fromUtc,
            ["toUtc"] = toUtc,
            ["scopes"] = scopes,
            ["limit"] = settings.MaxEntries
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{settings.BaseUrl.TrimEnd('/')}/v1/compliance/messages/query")
        {
            Content = JsonContent.Create(query)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            settings.QueryToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var response = await httpClientFactory
            .CreateClient(nameof(HttpChatArchiveQueryClient))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new ChatArchiveUnavailableException(
                $"Chat archive gateway returned {(int)response.StatusCode}.");
        }
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: timeout.Token);
        var items = document.ValueKind == JsonValueKind.Array
            ? document.EnumerateArray()
            : document.ValueKind == JsonValueKind.Object
                && document.TryGetProperty("items", out var nested)
                && nested.ValueKind == JsonValueKind.Array
                    ? nested.EnumerateArray()
                    : [];
        return items
            .Take(settings.MaxEntries)
            .Select(item => item.Clone())
            .ToArray();
    }
}

/// <summary>聊天归档不可用或拒绝查询时的受控错误，不包含上游响应正文。</summary>
public sealed class ChatArchiveUnavailableException(string message)
    : Exception(message);
