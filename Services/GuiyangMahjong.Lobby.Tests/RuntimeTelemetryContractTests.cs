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
/// 验证 Dedicated Server 心跳经过 Lobby 校验和映射后仍保持 v1 遥测语义。
/// 这些测试是线协议门禁，字段被删除、改名、改单位或错误填充零值时必须失败。
/// </summary>
public sealed class RuntimeTelemetryContractTests
{
    /// <summary>
    /// 验证 v1 心跳的全部可选字段能够无损写入房间运行快照，
    /// 同时确认新鲜度使用 Lobby 接收时钟而不是发送端时钟。
    /// </summary>
    [Fact]
    public async Task V1Heartbeat_AllFieldsReachLobbyRuntimeSnapshotWithoutLoss()
    {
        var fixture = await CreateRegisteredFixtureAsync();
        var sentAtUtc = fixture.Time.GetUtcNow().AddSeconds(-3);
        var gameStartedAtUtc = fixture.Time.GetUtcNow().AddMinutes(-2);
        var disconnectedAtUtc = fixture.Time.GetUtcNow().AddSeconds(-20);
        var connectionEventId = Guid.NewGuid().ToString();
        var heartbeat = new GameServerHeartbeat(
            fixture.RoomId,
            "heartbeat-credential",
            1,
            "Playing",
            2,
            "contract-build-1",
            sentAtUtc,
            [fixture.Owner.PlayerId],
            gameStartedAtUtc,
            16.67,
            59.98,
            1_234,
            256L * 1024 * 1024,
            37.5,
            9_876,
            5_432,
            [
                new PlayerRuntimeTelemetry(
                    fixture.Owner.PlayerId,
                    0,
                    "Reconnecting",
                    42.5,
                    disconnectedAtUtc,
                    true,
                    fixture.Time.GetUtcNow().AddSeconds(-10),
                    disconnectedAtUtc,
                    null,
                    "NetworkInterrupted",
                    3,
                    connectionEventId,
                    1.25,
                    4,
                    2)
            ],
            1,
            250,
            [
                new RpcMethodTelemetry(
                    "Server.RequestAction", 100, 2, 1, 0, 3.5, 8.2)
            ],
            new SettlementRuntimeTelemetry(
                "Calculating",
                fixture.MatchId,
                null,
                null,
                null,
                null,
                null)) with
        {
            RoomEpoch = 1,
            FencingToken = 1,
            ActionSequence = 88,
            StateVersion = 34,
            SnapshotVersion = 7,
            SnapshotCreatedAtUtc = fixture.Time.GetUtcNow().AddSeconds(-2),
            RecoveryState = "Healthy",
            LastTraceId = "trace-runtime-contract"
        };

        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(),
            fixture.Allocator.ServerInstanceId,
            heartbeat,
            CancellationToken.None);

        var runtime = await fixture.Monitoring.GetRuntimeAsync(
            fixture.RoomId,
            CancellationToken.None);
        Assert.NotNull(runtime);
        Assert.Equal(fixture.Time.GetUtcNow(), runtime.ObservedAtUtc);
        Assert.NotEqual(sentAtUtc, runtime.ObservedAtUtc);
        Assert.Equal(gameStartedAtUtc, runtime.GameStartedAtUtc);
        Assert.Equal(2, runtime.CurrentRound);
        Assert.Equal(16.67, runtime.ServerTickMilliseconds);
        Assert.Equal(59.98, runtime.ServerFramesPerSecond);
        Assert.Equal(1_234, runtime.RpcReceivedCount);
        Assert.Equal(256L * 1024 * 1024, runtime.ProcessMemoryBytes);
        Assert.Equal(37.5, runtime.ProcessCpuPercent);
        Assert.Equal(9_876, runtime.NetworkIngressBytes);
        Assert.Equal(5_432, runtime.NetworkEgressBytes);
        Assert.Equal("contract-build-1", runtime.BuildVersion);
        Assert.Equal(1, runtime.TelemetrySchemaVersion);
        Assert.Equal(250, runtime.ProcessCpuSampleWindowMilliseconds);
        Assert.Null(runtime.NetworkIngressBytesPerSecond);
        Assert.Null(runtime.NetworkEgressBytesPerSecond);
        var rpcMethod = Assert.Single(runtime.RpcMethods!);
        Assert.Equal("Server.RequestAction", rpcMethod.MethodName);
        Assert.Equal(100, rpcMethod.ReceivedCount);
        Assert.Equal(3.5, rpcMethod.P95DurationMilliseconds);
        Assert.Equal("Calculating", runtime.Settlement?.Status);
        Assert.Equal(88, runtime.ActionSequence);
        Assert.Equal(34, runtime.StateVersion);
        Assert.Equal(1, runtime.RoomEpoch);
        Assert.Equal(7, runtime.SnapshotVersion);
        Assert.Equal("Healthy", runtime.RecoveryState);
        Assert.Equal("trace-runtime-contract", runtime.LastTraceId);

