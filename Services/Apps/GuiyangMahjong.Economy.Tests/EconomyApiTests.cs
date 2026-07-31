using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Economy.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GuiyangMahjong.Economy.Tests;

/// <summary>以真实 HTTP 管线验证认证、幂等、双人审批、撤销和非负余额约束。</summary>
public sealed class EconomyFactory : WebApplicationFactory<Program>
{
    public const string SourceToken = "economy-source-token-that-is-long-enough-0001";
    public const string AdminToken = "economy-admin-token-that-is-long-enough-00002";
    public const string MonitorToken = "economy-monitor-token-that-is-long-enough-0003";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Economy:PersistenceMode"] = "InMemory",
                ["Economy:SourceIngestionToken"] = SourceToken, ["Economy:AdminCommandToken"] = AdminToken,
                ["Economy:MonitoringToken"] = MonitorToken }));
    }
}

public sealed class EconomyApiTests(EconomyFactory factory) : IClassFixture<EconomyFactory>
{
    [Fact]
    public async Task RewardAndReversalAreAtomicAndIdempotent()
    {
        var player = $"player-{Guid.NewGuid():N}"; var grant = $"reward-{Guid.NewGuid():N}"; var eventId = Guid.NewGuid();
        using var claimed = await Post("/internal/sources/reward-claims", EconomyFactory.SourceToken, eventId,
            new RewardClaimRequest(eventId.ToString(), grant, player, "COIN", 500, DateTimeOffset.UtcNow,
                $"daily:{grant}", $"trace-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Created, claimed.StatusCode);
        using var duplicate = await Post("/internal/sources/reward-claims", EconomyFactory.SourceToken, eventId,
            new RewardClaimRequest(eventId.ToString(), grant, player, "COIN", 500, DateTimeOffset.UtcNow,
                $"daily:{grant}", $"trace-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode); // 同 Key 但时间/Trace 不同必须拒绝，而非误判重复。

        var command = Guid.NewGuid();
        var request = new AdminWalletOperationRequest("RevokeReward", player, Guid.NewGuid().ToString(), null, null,
            grant, "operator-one", "approver-two", "Verified duplicate reward reversal.", "CASE-1001",
            $"trace-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        using var reversed = await Post("/internal/admin/wallet-operations", EconomyFactory.AdminToken, command, request);
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        using var repeated = await Post("/internal/admin/wallet-operations", EconomyFactory.AdminToken, command, request);
        Assert.True((await repeated.Content.ReadFromJsonAsync<WalletOperationResult>())!.Duplicate);
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", EconomyFactory.MonitorToken);
        var balances = await client.GetFromJsonAsync<WalletBalance[]>($"/internal/monitoring/players/{player}/balances");
        Assert.Equal(0, Assert.Single(balances!).Balance);
    }

    [Fact]
    public async Task UnauthorizedAndSameApproverAreRejected()
    {
        using var client = factory.CreateClient();
        using var anonymous = await client.GetAsync("/internal/monitoring/players/player-test/balances");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var request = new AdminWalletOperationRequest("GrantCompensation", "player-test", Guid.NewGuid().ToString(),
            "COIN", 1, null, "same-user", "same-user", "Invalid self approval command.", "CASE-1002",
            "trace-12345678", DateTimeOffset.UtcNow);
        using var rejected = await Post("/internal/admin/wallet-operations", EconomyFactory.AdminToken, Guid.NewGuid(), request);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    private async Task<HttpResponseMessage> Post(string path, string token, Guid key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); request.Headers.Add("Idempotency-Key", key.ToString());
        return await factory.CreateClient().SendAsync(request);
    }
}
