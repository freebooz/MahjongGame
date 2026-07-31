using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.GameData.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace GuiyangMahjong.GameData.Tests;

/// <summary>GameData阶段8.2兼容写入口的身份、输入和幂等集成测试。</summary>
public sealed class LegacyReplayApiFactory : WebApplicationFactory<Program>
{
    public const string Token = "gamedata-legacy-replay-test-token-000000001";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["GameData:PersistenceMode"] = "InMemory",
                ["GameData:LobbyAuthorityToken"] = "gamedata-lobby-test-token-00000000000001",
                ["GameData:MonitoringToken"] = "gamedata-monitor-test-token-0000000000001",
                ["GameData:SettlementSigningKey"] = "gamedata-settlement-test-key-000000000001",
                ["GameData:AllocatorRecoveryToken"] = "gamedata-recovery-test-token-000000000001",
                ["GameData:LegacyReplayIngestionToken"] = Token
            }));
    }
}

public sealed class LegacyReplayApiTests(LegacyReplayApiFactory factory) : IClassFixture<LegacyReplayApiFactory>
{
    [Fact]
    public async Task Endpoint_RequiresDedicatedCredential_RejectsPii_AndReturnsFirstResponse()
    {
        var eventId = Guid.NewGuid().ToString();
        var body = Request(eventId, new { replayId = "legacy-replay-api" });
        using var client = factory.CreateClient();
        using var unauthorized = await SendAsync(client, "wrong-purpose-token-that-is-long-enough-001", eventId, body);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var piiEventId = Guid.NewGuid().ToString();
        using var pii = await SendAsync(client, LegacyReplayApiFactory.Token, piiEventId,
            Request(piiEventId, new { replayId = "x", accessToken = "forbidden" }));
        Assert.Equal(HttpStatusCode.BadRequest, pii.StatusCode);

        using var first = await SendAsync(client, LegacyReplayApiFactory.Token, eventId, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await SendAsync(client, LegacyReplayApiFactory.Token, eventId, body);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.True((await duplicate.Content.ReadFromJsonAsync<LegacyReplayEvidenceResult>())!.Duplicate);
    }

    private static object Request(string eventId, object data) => new
    {
        eventId,
        playerId = "player-replay-api",
        evidenceType = "Replay",
        occurredAtUtc = DateTimeOffset.UtcNow,
        sourceReference = $"replay:{eventId}",
        data,
        sensitivity = "Restricted"
    };

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string idempotencyKey, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/replay-evidence/legacy-player-index")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}
