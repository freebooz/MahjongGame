using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task RoomControlIsIdempotentAndProhibitsNewPlayers()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            LobbyWebApplicationFactory.ManagementToken);
        var store = factory.Services.GetRequiredService<ILobbyStore>();
        var now = DateTimeOffset.UtcNow;
        var room = new LobbyRoom
        {
            RoomId = Guid.NewGuid().ToString("N"),
            RoomCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
            OwnerPlayerId = $"owner-{Guid.NewGuid():N}",
            RoundCount = 4,
            PublicRoom = true,
            AutoStart = false,
            MaximumPlayers = 4,
            RuleSnapshot = [],
            Lifecycle = RoomLifecycle.Waiting,
            PlayerIds = [],
            MatchId = Guid.NewGuid().ToString("N"),
            StateSequence = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        room = room with { PlayerIds = [room.OwnerPlayerId] };
        Assert.Equal(
            CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);

        var request = new AdminUpdateRoomControlRequest(
            "ProhibitNewPlayers",
            room.StateSequence,
            "Security investigation admission control",
            Guid.NewGuid().ToString());
        var commandId = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", commandId);
        var path = $"/internal/admin/rooms/{room.RoomId}/controls";
        using var first = await client.PostAsJsonAsync(path, request);
        var result =
            await first.Content.ReadFromJsonAsync<AdminUpdateRoomControlResult>();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(result);
        Assert.True(result.NewPlayersProhibited);
        Assert.Equal(2, result.StateSequence);

        using var duplicate = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(
            AddPlayerStatus.AdmissionProhibited,
            (await store.TryAddPlayerAsync(
                room.RoomCode,
                $"joining-{Guid.NewGuid():N}",
                CancellationToken.None)).Status);

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var stale = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task ForceDissolveTransitionsRoomAndTreatsRetryAsTerminalSuccess()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Request-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            LobbyWebApplicationFactory.ManagementToken);
        var store = factory.Services.GetRequiredService<ILobbyStore>();
        var now = DateTimeOffset.UtcNow;
        var room = new LobbyRoom
        {
            RoomId = Guid.NewGuid().ToString("N"),
            RoomCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
            OwnerPlayerId = $"dissolve-owner-{Guid.NewGuid():N}",
            RoundCount = 4,
            PublicRoom = false,
            AutoStart = false,
            MaximumPlayers = 4,
            RuleSnapshot = [],
            Lifecycle = RoomLifecycle.Waiting,
            PlayerIds = [],
            MatchId = Guid.NewGuid().ToString("N"),
            StateSequence = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        room = room with { PlayerIds = [room.OwnerPlayerId] };
        Assert.Equal(
            CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);
        var request = new AdminUpdateRoomControlRequest(
            "ForceDissolveRoom",
            room.StateSequence,
            "Abnormal room forced dissolution",
            Guid.NewGuid().ToString());
        var path = $"/internal/admin/rooms/{room.RoomId}/controls";
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var first = await client.PostAsJsonAsync(path, request);
        var firstResult =
            await first.Content.ReadFromJsonAsync<AdminUpdateRoomControlResult>();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(firstResult);
        Assert.Equal(RoomLifecycle.Failed, firstResult.Lifecycle);
        Assert.True(firstResult.NewPlayersProhibited);
        Assert.True(firstResult.MarkedAbnormal);
        Assert.False(firstResult.AlreadyTerminal);

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var retry = await client.PostAsJsonAsync(path, request);
        var retryResult =
            await retry.Content.ReadFromJsonAsync<AdminUpdateRoomControlResult>();
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.NotNull(retryResult);
        Assert.True(retryResult.AlreadyTerminal);
        Assert.Null(await store.GetActiveRoomByPlayerAsync(
            room.OwnerPlayerId,
            CancellationToken.None));
    }
}
