using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// Admin 到 Configuration Service 的用途隔离客户端。它只代理受控 API，不访问配置数据库，
/// 且对草稿、发布、回滚等 POST 禁止透明重试，避免重复审批副作用。
/// </summary>
public sealed class ConfigurationManagementClient(
    IHttpClientFactory clients,
    IOptions<AdminOptions> options)
{
    private readonly ConfigurationManagementOptions settings = options.Value.Configuration;

    /// <summary>列出草稿或版本；上游故障只降级配置面板，不影响房间与玩家监控。</summary>
    public Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, null, null, cancellationToken);

    /// <summary>创建、验证、发布或回滚；Idempotency-Key 原样转发并由 Configuration 权威持久化。</summary>
    public Task<JsonElement> PostAsync(
        string path, object? body, string operatorId, string idempotencyKey, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, body, (operatorId, idempotencyKey), cancellationToken);

    private async Task<JsonElement> SendAsync(
        HttpMethod method, string path, object? body, (string OperatorId, string IdempotencyKey)? command,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled) throw new AdminOperationException("CONFIGURATION_MANAGEMENT_DISABLED", "配置治理功能未启用。", 503);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var client = clients.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(settings.BaseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.CommandToken);
        if (command is { } value)
        {
            request.Headers.TryAddWithoutValidation("X-Operator-Id", value.OperatorId);
            request.Headers.TryAddWithoutValidation("Idempotency-Key", value.IdempotencyKey);
        }
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new AdminOperationException(
                json.TryGetProperty("title", out var title) ? title.GetString() ?? "CONFIGURATION_UPSTREAM_ERROR" : "CONFIGURATION_UPSTREAM_ERROR",
                "配置服务拒绝了受控请求。", (int)response.StatusCode);
        return json;
    }
}
