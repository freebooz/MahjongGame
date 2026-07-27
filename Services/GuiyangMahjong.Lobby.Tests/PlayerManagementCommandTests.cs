using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Security;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class PlayerManagementCommandTests(LobbyWebApplicationFactory factory)
    : IClassFixture<LobbyWebApplicationFactory>
{
    [Fact]
    public async Task DisconnectRejectsOldTokenRemovesPresenceAndAllowsNewLogin()
    {
        var playerId = $"managed-player-{Guid.NewGuid():N}";
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var oldToken = HmacPlayerTokenValidator.CreateSignedToken(
            LobbyWebApplicationFactory.SigningKey,
            new PlayerIdentity(playerId, "Managed Player", "Guest"),
            DateTimeOffset.UtcNow.AddMinutes(10),
            issuedAt);
        using var playerClient = factory.CreateClient();
        playerClient.DefaultRequestHeaders.Add("X-Request-Id", Guid.NewGuid().ToString());
        playerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", oldToken);
        Assert.Equal(
            HttpStatusCode.OK,
            (await playerClient.GetAsync("/v1/lobby/bootstrap")).StatusCode);

        var effectiveAt = DateTimeOffset.UtcNow;
        var path =
            $"/internal/admin/players/{Uri.EscapeDataString(playerId)}/disconnect";
        var command = new AdminDisconnectPlayerRequest(
            "Security investigation forced logout",
            Guid.NewGuid().ToString(),
            effectiveAt);
        using var managementClient = factory.CreateClient();
        managementClient.DefaultRequestHeaders.Add("X-Request-Id", Guid.NewGuid().ToString());
        using var unauthorized = await managementClient.PostAsJsonAsync(path, command);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        managementClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                LobbyWebApplicationFactory.ManagementToken);
        managementClient.DefaultRequestHeaders.Add(
            "Idempotency-Key",
            Guid.NewGuid().ToString());
        using var disconnected = await managementClient.PostAsJsonAsync(path, command);
        Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await playerClient.GetAsync("/v1/lobby/bootstrap")).StatusCode);

        using var monitoringClient = factory.CreateClient();
        monitoringClient.DefaultRequestHeaders.Add("X-Request-Id", Guid.NewGuid().ToString());
        monitoringClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                LobbyWebApplicationFactory.MonitoringToken);
        var presence = await monitoringClient.GetFromJsonAsync<PlayerPresenceSnapshot[]>(
            $"/internal/monitoring/player-presence?playerIds={Uri.EscapeDataString(playerId)}");
        Assert.NotNull(presence);
        Assert.False(Assert.Single(presence).Online);

        using var newLoginClient = factory.CreateAuthenticatedClient(
            playerId,
            "Managed Player",
            DateTimeOffset.UtcNow.AddMinutes(10),
            effectiveAt.AddMilliseconds(1));
        newLoginClient.DefaultRequestHeaders.Add("X-Request-Id", Guid.NewGuid().ToString());
        Assert.Equal(
            HttpStatusCode.OK,
            (await newLoginClient.GetAsync("/v1/lobby/bootstrap")).StatusCode);
    }
}
