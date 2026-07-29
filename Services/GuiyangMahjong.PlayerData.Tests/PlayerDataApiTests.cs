using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GuiyangMahjong.PlayerData.Tests;

public sealed class PlayerDataWebApplicationFactory
    : WebApplicationFactory<Program>
{
    public const string SourceToken =
        "player-data-source-token-that-is-long-enough-01";
    public const string AdminToken =
        "player-data-admin-token-that-is-long-enough-002";
    public const string ChatToken =
        "player-data-chat-token-that-is-long-enough-0003";
    public const string MonitoringToken =
        "player-data-monitor-token-that-is-long-enough-04";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["PlayerData:PersistenceMode"] = "InMemory",
                    ["PlayerData:SourceIngestionToken"] = SourceToken,
                    ["PlayerData:AdminCommandToken"] = AdminToken,
                    ["PlayerData:ChatGatewayToken"] = ChatToken,
                    ["PlayerData:MonitoringToken"] = MonitoringToken,
                    ["PlayerData:ProjectionEnabled"] = "false"
                }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatPolicyClient>();
            services.AddSingleton<TestChatPolicyClient>();
            services.AddSingleton<IChatPolicyClient>(provider =>
                provider.GetRequiredService<TestChatPolicyClient>());
        });
    }
}

public sealed class PlayerDataApiTests(
    PlayerDataWebApplicationFactory factory)
    : IClassFixture<PlayerDataWebApplicationFactory>
{
    /// <summary>
    /// Admin 的 PlayerData 依赖探测必须使用只读监控凭据，匿名请求不得获知内部就绪状态。
    /// </summary>
    [Fact]
    public async Task InternalMonitoringHealthRequiresDedicatedCredential()
    {
        using var anonymous = factory.CreateClient();
        using var rejected = await anonymous.GetAsync("/internal/monitoring/health");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var monitoring = factory.CreateClient();
        monitoring.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                PlayerDataWebApplicationFactory.MonitoringToken);
        using var ready = await monitoring.GetAsync("/internal/monitoring/health");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task RewardClaimAndAdminReversalAreAtomicAndIdempotent()
    {
        using var source = factory.CreateClient();
        var playerId = $"player-{Guid.NewGuid():N}";
        var rewardGrantId = $"reward-{Guid.NewGuid():N}";
        var rewardEventId = Guid.NewGuid().ToString();
        using var reward = await SendAsync(
            source,
            "/internal/sources/reward-claims",
            PlayerDataWebApplicationFactory.SourceToken,
            rewardEventId,
            new
            {
                eventId = rewardEventId,
                rewardGrantId,
                playerId,
                assetCode = "COIN",
                amount = 500,
                occurredAtUtc = DateTimeOffset.UtcNow,
                sourceReference = $"daily-reward:{rewardGrantId}",
                traceId = $"trace-{Guid.NewGuid():N}"
            });
        Assert.Equal(HttpStatusCode.Created, reward.StatusCode);

        using var monitoring = factory.CreateClient();
        monitoring.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                PlayerDataWebApplicationFactory.MonitoringToken);
        var balances = await monitoring.GetFromJsonAsync<WalletBalance[]>(
            $"/internal/monitoring/players/{playerId}/balances");
        Assert.NotNull(balances);
        Assert.Equal(500, Assert.Single(balances).Balance);

        var commandId = Guid.NewGuid().ToString();
        var caseId = Guid.NewGuid().ToString();
        var reversalBody = new
        {
            operationType = "RevokeReward",
            playerId,
            caseId,
            assetCode = (string?)null,
            amount = (long?)null,
            rewardGrantId,
            requestedBy = "compensation-operator",
            approvedBy = "player-approver",
            reason = "The duplicated daily reward must be reversed.",
            ticketId = "FIN-REWARD-REVERSAL-001",
            traceId = $"trace-{Guid.NewGuid():N}",
            approvedAtUtc = DateTimeOffset.UtcNow
        };
        using var reversed = await SendAsync(
            source,
            "/internal/admin/wallet-operations",
            PlayerDataWebApplicationFactory.AdminToken,
            commandId,
            reversalBody);
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        using var duplicate = await SendAsync(
            source,
            "/internal/admin/wallet-operations",
            PlayerDataWebApplicationFactory.AdminToken,
            commandId,
            reversalBody);
        var duplicateResult =
            await duplicate.Content.ReadFromJsonAsync<WalletOperationResult>();
        Assert.NotNull(duplicateResult);
        Assert.True(duplicateResult.Duplicate);

        balances = await monitoring.GetFromJsonAsync<WalletBalance[]>(
            $"/internal/monitoring/players/{playerId}/balances");
        Assert.Equal(0, Assert.Single(balances!).Balance);
    }

    [Fact]
    public async Task CompensationRequiresSeparateApprovalAndUpdatesBalance()
    {
        using var client = factory.CreateClient();
        var playerId = $"player-{Guid.NewGuid():N}";
        var commandId = Guid.NewGuid().ToString();
        using var response = await SendAsync(
            client,
            "/internal/admin/wallet-operations",
            PlayerDataWebApplicationFactory.AdminToken,
            commandId,
            new
            {
                operationType = "GrantCompensation",
                playerId,
                caseId = Guid.NewGuid().ToString(),
                assetCode = "COIN",
                amount = 1200,
                rewardGrantId = (string?)null,
                requestedBy = "compensation-operator",
                approvedBy = "player-approver",
                reason = "Approved compensation for the verified incident.",
                ticketId = "FIN-COMPENSATION-001",
                traceId = $"trace-{Guid.NewGuid():N}",
                approvedAtUtc = DateTimeOffset.UtcNow
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result =
            await response.Content.ReadFromJsonAsync<WalletOperationResult>();
        Assert.NotNull(result);
        Assert.Equal(1200, result.BalanceAfter);
        Assert.False(result.Duplicate);
    }

    [Fact]
    public async Task EvidenceSourceRejectsPiiAndWrongCredential()
    {
        using var client = factory.CreateClient();
        var eventId = Guid.NewGuid().ToString();
        using var unauthorized = await SendAsync(
            client,
            "/internal/sources/payment-orders",
            "wrong-token-that-is-still-long-enough-0000000",
            eventId,
            PaymentEvidence(eventId, new
            {
                orderReference = "masked-order",
                amountMinor = 100
            }));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            unauthorized.StatusCode);
        using var pii = await SendAsync(
            client,
            "/internal/sources/payment-orders",
            PlayerDataWebApplicationFactory.SourceToken,
            eventId,
            PaymentEvidence(eventId, new
            {
                orderReference = "masked-order",
                amountMinor = 100,
                cardNumber = "not-allowed"
            }));
        Assert.Equal(HttpStatusCode.BadRequest, pii.StatusCode);
    }

    [Fact]
    public async Task ChatAuthorizationFailsClosedForMutedPlayer()
    {
        var policy =
            factory.Services.GetRequiredService<TestChatPolicyClient>();
        policy.Allowed = false;
        try
        {
            using var client = factory.CreateClient();
            using var response = await SendAsync(
                client,
                "/internal/chat/messages/authorize",
                PlayerDataWebApplicationFactory.ChatToken,
                Guid.NewGuid().ToString(),
                new
                {
                    messageId = Guid.NewGuid().ToString(),
                    playerId = "player-muted-test",
                    roomId = "room-chat-test",
                    requestedAtUtc = DateTimeOffset.UtcNow
                });
            Assert.Equal(
                HttpStatusCode.Locked,
                response.StatusCode);
            var body =
                await response.Content.ReadFromJsonAsync<ChatPolicyResult>();
            Assert.NotNull(body);
            Assert.False(body.Allowed);
        }
        finally
        {
            policy.Allowed = true;
        }
    }

    private static object PaymentEvidence(
        string eventId,
        object data) =>
        new
        {
            eventId,
            playerId = "player-payment-test",
            evidenceType = "PaymentOrder",
            occurredAtUtc = DateTimeOffset.UtcNow,
            sourceReference = $"payment:{eventId}",
            data,
            sensitivity = "Financial"
        };

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        string token,
        string idempotencyKey,
        object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}

public sealed class TestChatPolicyClient : IChatPolicyClient
{
    public bool Allowed { get; set; } = true;

    public Task<ChatPolicyResult> GetPolicyAsync(
        string playerId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ChatPolicyResult(
            playerId,
            Allowed,
            Allowed ? null : DateTimeOffset.UtcNow.AddHours(1),
            Allowed ? "Allowed" : "Muted"));
}
