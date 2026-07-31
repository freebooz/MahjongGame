// 验证 Lobby 玩家公开 API 的认证、房间生命周期、幂等键、路由和 WebSocket 协议行为。
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class LobbyApiTests(LobbyWebApplicationFactory factory)
    : IClassFixture<LobbyWebApplicationFactory>
{
    [Fact]
    public async Task Readiness_ChecksStoreAndAllocatorDependency()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Bootstrap_ReturnsAuthenticatedPlayerAndProtocol()
    {
        using var client = factory.CreateAuthenticatedClient("bootstrap-player");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/lobby/bootstrap");
        LobbyWebApplicationFactory.AddRequestHeaders(request);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<LobbyBootstrapResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bootstrap-player", body?.PlayerId);
        Assert.Equal(1, body?.ProtocolVersion);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsSessionExpired()
    {
        using var client = factory.CreateAuthenticatedClient(
            "expired-player", expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/lobby/bootstrap");
        LobbyWebApplicationFactory.AddRequestHeaders(request);

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("SESSION_EXPIRED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentHttpCreation_ReturnsUniqueRoomCodes()
    {
        var tasks = Enumerable.Range(0, 50).Select(async index =>
        {
            using var client = factory.CreateAuthenticatedClient($"http-player-{index}");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/rooms")
            {
                Content = JsonContent.Create(NewCreateRequest(false, null))
            };
            LobbyWebApplicationFactory.AddRequestHeaders(request, $"create-room-key-{index:D4}");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<RoomOperation>();
        });

        var rooms = await Task.WhenAll(tasks);

        Assert.Equal(50, rooms.Select(room => room!.RoomCode).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task PasswordFailure_IsLimitedAndResponseNeverEchoesPassword()
    {
        const string secret = "857421";
        using var owner = factory.CreateAuthenticatedClient("password-owner");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/v1/rooms")
        {
            Content = JsonContent.Create(NewCreateRequest(true, secret))
        };
        LobbyWebApplicationFactory.AddRequestHeaders(create, "password-room-create-key");
        var createResponse = await owner.SendAsync(create);
        var room = await createResponse.Content.ReadFromJsonAsync<RoomOperation>();
        Assert.NotNull(room);

        using var joiner = factory.CreateAuthenticatedClient("password-joiner");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var join = NewJoinRequest(room.RoomCode, "wrong00", $"wrong-password-{attempt:D2}");
            var response = await joiner.SendAsync(join);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("wrong00", body, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }

        using var limitedJoin = NewJoinRequest(room.RoomCode, secret, "password-limit-final");
        var limited = await joiner.SendAsync(limitedJoin);
        var limitedBody = await limited.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("RATE_LIMITED", limitedBody, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, limitedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyOwnerCanCloseRoom_AndOwnerCanCreateAgainAfterClosing()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var owner = factory.CreateAuthenticatedClient($"close-owner-{suffix}");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/v1/rooms")
        {
            Content = JsonContent.Create(NewCreateRequest(false, null))
        };
        LobbyWebApplicationFactory.AddRequestHeaders(create, $"close-create-{suffix}");
        var createResponse = await owner.SendAsync(create);
        var room = await createResponse.Content.ReadFromJsonAsync<RoomOperation>();
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        Assert.NotNull(room);

        using var member = factory.CreateAuthenticatedClient($"close-member-{suffix}");
        using var join = NewJoinRequest(room.RoomCode, string.Empty, $"close-join-{suffix}");
        Assert.Equal(HttpStatusCode.Accepted, (await member.SendAsync(join)).StatusCode);

        using var memberClose = new HttpRequestMessage(HttpMethod.Post, "/v1/rooms/current/close");
        LobbyWebApplicationFactory.AddRequestHeaders(memberClose, $"member-close-{suffix}");
        Assert.Equal(HttpStatusCode.Forbidden, (await member.SendAsync(memberClose)).StatusCode);

        using var rejoin = NewJoinRequest(room.RoomCode, string.Empty, $"close-rejoin-{suffix}");
        Assert.Equal(HttpStatusCode.Accepted, (await member.SendAsync(rejoin)).StatusCode);

        using var ownerClose = new HttpRequestMessage(HttpMethod.Post, "/v1/rooms/current/close");
        LobbyWebApplicationFactory.AddRequestHeaders(ownerClose, $"owner-close-{suffix}");
        var closeResponse = await owner.SendAsync(ownerClose);
        var closed = await closeResponse.Content.ReadFromJsonAsync<RoomOperation>();
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        Assert.Equal(RoomLifecycle.Closed, closed?.Lifecycle);

        using var createAgain = new HttpRequestMessage(HttpMethod.Post, "/v1/rooms")
        {
            Content = JsonContent.Create(NewCreateRequest(false, null))
        };
        LobbyWebApplicationFactory.AddRequestHeaders(createAgain, $"close-create-again-{suffix}");
        var createAgainResponse = await owner.SendAsync(createAgain);
        Assert.Equal(HttpStatusCode.Accepted, createAgainResponse.StatusCode);
        var secondRoom = await createAgainResponse.Content.ReadFromJsonAsync<RoomOperation>();
        Assert.NotEqual(room.RoomId, secondRoom?.RoomId);
    }

    [Fact]
    public async Task OpenApi_IsServedByIndependentApplication()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.yaml");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("openapi: 3.1.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchResultEndpoint_AuthenticatesAndAcknowledgesDuplicate()
    {
        const string resultCredential = "test-result-credential-which-is-long-enough";
        var store = factory.Services.GetRequiredService<ILobbyStore>();
        var matchId = Guid.NewGuid().ToString();
        var room = new LobbyRoom
        {
            RoomId = Guid.NewGuid().ToString(),
            RoomCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
            OwnerPlayerId = "result-api-owner",
            RoundCount = 4,
            PublicRoom = false,
            AutoStart = true,
            MaximumPlayers = 4,
            RuleSnapshot = new Dictionary<string, object?> { ["ruleId"] = "GuiyangMainstreamV1" },
            Lifecycle = RoomLifecycle.Settling,
            PlayerIds = ["result-api-owner"],
            Route = new GameServerRoute(
                Guid.NewGuid().ToString(), string.Empty, string.Empty,
                Guid.NewGuid().ToString(), matchId, "127.0.0.1", 19000, string.Empty, DateTimeOffset.UtcNow),
            ResultCredentialHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(resultCredential))),
            MatchId = matchId,
            StateSequence = 5,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        room = room with { Route = room.Route! with { RoomId = room.RoomId } };
        Assert.Equal(CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);
        var report = TestShuffleFairness.CreateReport(
            room.RoomId, room.Route.ServerInstanceId, 7, 4,
            [new MatchPlayerResult("result-api-owner", 0, 1, 12)]);
        using var client = factory.CreateClient();

        async Task<MatchResultAck?> SubmitAsync(string key)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"/internal/matches/{matchId}/result")
            {
                Content = JsonContent.Create(report)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resultCredential);
            LobbyWebApplicationFactory.AddRequestHeaders(request, key);
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<MatchResultAck>();
        }

        var first = await SubmitAsync("match-result-api-first-0001");
        var duplicate = await SubmitAsync("match-result-api-second-0001");
        Assert.False(first?.Duplicate);
        Assert.True(duplicate?.Duplicate);
    }

    [Fact]
    public async Task MatchResultRecovery_RequiresInternalTokenAndClosesFailedRoom()
    {
        const string internalToken = "test-only-internal-service-token-which-is-long-enough";
        var store = factory.Services.GetRequiredService<ILobbyStore>();
        var matchId = Guid.NewGuid().ToString();
        var instanceId = Guid.NewGuid().ToString();
        var room = new LobbyRoom
        {
            RoomId = Guid.NewGuid().ToString(),
            RoomCode = Random.Shared.Next(0, 1_000_000).ToString("D6"),
            OwnerPlayerId = "recovery-owner",
            RoundCount = 4,
            PublicRoom = false,
            AutoStart = true,
            MaximumPlayers = 4,
            RuleSnapshot = new Dictionary<string, object?> { ["ruleId"] = "GuiyangMainstreamV1" },
            Lifecycle = RoomLifecycle.Failed,
            PlayerIds = ["recovery-owner"],
            LastServerInstanceId = instanceId,
            MatchId = matchId,
            StateSequence = 7,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        Assert.Equal(CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);
        var report = TestShuffleFairness.CreateReport(
            room.RoomId, instanceId, 11, 4,
            [new MatchPlayerResult("recovery-owner", 0, 1, 20)]);
        using var client = factory.CreateClient();

        using var unauthorized = new HttpRequestMessage(
            HttpMethod.Post, $"/internal/matches/{matchId}/result/recovery")
        {
            Content = JsonContent.Create(report)
        };
        unauthorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        LobbyWebApplicationFactory.AddRequestHeaders(unauthorized, "match-recovery-unauthorized-0001");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unauthorized)).StatusCode);

        using var authorized = new HttpRequestMessage(
            HttpMethod.Post, $"/internal/matches/{matchId}/result/recovery")
        {
            Content = JsonContent.Create(report)
        };
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", internalToken);
        LobbyWebApplicationFactory.AddRequestHeaders(authorized, "match-recovery-authorized-0001");
        var response = await client.SendAsync(authorized);
        var acknowledgement = await response.Content.ReadFromJsonAsync<MatchResultAck>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(acknowledgement?.Accepted);
        Assert.Equal(RoomLifecycle.Closed,
            (await store.GetRoomByIdAsync(room.RoomId, CancellationToken.None))?.Lifecycle);
    }

    private static CreateRoomRequest NewCreateRequest(bool protectedRoom, string? password) => new(
        4,
        true,
        true,
        protectedRoom,
        password,
        new Dictionary<string, object?> { ["ruleId"] = "GuiyangMainstreamV1" });

    private static HttpRequestMessage NewJoinRequest(string roomCode, string password, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/rooms/{roomCode}/join")
        {
            Content = JsonContent.Create(new JoinRoomRequest(password, 1))
        };
        LobbyWebApplicationFactory.AddRequestHeaders(request, idempotencyKey);
        return request;
    }
}
