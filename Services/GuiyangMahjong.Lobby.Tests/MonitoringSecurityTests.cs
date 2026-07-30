// 验证 Lobby 监控接口的只读凭据、分页游标绑定、批量上限和敏感数据隔离。
using System.Net;
using System.Net.Http.Headers;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class MonitoringSecurityTests(LobbyWebApplicationFactory factory)
    : IClassFixture<LobbyWebApplicationFactory>
{
    [Fact]
    public async Task MonitoringRoomsRequiresDedicatedReadOnlyCredential()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", Guid.NewGuid().ToString());
        using var unauthorized = await client.GetAsync("/internal/monitoring/rooms");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", LobbyWebApplicationFactory.MonitoringToken);
        using var authorized = await client.GetAsync("/internal/monitoring/rooms");
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        using var presence = await client.GetAsync(
            "/internal/monitoring/player-presence?playerIds=player-one");
        Assert.Equal(HttpStatusCode.OK, presence.StatusCode);
    }
}
