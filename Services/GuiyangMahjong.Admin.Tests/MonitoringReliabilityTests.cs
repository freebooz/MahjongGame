using System.Diagnostics;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Tests;

/// <summary>
/// 验证工作流 C 的硬超时、最后成功快照、熔断短路、部分成功和高危操作实时校验。
/// </summary>
public sealed class MonitoringReliabilityTests
{
    /// <summary>
    /// 即使下游完全忽略取消，Admin 等待时间仍由独立硬超时限定，并记录受控超时状态。
    /// </summary>
    [Fact]
    public async Task IgnoredCancellationStillReturnsWithinHardTimeout()
    {
        var service = CreateReliability();
        var neverCompletes = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var result = await service.ExecuteAsync(
            "Lobby",
            "rooms",
            true,
            TimeSpan.FromMilliseconds(40),
            _ => neverCompletes.Task,
            () => "empty",
            true,
            CancellationToken.None);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal("empty", result.Value);
        Assert.False(result.IsLive);
        Assert.Equal("Unavailable", result.Health.Status);
        Assert.Equal("SOURCE_TIMEOUT", result.Health.ErrorCode);
        Assert.Equal(1, result.Health.TimeoutCount);
    }

    /// <summary>
    /// 实时请求失败后必须返回有版本的最后成功快照，并醒目标记为 Stale 而不是 Healthy。
    /// </summary>
    [Fact]
    public async Task FailureUsesVersionedLastSuccessSnapshotAsStale()
    {
        var service = CreateReliability();
        var live = await service.ExecuteAsync(
            "Allocator",
            "instances",
            true,
            TimeSpan.FromSeconds(1),
            _ => Task.FromResult<IReadOnlyList<string>>(["instance-a"]),
            () => Array.Empty<string>(),
            true,
            CancellationToken.None);

        var stale = await service.ExecuteAsync<IReadOnlyList<string>>(
            "Allocator",
            "instances",
            true,
            TimeSpan.FromSeconds(1),
            _ => throw new HttpRequestException("internal endpoint must not leak"),
            () => Array.Empty<string>(),
            true,
            CancellationToken.None);

        Assert.True(live.IsLive);
        Assert.False(stale.IsLive);
        Assert.Equal(["instance-a"], stale.Value);
        Assert.Equal("Stale", stale.Health.Status);
        Assert.Equal(live.Health.SnapshotVersion, stale.Health.SnapshotVersion);
        Assert.DoesNotContain("endpoint", stale.Health.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 连续失败达到阈值后，后续调用必须被熔断器短路，避免对故障来源形成请求风暴。
    /// </summary>
    [Fact]
    public async Task ConsecutiveFailuresOpenCircuitAndShortCircuitFollowingCall()
    {
        var service = CreateReliability(failureThreshold: 2);
        var invocationCount = 0;
        Task<string> Fail(CancellationToken _)
        {
            invocationCount++;
            throw new HttpRequestException("downstream failed");
        }

        _ = await service.ExecuteAsync(
            "Auth", "players", true, TimeSpan.FromSeconds(1),
            Fail, () => "empty", true, CancellationToken.None);
        _ = await service.ExecuteAsync(
            "Auth", "players", true, TimeSpan.FromSeconds(1),
            Fail, () => "empty", true, CancellationToken.None);
        var rejected = await service.ExecuteAsync(
            "Auth", "players", true, TimeSpan.FromSeconds(1),
            Fail, () => "empty", true, CancellationToken.None);

        Assert.Equal(2, invocationCount);
        Assert.Equal("Open", rejected.Health.CircuitState);
        Assert.Equal("CIRCUIT_OPEN", rejected.Health.ErrorCode);
    }

    /// <summary>
    /// Allocator 故障时总览仍应返回 Lobby 房间，并通过部分成功契约标明缺失来源。
    /// </summary>
    [Fact]
    public async Task OverviewKeepsLobbyRoomsWhenAllocatorIsUnavailable()
    {
        var admin = CreateOptions(failureThreshold: 3);
        var service = CreateReliability(admin);
        var aggregation = new MonitoringAggregationService(
            new ReliableLobbyClient(),
            new FailingAllocatorClient(),
            service,
            Microsoft.Extensions.Options.Options.Create(admin),
            TimeProvider.System);

        var overview = await aggregation.GetOverviewAsync(CancellationToken.None);

        Assert.Equal(1, overview.TotalRooms);
        Assert.NotNull(overview.Reliability);
        Assert.True(overview.Reliability.Partial);
        Assert.False(overview.Reliability.SafeForHighRiskActions);
        Assert.Contains(
            overview.Reliability.Sources,
            item => item.Source == "Allocator" && item.Status == "Unavailable");
    }

    /// <summary>
    /// 高危管理读取不得复用 Allocator 的陈旧快照，即使只读页面仍能成功显示缓存数据。
    /// </summary>
    [Fact]
    public async Task HighRiskRoomReadRejectsStaleDependency()
    {
        var admin = CreateOptions(failureThreshold: 3);
        var reliability = CreateReliability(admin);
        var allocator = new SwitchableAllocatorClient();
        var aggregation = new MonitoringAggregationService(
            new ReliableLobbyClient(),
            allocator,
            reliability,
            Microsoft.Extensions.Options.Options.Create(admin),
            TimeProvider.System);
        _ = await aggregation.GetOverviewAsync(CancellationToken.None);
        allocator.Fail = true;

        await Assert.ThrowsAsync<MonitoringFreshDataRequiredException>(() =>
            aggregation.GetRoomForActionAsync(
                ReliableLobbyClient.RoomId,
                CancellationToken.None));
    }

    private static MonitoringSourceReliabilityService CreateReliability(
        int failureThreshold = 3) =>
        CreateReliability(CreateOptions(failureThreshold));

    private static MonitoringSourceReliabilityService CreateReliability(
        AdminOptions options) =>
        new(
            Microsoft.Extensions.Options.Options.Create(options),
            TimeProvider.System,
            NullLogger<MonitoringSourceReliabilityService>.Instance);

    private static AdminOptions CreateOptions(int failureThreshold) =>
        new()
        {
            MonitoringReliability = new MonitoringReliabilityOptions
            {
                CircuitFailureThreshold = failureThreshold,
                CircuitBreakSeconds = 30,
                CircuitMaxBreakSeconds = 120,
                StaleAfterSeconds = 1,
                SnapshotTtlSeconds = 60,
                MaxSnapshotEntries = 16
            },
            Auth = new AuthMonitoringOptions
            {
                Enabled = true,
                MonitoringToken = "test-auth-monitoring-token-that-is-long-enough"
            },
            Lobby = new LobbyMonitoringOptions
            {
                Enabled = true,
                MonitoringToken = "test-lobby-monitoring-token-that-is-long-enough"
            },
            Allocators =
            [
                new AllocatorMonitoringOptions
                {
                    Enabled = true,
                    MonitoringToken = "test-allocator-monitoring-token-long-enough"
                }
            ]
        };

    private sealed class ReliableLobbyClient : ILobbyMonitoringClient
    {
        public const string RoomId = "room-reliability-test";

        public Task<IReadOnlyList<RoomMonitorSnapshot>> ListRoomsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RoomMonitorSnapshot>>(
            [
                new RoomMonitorSnapshot
                {
                    RoomId = RoomId,
                    RoomCode = "990001",
                    OwnerPlayerId = "player-owner",
                    RoundCount = 8,
                    PublicRoom = true,
                    AutoStart = true,
                    MaximumPlayers = 4,
                    RuleSnapshot = new Dictionary<string, JsonElement>
                    {
                        ["gameMode"] = JsonSerializer.SerializeToElement("Standard")
                    },
                    Lifecycle = "Playing",
                    PlayerIds = ["player-owner"],
                    MatchId = "match-reliability-test",
                    StateSequence = 1,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                }
            ]);

        /// <summary>可靠性测试只需要单页房间，以隔离来源熔断行为。</summary>
        public async Task<CursorPage<RoomMonitorSnapshot>> ListRoomsPageAsync(
            string? lifecycle,
            string? gameMode,
            string? search,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var rooms = cursor is null
                ? await ListRoomsAsync(cancellationToken)
                : [];
            return new CursorPage<RoomMonitorSnapshot>(
                rooms.ToArray(),
                null,
                false,
                pageSize);
        }

        public Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
            string roomId,
            CancellationToken cancellationToken) =>
            Task.FromResult<RoomRuntimeTelemetry?>(null);

        public Task<RoomTimelineEvent[]> ListEventsAsync(
            string roomId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<RoomTimelineEvent>());

        public Task<PlayerPresenceSnapshot[]> GetPlayerPresenceAsync(
            IReadOnlyCollection<string> playerIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<PlayerPresenceSnapshot>());
    }

    private sealed class FailingAllocatorClient : IAllocatorMonitoringClient
    {
        public Task<IReadOnlyList<MonitoredInstance>> ListInstancesAsync(
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("allocator unavailable");
    }

    private sealed class SwitchableAllocatorClient : IAllocatorMonitoringClient
    {
        public bool Fail { get; set; }

        public Task<IReadOnlyList<MonitoredInstance>> ListInstancesAsync(
            CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("allocator unavailable");
            return Task.FromResult<IReadOnlyList<MonitoredInstance>>([]);
        }
    }

}
