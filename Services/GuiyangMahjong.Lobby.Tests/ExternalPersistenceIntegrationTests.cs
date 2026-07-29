using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class ExternalPersistenceFactAttribute : FactAttribute
{
    public ExternalPersistenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOBBY_TEST_POSTGRES"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOBBY_TEST_REDIS")))
        {
            Skip = "Set LOBBY_TEST_POSTGRES and LOBBY_TEST_REDIS to run external persistence tests.";
        }
    }
}

public sealed class ExternalPersistenceIntegrationTests
{
    [ExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_AllowsOnlyOneActiveRoomAcrossStoreInstances()
    {
        var options = CreateOptions();
        await using var connectionsA = new LobbyPersistenceConnections(options);
        await using var connectionsB = new LobbyPersistenceConnections(options);
        var storeA = CreateStore(options, connectionsA);
        var storeB = CreateStore(options, connectionsB);
        await storeA.InitializeAsync(CancellationToken.None);

        var playerId = $"external-player-{Guid.NewGuid():N}";
        var firstRoom = NewRoom(playerId, RandomRoomCode());
        var secondRoom = NewRoom(playerId, RandomRoomCode());
        while (secondRoom.RoomCode == firstRoom.RoomCode)
            secondRoom = NewRoom(playerId, RandomRoomCode());
        var results = await Task.WhenAll(
            storeA.TryCreateRoomAsync(firstRoom, CancellationToken.None),
            storeB.TryCreateRoomAsync(secondRoom, CancellationToken.None));
        var createdRoom = results[0].Status == CreateRoomStatus.Created
            ? firstRoom
            : results[1].Status == CreateRoomStatus.Created
                ? secondRoom
                : null;
        try
        {
            Assert.Single(results, result => result.Status == CreateRoomStatus.Created);
            Assert.Single(results, result => result.Status == CreateRoomStatus.PlayerAlreadyActive);

            await using var command = connectionsA.Postgres.CreateCommand(
                "SELECT COUNT(*) FROM active_player_rooms WHERE player_id=$1");
            command.Parameters.AddWithValue(playerId);
            Assert.Equal(1L, await command.ExecuteScalarAsync(CancellationToken.None));

            await using var roomCount = connectionsA.Postgres.CreateCommand(
                "SELECT COUNT(*) FROM lobby_rooms WHERE payload->'playerIds' ? $1");
            roomCount.Parameters.AddWithValue(playerId);
            Assert.Equal(1L, await roomCount.ExecuteScalarAsync(CancellationToken.None));
        }
        finally
        {
            if (createdRoom is not null)
            {
                var failedRoom = RoomStateMachine.Transition(
                    createdRoom, RoomLifecycle.Failed, TimeProvider.System);
                _ = await storeA.UpdateRoomAsync(failedRoom, CancellationToken.None);
            }
        }
    }

    [ExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_RoomAdmissionControlIsVisibleAcrossInstances()
    {
        var options = CreateOptions();
        await using var connectionsA = new LobbyPersistenceConnections(options);
        await using var connectionsB = new LobbyPersistenceConnections(options);
        var storeA = CreateStore(options, connectionsA);
        var storeB = CreateStore(options, connectionsB);
        await storeA.InitializeAsync(CancellationToken.None);
        var room = NewRoom(
            $"external-owner-{Guid.NewGuid():N}",
            RandomRoomCode());
        Assert.Equal(
            CreateRoomStatus.Created,
            (await storeA.TryCreateRoomAsync(room, CancellationToken.None)).Status);
        try
        {
            var controlled = room with
            {
                NewPlayersProhibited = true,
                StateSequence = room.StateSequence + 1,
                UpdatedAtUtc = room.UpdatedAtUtc.AddSeconds(1)
            };
            Assert.True(await storeA.UpdateRoomAsync(
                controlled,
                CancellationToken.None));
            var reloaded = await storeB.GetRoomByIdAsync(
                room.RoomId,
                CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.True(reloaded.NewPlayersProhibited);
            Assert.Equal(
                AddPlayerStatus.AdmissionProhibited,
                (await storeB.TryAddPlayerAsync(
                    room.RoomCode,
                    $"external-joiner-{Guid.NewGuid():N}",
                    CancellationToken.None)).Status);
        }
        finally
        {
            var current = await storeA.GetRoomByIdAsync(
                room.RoomId,
                CancellationToken.None);
            if (current is not null)
            {
                _ = await storeA.UpdateRoomAsync(
                    current with
                    {
                        Lifecycle = RoomLifecycle.Failed,
                        StateSequence = current.StateSequence + 1,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    CancellationToken.None);
            }
        }
    }

    [ExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Redis_IdempotencyCoalescesExecutionAcrossStoreInstances()
    {
        var options = CreateOptions();
        await using var connectionsA = new LobbyPersistenceConnections(options);
        await using var connectionsB = new LobbyPersistenceConnections(options);
        var storeA = new RedisIdempotencyStore(connectionsA, options);
        var storeB = new RedisIdempotencyStore(connectionsB, options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<IdempotentHttpResponse> Execute()
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return new IdempotentHttpResponse(
                202,
                JsonSerializer.SerializeToElement(new { roomCode = "810003" }));
        }

        var key = $"external-idempotency-{Guid.NewGuid():N}";
        var first = storeA.ExecuteAsync(key, Execute, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = storeB.ExecuteAsync(key, Execute, CancellationToken.None);
        await Task.Delay(100);
        release.TrySetResult();
        var responses = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(responses[0].Body.GetRawText(), responses[1].Body.GetRawText());
    }

    [ExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Redis_PresenceAggregatesAndExpiresAcrossStoreInstances()
    {
        var options = CreateOptions();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-20T00:00:00Z"));
        await using var connectionsA = new LobbyPersistenceConnections(options);
        await using var connectionsB = new LobbyPersistenceConnections(options);
        var presenceA = new RedisOnlinePresenceService(connectionsA, options, time);
        var presenceB = new RedisOnlinePresenceService(connectionsB, options, time);

        await presenceA.TouchAsync("external-presence-a", CancellationToken.None);
        await presenceB.TouchAsync("external-presence-b", CancellationToken.None);
        Assert.Equal(2, await presenceA.GetOnlineCountAsync(CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(16));
        await presenceB.TouchAsync("external-presence-b", CancellationToken.None);
        Assert.Equal(1, await presenceA.GetOnlineCountAsync(CancellationToken.None));
    }

    [ExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Redis_AccessRevocationIsSharedAndMonotonicAcrossInstances()
    {
        var options = CreateOptions();
        await using var connectionsA = new LobbyPersistenceConnections(options);
        await using var connectionsB = new LobbyPersistenceConnections(options);
        var revocationsA = new RedisPlayerAccessRevocationStore(connectionsA, options);
        var revocationsB = new RedisPlayerAccessRevocationStore(connectionsB, options);
        var playerId = $"external-revocation-{Guid.NewGuid():N}";
        var firstCutoff = DateTimeOffset.UtcNow;
        var laterCutoff = firstCutoff.AddSeconds(1);

        await revocationsA.RevokeBeforeAsync(
            playerId,
            laterCutoff,
            CancellationToken.None);
        var stored = await revocationsB.RevokeBeforeAsync(
            playerId,
            firstCutoff,
            CancellationToken.None);

        Assert.Equal(
            laterCutoff.ToUnixTimeMilliseconds(),
            stored.ToUnixTimeMilliseconds());
        Assert.True(await revocationsB.IsRevokedAsync(
            playerId,
            firstCutoff,
            CancellationToken.None));
        Assert.False(await revocationsB.IsRevokedAsync(
            playerId,
            laterCutoff.AddMilliseconds(1),
            CancellationToken.None));
    }

    [ExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Redis_EventPublishedByOneHubReachesAnotherHubClient()
    {
        var options = CreateOptions();
        await using var connectionsA = new LobbyPersistenceConnections(options);
        await using var connectionsB = new LobbyPersistenceConnections(options);
        var presence = new RedisOnlinePresenceService(connectionsB, options, TimeProvider.System);
        var hubA = CreateHub(options, connectionsA, presence);
        var hubB = CreateHub(options, connectionsB, presence);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var socket = new CapturedWebSocket();

        await hubB.StartAsync(cancellation.Token);
        var client = hubB.HandleClientAsync(
            new PlayerIdentity("external-event-player", "External Event Player", "Test"),
            socket,
            cancellation.Token);
        try
        {
            var initial = Deserialize(await socket.ReadSentAsync(cancellation.Token));
            await hubA.PublishAsync(
                LobbyEventTypes.RoomUpdated,
                new { roomCode = "810004" },
                cancellation.Token);
            var received = Deserialize(await socket.ReadSentAsync(cancellation.Token));

            Assert.Equal(LobbyEventTypes.LobbyUpdated, initial.Type);
            Assert.Equal(LobbyEventTypes.RoomUpdated, received.Type);
            Assert.True(received.Sequence > initial.Sequence);
        }
        finally
        {
            cancellation.Cancel();
            await client.WaitAsync(TimeSpan.FromSeconds(5));
            await hubB.StopAsync(CancellationToken.None);
        }
    }

    [ExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_EventHistorySurvivesRedisExpiryAndProjectsPlayerHistory()
    {
        var options = CreateOptions();
        await using var connections = new LobbyPersistenceConnections(options);
        var store = CreateStore(options, connections);
        await store.InitializeAsync(CancellationToken.None);
        var monitoring = new RedisRoomMonitoringStore(connections, options);
        var playerId = $"external-history-{Guid.NewGuid():N}";
        var room = NewRoom(playerId, RandomRoomCode());
        Assert.Equal(
            CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);
        var eventId = Guid.NewGuid().ToString();
        var roomEvent = new RoomTimelineEvent(
            eventId,
            "PlayerConnectionChanged",
            room.CreatedAtUtc.AddMinutes(1),
            room.StateSequence,
            "trace-history-001",
            new Dictionary<string, object?>
            {
                ["playerId"] = playerId,
                ["from"] = "Connected",
                ["to"] = "Disconnected",
                ["trustee"] = true
            });
        await monitoring.AppendEventAsync(
            room.RoomId,
            roomEvent,
            CancellationToken.None);
        await monitoring.AppendEventAsync(
            room.RoomId,
            roomEvent,
            CancellationToken.None);
        var closed = room with
        {
            PlayerIds = [],
            Lifecycle = RoomLifecycle.Failed,
            StateSequence = room.StateSequence + 1,
            UpdatedAtUtc = room.CreatedAtUtc.AddMinutes(2)
        };
        Assert.True(await store.UpdateRoomAsync(closed, CancellationToken.None));

        // 模拟 Redis 热数据过期；历史读取必须从 PostgreSQL 权威库恢复。
        await connections.Redis.GetDatabase().KeyDeleteAsync(
            $"{options.Value.Persistence.RedisKeyPrefix}:monitor:room:{room.RoomId}:events");
        var restored = await monitoring.ListEventsAsync(
            room.RoomId,
            200,
            CancellationToken.None);
        var histories = new PostgresPlayerHistoryStore(connections);
        var roomHistory = await histories.ListRoomsAsync(
            playerId, 100, null, null, CancellationToken.None);
        var connectionHistory = await histories.ListConnectionsAsync(
            playerId, 100, null, null, CancellationToken.None);

        Assert.Single(restored, item => item.EventId == eventId);
        Assert.Single(roomHistory.Items);
        Assert.NotNull(roomHistory.Items[0].LeftAtUtc);
        Assert.Single(connectionHistory.Items, item => item.EventId == eventId);
    }

    private static IOptions<LobbyOptions> CreateOptions()
    {
        var postgres = Environment.GetEnvironmentVariable("LOBBY_TEST_POSTGRES")!;
        var redis = Environment.GetEnvironmentVariable("LOBBY_TEST_REDIS")!;
        return Microsoft.Extensions.Options.Options.Create(new LobbyOptions
        {
            PresenceTimeoutSeconds = 15,
            IdempotencyTtlSeconds = 60,
            IdempotencyLockSeconds = 5,
            Persistence = new LobbyPersistenceOptions
            {
                Mode = "RedisPostgres",
                PostgresConnectionString = postgres,
                RedisConnectionString = redis,
                RedisKeyPrefix = $"guiyang:lobby:external:{Guid.NewGuid():N}"
            }
        });
    }

    private static RedisPostgresLobbyStore CreateStore(
        IOptions<LobbyOptions> options,
        LobbyPersistenceConnections connections) => new(
            options,
            connections,
            NullLogger<RedisPostgresLobbyStore>.Instance);

    private static LobbyEventHub CreateHub(
        IOptions<LobbyOptions> options,
        LobbyPersistenceConnections connections,
        IOnlinePresenceService presence) => new(
            options,
            connections,
            presence,
            TimeProvider.System,
            NullLogger<LobbyEventHub>.Instance);

    private static LobbyRoom NewRoom(string playerId, string roomCode)
    {
        var now = DateTimeOffset.UtcNow;
        return new LobbyRoom
        {
            RoomId = Guid.NewGuid().ToString("N"),
            RoomCode = roomCode,
            OwnerPlayerId = playerId,
            RoundCount = 4,
            PublicRoom = true,
            AutoStart = true,
            MaximumPlayers = 4,
            RuleSnapshot = new Dictionary<string, object?> { ["ruleId"] = "GuiyangMainstreamV1" },
            Lifecycle = RoomLifecycle.Waiting,
            PlayerIds = [playerId],
            MatchId = Guid.NewGuid().ToString("N"),
            StateSequence = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private static string RandomRoomCode() => Random.Shared.Next(0, 1_000_000).ToString("D6");

    private static LobbyEventEnvelope Deserialize(string payload) =>
        JsonSerializer.Deserialize<LobbyEventEnvelope>(
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Lobby event payload is invalid.");

    private sealed class CapturedWebSocket : WebSocket
    {
        private readonly Channel<string> sent = Channel.CreateUnbounded<string>();
        private WebSocketState state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public Task<string> ReadSentAsync(CancellationToken cancellationToken) =>
            sent.Reader.ReadAsync(cancellationToken).AsTask();

        public override void Abort() => state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            state = WebSocketState.Closed;
            sent.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent.Writer.TryWrite(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}
