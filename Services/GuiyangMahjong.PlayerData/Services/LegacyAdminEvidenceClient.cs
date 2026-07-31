using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Options;
using GuiyangMahjong.PlayerData.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.PlayerData.Services;

/// <summary>阶段 8.5 剩余证据兼容客户端；PlayerData 不再保存举报或支付投影。</summary>
public interface ILegacyAdminEvidenceClient
{
    Task<EvidenceRecordResult> IngestAsync(RecordEvidenceRequest request, CancellationToken cancellationToken);
}

/// <summary>把经过旧接口校验的脱敏证据直接交给 Admin/TrustSafety 读模型；POST 不透明重试。</summary>
public sealed class HttpLegacyAdminEvidenceClient(
    IHttpClientFactory httpClientFactory,
    IOptions<PlayerDataOptions> options,
    ILogger<HttpLegacyAdminEvidenceClient> logger) : ILegacyAdminEvidenceClient
{
    public async Task<EvidenceRecordResult> IngestAsync(
        RecordEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(options.Value.AdminProjectionBaseUrl), "/internal/projections/player-evidence"))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AdminEvidenceIngestionToken);
        message.Headers.Add("Idempotency-Key", Guid.Parse(request.EventId).ToString());
        try
        {
            using var response = await httpClientFactory.CreateClient(nameof(HttpLegacyAdminEvidenceClient))
                .SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new PlayerDataOperationException("ADMIN_EVIDENCE_REJECTED",
                    "Admin evidence owner rejected the compatibility request.",
                    response.StatusCode == System.Net.HttpStatusCode.Conflict ? 409 : 503);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var duplicate = body.TryGetProperty("duplicate", out var value) && value.GetBoolean();
            logger.LogInformation("旧证据入口已转发至专用读模型。EvidenceType={EvidenceType} EventId={EventId}",
                request.EvidenceType, request.EventId);
            return new EvidenceRecordResult(request.EventId, duplicate);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Admin证据摄取依赖不可用。ErrorType={ErrorType}", exception.GetType().Name);
            throw new PlayerDataOperationException("ADMIN_EVIDENCE_UNAVAILABLE",
                "Admin evidence owner is temporarily unavailable.", 503);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Admin证据摄取请求超时。ErrorType={ErrorType}", exception.GetType().Name);
            throw new PlayerDataOperationException("ADMIN_EVIDENCE_TIMEOUT",
                "Admin evidence owner is temporarily unavailable.", 503);
        }
    }
}
