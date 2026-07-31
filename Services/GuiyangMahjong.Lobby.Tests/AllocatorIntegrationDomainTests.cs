using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace GuiyangMahjong.Lobby.Tests;

/// <summary>
/// 验证 Lobby 与 Allocator 的领域集成边界，包括路由票据、权威规则快照、
/// Dedicated Server 生命周期以及结算哈希；测试使用内存适配器，不访问生产基础设施。
/// </summary>
public sealed class AllocatorIntegrationDomainTests
{
    [Fact]
    public async Task Route_IsUnavailableUntilRegistrationThenGetsShortLivedTicket()
    {
        var fixture = CreateFixture();
        var owner = new PlayerIdentity("owner-route", "Owner", "Guest");
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(), owner, NewCreateRequest(), CancellationToken.None);

        await Assert.ThrowsAsync<LobbyOperationException>(() => fixture.Service.GetRouteAsync(
            Guid.NewGuid().ToString(), owner, created.RoomCode, CancellationToken.None));

        var room = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        var registration = new GameServerRegistration(
            fixture.Allocator.ServerInstanceId,
            room.RoomId,
            room.MatchId,
            "127.0.0.1",
            19000,
            "test",
            "one-time-registration-credential");
        var acknowledgement = await fixture.Service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(), registration, CancellationToken.None);

        Assert.Equal(created.RoomCode, acknowledgement.RoomBootstrap.RoomCode);
        Assert.Equal(room.RoomId, acknowledgement.RoomBootstrap.RoomId);
        Assert.Equal(room.MatchId, acknowledgement.RoomBootstrap.MatchId);
        Assert.Equal(owner.PlayerId, acknowledgement.RoomBootstrap.OwnerPlayerId);
        Assert.Equal(4, acknowledgement.RoomBootstrap.RoundCount);
        Assert.Equal("GuiyangMainstreamV1", acknowledgement.RoomBootstrap.RuleSnapshot["ruleId"]!.ToString());

        var route = await fixture.Service.GetRouteAsync(
            Guid.NewGuid().ToString(), owner, created.RoomCode, CancellationToken.None);
        Assert.Equal(fixture.Allocator.ServerInstanceId, route.ServerInstanceId);
        Assert.NotEmpty(route.JoinTicket);
        Assert.True(route.TicketExpireAtUtc > fixture.Time.GetUtcNow());
        var encodedPayload = route.JoinTicket.Split('.', 2)[0]
            .Replace('-', '+')
            .Replace('_', '/');
        encodedPayload = encodedPayload.PadRight(
            encodedPayload.Length + ((4 - encodedPayload.Length % 4) % 4),
            '=');
        using var payload = JsonDocument.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(encodedPayload)));
        Assert.Equal(owner.PlayerId, payload.RootElement.GetProperty("playerId").GetString());
        Assert.Equal(owner.DisplayName, payload.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task RegistrationBootstrap_UsesImmutableAuthoritativeRuleSnapshot()
    {
        var fixture = CreateFixture();
        var requestedRules = new Dictionary<string, object?>
        {
            ["ruleId"] = "GuiyangMainstreamV1",
            ["baseScore"] = 3,
            ["turnTimeoutSeconds"] = 21
        };
        var request = new CreateRoomRequest(8, false, false, false, null, requestedRules);
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(),
            new PlayerIdentity("owner-bootstrap", "Owner", "Guest"),
            request,
            CancellationToken.None);
        requestedRules["baseScore"] = 99;

        var room = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        var acknowledgement = await fixture.Service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(),
            new GameServerRegistration(
                fixture.Allocator.ServerInstanceId,
                room.RoomId,
                room.MatchId,
                "127.0.0.1",
                19000,
                "test",
                "credential"),
            CancellationToken.None);

        Assert.Equal(created.RoomCode, acknowledgement.RoomBootstrap.RoomCode);
        Assert.Equal(8, acknowledgement.RoomBootstrap.RoundCount);
        Assert.False(acknowledgement.RoomBootstrap.PublicRoom);
        Assert.Equal("3", acknowledgement.RoomBootstrap.RuleSnapshot["baseScore"]!.ToString());
        Assert.Equal("21", acknowledgement.RoomBootstrap.RuleSnapshot["turnTimeoutSeconds"]!.ToString());
    }

    [Fact]
    public async Task AllocatorFailure_RemovesRouteAndFailsRoom()
    {
        var fixture = CreateFixture();
        var owner = new PlayerIdentity("owner-failure", "Owner", "Guest");
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(), owner, NewCreateRequest(), CancellationToken.None);
        var room = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        await fixture.Service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(),
            new GameServerRegistration(
                fixture.Allocator.ServerInstanceId,
                room.RoomId,
                room.MatchId,
                "127.0.0.1",
                19000,
                "test",
                "credential"),
            CancellationToken.None);

        await fixture.Service.MarkGameServerFailedAsync(new GameServerFailure(
            fixture.Allocator.ServerInstanceId,
            room.RoomId,
            "test crash"), CancellationToken.None);

        var failed = await fixture.Store.GetRoomByIdAsync(room.RoomId, CancellationToken.None);
        Assert.Equal(RoomLifecycle.Failed, failed?.Lifecycle);
        Assert.Null(failed?.Route);
    }

    [Fact]
    public async Task FailureBeforeRegistration_FailsAllocatingRoom()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(),
            new PlayerIdentity("owner-startup-failure", "Owner", "Guest"),
            NewCreateRequest(),
            CancellationToken.None);

        await fixture.Service.MarkGameServerFailedAsync(new GameServerFailure(
            fixture.Allocator.ServerInstanceId,
            created.RoomId,
            "registration timeout"), CancellationToken.None);

        var failed = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.Equal(RoomLifecycle.Failed, failed?.Lifecycle);
        Assert.Null(failed?.PendingServerInstanceId);
    }

    [Fact]
    public async Task EmptyWaitingRoom_ClosesAndDrainsDedicatedServerAfterTimeout()
    {
        var fixture = CreateFixture();
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(),
            new PlayerIdentity("owner-empty-room", "Owner", "Guest"),
            NewCreateRequest(),
            CancellationToken.None);
        var room = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        await fixture.Service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(),
            new GameServerRegistration(
                fixture.Allocator.ServerInstanceId, room.RoomId, room.MatchId,
                "127.0.0.1", 19000, "test", "credential"),
            CancellationToken.None);
        var emptyHeartbeat = NewHeartbeat(room.RoomId, "Waiting") with
        {
            ConnectedPlayers = 0,
            ConnectedPlayerIds = []
        };

        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(),
            fixture.Allocator.ServerInstanceId,
            emptyHeartbeat,
            CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromSeconds(91));
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(),
            fixture.Allocator.ServerInstanceId,
            emptyHeartbeat,
            CancellationToken.None);

        var closed = await fixture.Store.GetRoomByIdAsync(room.RoomId, CancellationToken.None);
        Assert.Equal(RoomLifecycle.Closed, closed?.Lifecycle);
        Assert.Null(closed?.Route);
        Assert.Equal(fixture.Allocator.ServerInstanceId, closed?.LastServerInstanceId);
        Assert.Equal(1, fixture.Allocator.DrainCount);
        Assert.Null(await fixture.Store.GetActiveRoomByPlayerAsync(
            "owner-empty-room", CancellationToken.None));
    }

    [Fact]
    public async Task EmptyPlayingRoom_FailsAndDrainsDedicatedServerAfterReconnectWindow()
    {
        var fixture = CreateFixture();
        var owner = new PlayerIdentity("owner-empty-match", "Owner", "Guest");
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(), owner, NewCreateRequest(), CancellationToken.None);
        var room = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        await fixture.Service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(),
            new GameServerRegistration(
                fixture.Allocator.ServerInstanceId, room.RoomId, room.MatchId,
                "127.0.0.1", 19000, "test", "credential"),
            CancellationToken.None);
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(),
            fixture.Allocator.ServerInstanceId,
            NewHeartbeat(room.RoomId, "Playing") with
            {
                ConnectedPlayerIds = [owner.PlayerId]
            },
            CancellationToken.None);
        var emptyHeartbeat = NewHeartbeat(room.RoomId, "Playing") with
        {
            ConnectedPlayers = 0,
            ConnectedPlayerIds = []
        };
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(),
            fixture.Allocator.ServerInstanceId,
            emptyHeartbeat,
            CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromSeconds(91));
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(),
            fixture.Allocator.ServerInstanceId,
            emptyHeartbeat,
            CancellationToken.None);

        var failed = await fixture.Store.GetRoomByIdAsync(room.RoomId, CancellationToken.None);
        Assert.Equal(RoomLifecycle.Failed, failed?.Lifecycle);
        Assert.Equal(1, fixture.Allocator.DrainCount);
        Assert.Null(await fixture.Store.GetActiveRoomByPlayerAsync(
            owner.PlayerId, CancellationToken.None));
    }

    [Fact]
    public async Task ReconnectRoute_UsesAuthenticatedPlayerMappingInsteadOfClientHints()
    {
        var fixture = CreateFixture();
        var owner = new PlayerIdentity("owner-reconnect", "Owner", "Guest");
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(), owner, NewCreateRequest(), CancellationToken.None);
        var room = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        await fixture.Service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(),
            new GameServerRegistration(
                fixture.Allocator.ServerInstanceId, room.RoomId, room.MatchId,
                "127.0.0.1", 19000, "test", "credential"),
            CancellationToken.None);

        var route = await fixture.Service.GetReconnectRouteAsync(
            Guid.NewGuid().ToString(), owner,
            new ReconnectRouteRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString()),
            CancellationToken.None);

        Assert.Equal(room.RoomId, route.RoomId);
        Assert.Equal(room.MatchId, route.MatchId);
        await Assert.ThrowsAsync<LobbyOperationException>(() => fixture.Service.GetReconnectRouteAsync(
            Guid.NewGuid().ToString(),
            new PlayerIdentity("not-a-member", "Other", "Guest"),
            new ReconnectRouteRequest(),
            CancellationToken.None));
    }

    [Fact]
    public async Task MatchResult_IsPersistedOnceAndDuplicateIsAcknowledged()
    {
        var fixture = CreateFixture();
        var owner = new PlayerIdentity("owner-result", "Owner", "Guest");
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(), owner, NewCreateRequest(), CancellationToken.None);
        var room = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        var registration = await fixture.Service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(),
            new GameServerRegistration(
                fixture.Allocator.ServerInstanceId, room.RoomId, room.MatchId,
                "127.0.0.1", 19000, "test", "credential"),
            CancellationToken.None);
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId,
            NewHeartbeat(room.RoomId, "Playing"), CancellationToken.None);
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId,
            NewHeartbeat(room.RoomId, "Settling"), CancellationToken.None);
        var report = TestShuffleFairness.CreateReport(
            room.RoomId,
            fixture.Allocator.ServerInstanceId,
            42,
            4,
            [new MatchPlayerResult(owner.PlayerId, 0, 1, 16)]);

        var first = await fixture.Service.SubmitMatchResultAsync(
            Guid.NewGuid().ToString(), room.MatchId, registration.ResultCredential,
            report, CancellationToken.None);
        var duplicate = await fixture.Service.SubmitMatchResultAsync(
            Guid.NewGuid().ToString(), room.MatchId, registration.ResultCredential,
            report, CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.False(first.Duplicate);
        Assert.True(duplicate.Accepted);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(RoomLifecycle.Closed,
            (await fixture.Store.GetRoomByIdAsync(room.RoomId, CancellationToken.None))?.Lifecycle);
        var runtime = await fixture.Monitoring.GetRuntimeAsync(room.RoomId, CancellationToken.None);
        Assert.NotNull(runtime);
        Assert.Equal("Completed", runtime.Settlement?.Status);
        Assert.Equal(report.ResultSequence, runtime.Settlement?.ResultSequence);
        var canonicalResult = JsonSerializer.SerializeToUtf8Bytes(
            report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var expectedHash = Convert.ToHexString(SHA256.HashData(canonicalResult)).ToLowerInvariant();
        Assert.Equal(expectedHash, runtime.Settlement?.ResultHash);
        Assert.Equal(fixture.Time.GetUtcNow(), runtime.Settlement?.ConfirmedAtUtc);
        Assert.Equal(2, fixture.Allocator.DrainCount);
        var conflicting = report with
        {
            Players = [new MatchPlayerResult(owner.PlayerId, 0, 1, 99)]
        };
        await Assert.ThrowsAsync<LobbyOperationException>(() => fixture.Service.SubmitMatchResultAsync(
            Guid.NewGuid().ToString(), room.MatchId, registration.ResultCredential,
            conflicting, CancellationToken.None));
    }

    [Fact]
    public async Task MatchResult_RetryAfterDrainFailureDoesNotPersistTwice()
    {
        var fixture = CreateFixture();
        var owner = new PlayerIdentity("owner-result-retry", "Owner", "Guest");
        var created = await fixture.Service.CreateRoomAsync(
            Guid.NewGuid().ToString(), owner, NewCreateRequest(), CancellationToken.None);
        var room = await fixture.Store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        var registration = await fixture.Service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(),
            new GameServerRegistration(
                fixture.Allocator.ServerInstanceId, room.RoomId, room.MatchId,
                "127.0.0.1", 19000, "test", "credential"),
            CancellationToken.None);
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId,
            NewHeartbeat(room.RoomId, "Playing"), CancellationToken.None);
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId,
            NewHeartbeat(room.RoomId, "Settling"), CancellationToken.None);
        var report = TestShuffleFairness.CreateReport(
            room.RoomId, fixture.Allocator.ServerInstanceId, 88, 4,
            [new MatchPlayerResult(owner.PlayerId, 0, 1, 20)]);
        fixture.Allocator.DrainFailuresRemaining = 1;

        var firstFailure = await Assert.ThrowsAsync<LobbyOperationException>(() =>
            fixture.Service.SubmitMatchResultAsync(
                Guid.NewGuid().ToString(), room.MatchId, registration.ResultCredential,
                report, CancellationToken.None));
        Assert.Equal(LobbyErrorCode.ServerUnavailable, firstFailure.ErrorCode);
        var recovered = await fixture.Service.SubmitMatchResultAsync(
            Guid.NewGuid().ToString(), room.MatchId, registration.ResultCredential,
            report, CancellationToken.None);

        Assert.True(recovered.Accepted);
        Assert.True(recovered.Duplicate);
        Assert.Equal(2, fixture.Allocator.DrainCount);
    }

    private static Fixture CreateFixture()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LobbyOptions
        {
            TokenSigningKey = LobbyWebApplicationFactory.SigningKey,
            JoinTicketSigningKey = "test-only-join-ticket-signing-key-which-is-long-enough",
            InternalServiceToken = "test-only-internal-service-token-which-is-long-enough",
            RoomCodeRetryLimit = 100,
            MaximumPlayersPerRoom = 4,
            Allocator = new AllocatorClientOptions { Enabled = true, GameServerBuildVersion = "test" }
        });
        var store = new InMemoryLobbyStore();
        var allocator = new RecordingAllocatorClient();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-18T00:00:00Z"));
        var monitoring = new InMemoryRoomMonitoringStore();
        var service = new LobbyService(
            store,
            new RoomPasswordService(options, time),
            new NoOpEventPublisher(),
            allocator,
            new HmacJoinTicketIssuer(options, time),
            monitoring,
            options,
            time,
            NullLogger<LobbyService>.Instance);
        return new Fixture(service, store, monitoring, allocator, time);
    }

    private static CreateRoomRequest NewCreateRequest() => new(
        4,
        true,
        true,
        false,
        null,
        new Dictionary<string, object?> { ["ruleId"] = "GuiyangMainstreamV1" });

    private static GameServerHeartbeat NewHeartbeat(string roomId, string lifecycle) => new(
        roomId,
        "heartbeat-credential",
        1,
        lifecycle,
        1,
        "test",
        DateTimeOffset.UtcNow);

    /// <summary>
    /// 保存真实 Lobby 领域服务、权威房间存储和只读监控存储，便于同时验证结算事务与投影。
    /// </summary>
    private sealed record Fixture(
        LobbyService Service,
        InMemoryLobbyStore Store,
        InMemoryRoomMonitoringStore Monitoring,
        RecordingAllocatorClient Allocator,
        FixedTimeProvider Time);

    private sealed class RecordingAllocatorClient : IAllocatorClient
    {
        public string ServerInstanceId { get; } = Guid.NewGuid().ToString();
        public int DrainCount { get; private set; }
        public int DrainFailuresRemaining { get; set; }
        public bool Enabled => true;
        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<AllocatorAllocation> AllocateAsync(
            string requestId, string roomId, string matchId, CancellationToken cancellationToken) =>
            Task.FromResult(new AllocatorAllocation(
                requestId, roomId, ServerInstanceId, 19000, "Starting"));

        public Task<AllocatorRegistrationAck> ConfirmRegistrationAsync(
            string requestId, GameServerRegistration request, CancellationToken cancellationToken) =>
            Task.FromResult(new AllocatorRegistrationAck(
                requestId, request.ServerInstanceId, true, 3, "heartbeat-credential"));

        public Task RecordHeartbeatAsync(
            string requestId,
            string serverInstanceId,
            GameServerHeartbeat request,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DrainAsync(
            string requestId, string serverInstanceId, CancellationToken cancellationToken)
        {
            Assert.Equal(ServerInstanceId, serverInstanceId);
            DrainCount++;
            if (DrainFailuresRemaining-- > 0) throw new HttpRequestException("temporary drain failure");
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpEventPublisher : ILobbyEventPublisher
    {
        public Task PublishAsync(string type, object data, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