        var player = Assert.Single(runtime.Players);
        Assert.Equal(fixture.Owner.PlayerId, player.PlayerId);
        Assert.Equal(0, player.SeatIndex);
        Assert.Equal("Reconnecting", player.ConnectionState);
        Assert.Equal(42.5, player.LatencyMilliseconds);
        Assert.Equal(disconnectedAtUtc, player.DisconnectedAtUtc);
        Assert.True(player.Trustee);
        Assert.Equal("NetworkInterrupted", player.DisconnectReason);
        Assert.Equal(3, player.ConnectionStateSequence);
        Assert.Equal(connectionEventId, player.ConnectionEventId);
        Assert.Equal(1.25, player.PacketLossPercent);
        Assert.Equal(4, player.IllegalActionCount);
        Assert.Equal(2, player.ReconnectCount);
    }

    /// <summary>
    /// 验证旧构建未携带版本和可选指标时仍按 v1 接受，
    /// 并保持 null，防止 Admin 将“生产者未知”误显示为零。
    /// </summary>
    [Fact]
    public async Task LegacyHeartbeat_MissingOptionalFieldsDefaultsToV1AndPreservesNulls()
    {
        var fixture = await CreateRegisteredFixtureAsync();
        var legacyJson = $$"""
        {
          "roomId": "{{fixture.RoomId}}",
          "heartbeatCredential": "heartbeat-credential",
          "connectedPlayers": 0,
          "roomLifecycle": "Waiting",
          "roundId": 0,
          "buildVersion": "legacy-build",
          "sentAtUtc": "{{fixture.Time.GetUtcNow():O}}"
        }
        """;
        var heartbeat = JsonSerializer.Deserialize<GameServerHeartbeat>(
            legacyJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(heartbeat);
        Assert.Equal(1, heartbeat.TelemetrySchemaVersion);

        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(),
            fixture.Allocator.ServerInstanceId,
            heartbeat,
            CancellationToken.None);

        var runtime = await fixture.Monitoring.GetRuntimeAsync(
            fixture.RoomId,
            CancellationToken.None);
        Assert.NotNull(runtime);
        Assert.Equal(1, runtime.TelemetrySchemaVersion);
        Assert.Null(runtime.ServerTickMilliseconds);
        Assert.Null(runtime.ServerFramesPerSecond);
        Assert.Null(runtime.RpcReceivedCount);
        Assert.Null(runtime.ProcessMemoryBytes);
        Assert.Null(runtime.ProcessCpuPercent);
        Assert.Null(runtime.NetworkIngressBytes);
        Assert.Null(runtime.NetworkEgressBytes);
        Assert.Empty(runtime.Players);
    }

    /// <summary>
    /// 验证未知主版本在调用 Allocator 和写入监控存储前失败关闭，
    /// 避免不同单位或空值语义进入同一看板。
    /// </summary>
    [Fact]
    public async Task UnknownTelemetrySchemaVersion_IsRejectedBeforeSideEffects()
    {
        var fixture = await CreateRegisteredFixtureAsync();
        var heartbeat = NewHeartbeat(fixture) with { TelemetrySchemaVersion = 2 };

        var exception = await Assert.ThrowsAsync<LobbyOperationException>(() =>
            fixture.Service.RecordGameServerHeartbeatAsync(
                Guid.NewGuid().ToString(),
                fixture.Allocator.ServerInstanceId,
                heartbeat,
                CancellationToken.None));

        Assert.Equal(LobbyErrorCode.InvalidRequest, exception.ErrorCode);
        Assert.Equal(0, fixture.Allocator.HeartbeatCount);
        Assert.Null(await fixture.Monitoring.GetRuntimeAsync(
            fixture.RoomId,
            CancellationToken.None));
    }

    /// <summary>
    /// 验证 v1 CPU 使用节点总容量归一化的 0～100 百分比口径；
    /// 采用“单核可超过 100%”口径的生产者必须在进入存储前被拒绝。
    /// </summary>
    [Fact]
    public async Task V1Heartbeat_ProcessCpuAboveOneHundredIsRejected()
    {
        var fixture = await CreateRegisteredFixtureAsync();
        var heartbeat = NewHeartbeat(fixture) with { ProcessCpuPercent = 100.01 };

        var exception = await Assert.ThrowsAsync<LobbyOperationException>(() =>
            fixture.Service.RecordGameServerHeartbeatAsync(
                Guid.NewGuid().ToString(),
                fixture.Allocator.ServerInstanceId,
                heartbeat,
                CancellationToken.None));

        Assert.Equal(LobbyErrorCode.InvalidRequest, exception.ErrorCode);
        Assert.Null(await fixture.Monitoring.GetRuntimeAsync(
            fixture.RoomId,
            CancellationToken.None));
    }

    /// <summary>
    /// 验证 Lobby 只对同一实例的单调网络计数器计算速率；
    /// 计数器重置时速率回到 null，不能产生负值或异常尖峰。
    /// </summary>
    [Fact]
    public async Task NetworkCounters_ComputeRatesAndSuppressResetSpike()
    {
        var fixture = await CreateRegisteredFixtureAsync();
        var first = NewHeartbeat(fixture) with
        {
            NetworkIngressBytes = 1_000,
            NetworkEgressBytes = 2_000
        };
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId, first, CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        var second = first with
        {
            SentAtUtc = fixture.Time.GetUtcNow(),
            NetworkIngressBytes = 1_600,
            NetworkEgressBytes = 2_800
        };
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId, second, CancellationToken.None);
        var runtime = await fixture.Monitoring.GetRuntimeAsync(fixture.RoomId, CancellationToken.None);
        Assert.NotNull(runtime);
        Assert.Equal(300, runtime.NetworkIngressBytesPerSecond);
        Assert.Equal(400, runtime.NetworkEgressBytesPerSecond);

        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        var reset = second with
        {
            SentAtUtc = fixture.Time.GetUtcNow(),
            NetworkIngressBytes = 10,
            NetworkEgressBytes = 20
        };
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId, reset, CancellationToken.None);
        runtime = await fixture.Monitoring.GetRuntimeAsync(fixture.RoomId, CancellationToken.None);
        Assert.NotNull(runtime);
        Assert.Null(runtime.NetworkIngressBytesPerSecond);
        Assert.Null(runtime.NetworkEgressBytesPerSecond);
    }

    /// <summary>
    /// 验证连接状态序号和 EventId 作为幂等键；重复心跳不会重复写入掉线事件。
    /// </summary>
    [Fact]
    public async Task RepeatedConnectionHeartbeat_DoesNotDuplicateTimelineEvent()
    {
        var fixture = await CreateRegisteredFixtureAsync();
        var connected = NewHeartbeat(fixture) with
        {
            ConnectedPlayers = 1,
            ConnectedPlayerIds = [fixture.Owner.PlayerId],
            Players =
            [
                new PlayerRuntimeTelemetry(
                    fixture.Owner.PlayerId, 0, "Connected", 20, null, false,
                    null, fixture.Time.GetUtcNow(), null, null, 1, Guid.NewGuid().ToString())
            ]
        };
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId, connected, CancellationToken.None);

        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        var eventId = Guid.NewGuid().ToString();
        var disconnected = connected with
        {
            Players =
            [
                new PlayerRuntimeTelemetry(
                    fixture.Owner.PlayerId, 0, "Disconnected", 0, fixture.Time.GetUtcNow(), true,
                    fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(), null,
                    "NetworkInterrupted", 2, eventId)
            ]
        };
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId, disconnected, CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await fixture.Service.RecordGameServerHeartbeatAsync(
            Guid.NewGuid().ToString(), fixture.Allocator.ServerInstanceId,
            disconnected with { SentAtUtc = fixture.Time.GetUtcNow() },
            CancellationToken.None);

        var events = await fixture.Monitoring.ListEventsAsync(
            fixture.RoomId, 100, CancellationToken.None);
        Assert.Single(events, item => item.EventType == "PlayerConnectionChanged");
        Assert.Single(events, item => item.EventType == "PlayerTrusteeChanged");
        Assert.Contains(events, item => item.EventId == eventId);
    }

    /// <summary>
    /// 验证监控存储自身也以 EventId 幂等；即使多个心跳请求并发读取到相同旧快照，
    /// 最终仍只持久化一次连接事件。
    /// </summary>
    [Fact]
    public async Task MonitoringStore_ConcurrentDuplicateEventIdIsPersistedOnce()
    {
        var store = new InMemoryRoomMonitoringStore();
        var eventId = Guid.NewGuid().ToString();
        var roomEvent = new RoomTimelineEvent(
            eventId,
            "PlayerConnectionChanged",
            DateTimeOffset.Parse("2026-07-29T01:00:00Z"),
            2,
            Guid.NewGuid().ToString(),
            new Dictionary<string, object?> { ["playerId"] = "player-1" });

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            store.AppendEventAsync("room-concurrent", roomEvent, CancellationToken.None)));

        var events = await store.ListEventsAsync("room-concurrent", 100, CancellationToken.None);
        Assert.Single(events);
        Assert.Equal(eventId, events[0].EventId);
    }

    /// <summary>
    /// 创建已完成 Allocator 注册的测试房间，使测试只关注心跳到运行快照的真实领域链路。
    /// </summary>
    private static async Task<Fixture> CreateRegisteredFixtureAsync()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LobbyOptions
        {
            TokenSigningKey = LobbyWebApplicationFactory.SigningKey,
            JoinTicketSigningKey = "test-only-join-ticket-signing-key-which-is-long-enough",
            InternalServiceToken = "test-only-internal-service-token-which-is-long-enough",
            RoomCodeRetryLimit = 100,
            MaximumPlayersPerRoom = 4,
            Allocator = new AllocatorClientOptions
            {
                Enabled = true,
                GameServerBuildVersion = "contract-test"
            }
        });
        var store = new InMemoryLobbyStore();
        var monitoring = new InMemoryRoomMonitoringStore();
        var allocator = new RecordingAllocatorClient();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-29T01:00:00Z"));
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
        var owner = new PlayerIdentity("telemetry-contract-owner", "契约测试玩家", "Guest");
        var created = await service.CreateRoomAsync(
            Guid.NewGuid().ToString(),
            owner,
            new CreateRoomRequest(
                4,
                true,
                true,
                false,
                null,
                new Dictionary<string, object?> { ["ruleId"] = "GuiyangMainstreamV1" }),
            CancellationToken.None);
        var room = await store.GetRoomByIdAsync(created.RoomId, CancellationToken.None);
        Assert.NotNull(room);
        await service.RegisterGameServerAsync(
            Guid.NewGuid().ToString(),
            new GameServerRegistration(
                allocator.ServerInstanceId,
                room.RoomId,
                room.MatchId,
                "127.0.0.1",
                19000,
                "contract-test",
                "one-time-registration-credential"),
            CancellationToken.None);
        return new Fixture(
            service,
            monitoring,
            allocator,
            time,
            owner,
            room.RoomId,
            room.MatchId);
    }

    /// <summary>
    /// 生成只包含 v1 必填字段的心跳，用于验证单一契约分支。
    /// </summary>
    private static GameServerHeartbeat NewHeartbeat(Fixture fixture) => new(
        fixture.RoomId,
        "heartbeat-credential",
        0,
        "Waiting",
        0,
        "contract-build-1",
        fixture.Time.GetUtcNow());

    /// <summary>
    /// 保存契约测试需要的真实领域服务及可观测依赖，所有对象仅在单个测试内拥有。
    /// </summary>
    private sealed record Fixture(
        LobbyService Service,
        IRoomMonitoringStore Monitoring,
        RecordingAllocatorClient Allocator,
        FixedTimeProvider Time,
        PlayerIdentity Owner,
        string RoomId,
        string MatchId);

    /// <summary>
    /// 记录心跳副作用的 Allocator 测试替身；不启动外部进程，也不修改持久化状态。
    /// </summary>
    private sealed class RecordingAllocatorClient : IAllocatorClient
    {
        /// <summary>契约测试中固定启用 Allocator 路径。</summary>
        public bool Enabled => true;

        /// <summary>当前测试房间绑定的实例标识，生命周期等同于测试夹具。</summary>
        public string ServerInstanceId { get; } = Guid.NewGuid().ToString();

        /// <summary>已转发到 Allocator 的心跳次数，用于确认失败关闭发生在副作用之前。</summary>
        public int HeartbeatCount { get; private set; }

        /// <summary>测试依赖始终就绪，不引入网络失败因素。</summary>
        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        /// <summary>返回固定实例分配结果，使房间进入等待注册状态。</summary>
        public Task<AllocatorAllocation> AllocateAsync(
            string requestId,
            string roomId,
            string matchId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AllocatorAllocation(
                requestId,
                roomId,
                ServerInstanceId,
                19000,
                "Starting"));

        /// <summary>确认测试实例注册，并返回仅限测试使用的心跳凭证。</summary>
        public Task<AllocatorRegistrationAck> ConfirmRegistrationAsync(
            string requestId,
            GameServerRegistration request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AllocatorRegistrationAck(
                requestId,
                request.ServerInstanceId,
                true,
                3,
                "heartbeat-credential"));

        /// <summary>记录已通过版本预检的心跳转发次数，不产生其他副作用。</summary>
        public Task RecordHeartbeatAsync(
            string requestId,
            string serverInstanceId,
            GameServerHeartbeat request,
            CancellationToken cancellationToken)
        {
            HeartbeatCount++;
            return Task.CompletedTask;
        }

        /// <summary>契约测试不验证实例回收，因此排空操作为空实现。</summary>
        public Task DrainAsync(
            string requestId,
            string serverInstanceId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// 屏蔽 WebSocket 事件发布，使测试专注于遥测字段的领域映射。
    /// </summary>
    private sealed class NoOpEventPublisher : ILobbyEventPublisher
    {
        /// <summary>有意忽略事件，避免引入与本契约无关的异步副作用。</summary>
        public Task PublishAsync(
            string type,
            object data,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
