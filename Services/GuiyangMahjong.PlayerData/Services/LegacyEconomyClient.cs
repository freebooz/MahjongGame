using System.Net.Http.Json;
using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Options;
using GuiyangMahjong.PlayerData.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.PlayerData.Services;

/// <summary>阶段 8.3 旧 API 兼容客户端；PlayerData 只转发请求，不再持久化资产和奖励。</summary>
public interface ILegacyEconomyClient
{
    Task<EvidenceRecordResult> ClaimRewardAsync(RewardClaimRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<WalletOperationResult> ApplyWalletOperationAsync(AdminWalletOperationRequest request, string idempotencyKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(string playerId, CancellationToken cancellationToken);
}

/// <summary>使用按用途隔离凭据调用 Economy；任何非成功结果原样转化为稳定的上游错误。</summary>
public sealed class HttpLegacyEconomyClient(IHttpClientFactory factory, IOptions<PlayerDataOptions> options,
    ILogger<HttpLegacyEconomyClient> logger) : ILegacyEconomyClient
{
    public async Task<EvidenceRecordResult> ClaimRewardAsync(RewardClaimRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        logger.LogInformation("旧奖励入口转发至 Economy。Owner={Owner} EventId={EventId}", "Economy/Rewards", request.EventId);
        using var message = Create(HttpMethod.Post, "/internal/sources/reward-claims", options.Value.EconomySourceToken, idempotencyKey);
        message.Content = JsonContent.Create(request);
        using var response = await factory.CreateClient(nameof(HttpLegacyEconomyClient)).SendAsync(message, cancellationToken);
        return await ReadAsync<EvidenceRecordResult>(response, cancellationToken);
    }

    public async Task<WalletOperationResult> ApplyWalletOperationAsync(AdminWalletOperationRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        logger.LogInformation("旧钱包命令入口转发至 Economy。Owner={Owner} CaseId={CaseId}", "Economy/Inventory", request.CaseId);
        using var message = Create(HttpMethod.Post, "/internal/admin/wallet-operations", options.Value.EconomyAdminToken, idempotencyKey);
        message.Content = JsonContent.Create(request);
        using var response = await factory.CreateClient(nameof(HttpLegacyEconomyClient)).SendAsync(message, cancellationToken);
        return await ReadAsync<WalletOperationResult>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(string playerId, CancellationToken cancellationToken)
    {
        using var message = Create(HttpMethod.Get, $"/internal/monitoring/players/{Uri.EscapeDataString(playerId)}/balances", options.Value.EconomyMonitoringToken, null);
        using var response = await factory.CreateClient(nameof(HttpLegacyEconomyClient)).SendAsync(message, cancellationToken);
        return await ReadAsync<WalletBalance[]>(response, cancellationToken);
    }

    private HttpRequestMessage Create(HttpMethod method, string path, string token, string? key)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(options.Value.EconomyBaseUrl), path));
        request.Headers.Authorization = new("Bearer", token);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new PlayerDataOperationException("PLAYER_DATA_UPSTREAM_REJECTED", "Economy rejected the compatibility request.", (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new PlayerDataOperationException("PLAYER_DATA_UPSTREAM_INVALID", "Economy returned an invalid response.", 502);
    }
}
