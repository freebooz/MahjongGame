using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.GameData.Infrastructure;

/// <summary>只读核对房间控制面当前权威实例；GameData 不读取 Lobby 数据库。</summary>
public interface ISettlementAuthorityClient
{
    Task<SettlementAuthority> ValidateAsync(
        FinalResultEnvelope envelope,
        string credentialSha256,
        CancellationToken cancellationToken);
    /// <summary>结算已持久化后幂等通知 RoomControl 关闭房间；失败时调用方返回可重试错误。</summary>
    Task NotifyCommittedAsync(SettlementCommitResult result, string roomId, CancellationToken cancellationToken);
}

/// <summary>通过用途隔离内网接口核对凭据摘要、实例、Epoch、版本和参与者。</summary>
public sealed class HttpSettlementAuthorityClient(
    IHttpClientFactory httpClientFactory,
    IOptions<GameDataOptions> options) : ISettlementAuthorityClient
{
    /// <inheritdoc/>
    public async Task<SettlementAuthority> ValidateAsync(
        FinalResultEnvelope envelope,
        string credentialSha256,
        CancellationToken cancellationToken)
    {
        var requestBody = new SettlementAuthorityRequest(
            envelope.MatchId,
            envelope.RoomId,
            envelope.ServerInstanceId,
            envelope.RoomEpoch,
            envelope.RuleSetVersion,
            envelope.ServerBuild,
            credentialSha256);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.Value.LobbyBaseUrl.TrimEnd('/')}/internal/settlement-authority/validate")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", options.Value.LobbyAuthorityToken);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        using var response = await httpClientFactory
            .CreateClient(nameof(HttpSettlementAuthorityClient))
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw Settlement.GameDataException.Unavailable(
                "LOBBY_AUTHORITY_UNAVAILABLE", "房间权威校验暂时不可用");
        return await response.Content.ReadFromJsonAsync<SettlementAuthority>(cancellationToken)
            ?? throw Settlement.GameDataException.Unavailable(
                "LOBBY_AUTHORITY_INVALID", "房间权威校验响应无效");
    }

    /// <inheritdoc/>
    public async Task NotifyCommittedAsync(
        SettlementCommitResult result,
        string roomId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.Value.LobbyBaseUrl.TrimEnd('/')}/internal/settlement-authority/committed")
        {
            Content = JsonContent.Create(new
            {
                result.MatchId,
                RoomId = roomId,
                result.RoundNo,
                result.SettlementVersion,
                result.SettlementId,
                result.CommittedAtUtc
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", options.Value.LobbyAuthorityToken);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        using var response = await httpClientFactory
            .CreateClient(nameof(HttpSettlementAuthorityClient))
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw Settlement.GameDataException.Unavailable(
                "LOBBY_SETTLEMENT_CALLBACK_FAILED", "结算已保存但房间关闭确认暂时失败");
    }
}
