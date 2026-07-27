using System.Collections.Concurrent;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Security;
using GuiyangMahjong.Allocator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Tests;

public sealed class GameServerInstanceManagerTests
{
    [Fact]
    public async Task ConcurrentAllocations_UseUniquePortsAndProcesses()
    {
        var fixture = CreateFixture(19000, 19009);
        var allocations = await Task.WhenAll(Enumerable.Range(0, 10).Select(index =>
            fixture.Manager.AllocateAsync(
                Guid.NewGuid().ToString(),
                new AllocationRequest($"room-{index}", $"match-{index}", "test"),
                CancellationToken.None)));

        Assert.Equal(10, allocations.Select(x => x.Port).Distinct().Count());
        Assert.Equal(10, allocations.Select(x => x.ServerInstanceId).Distinct().Count());
        Assert.Equal(0, fixture.Ports.AvailableCount);
    }

    [Fact]
    public async Task AgonesAllocation_UsesReturnedRoute_RegistersAndDeletesGameServerOnDrain()
    {
        var agones = new FakeAgonesAllocationClient();
        var fixture = CreateFixture(19000, 19000, AllocatorBackendMode.Agones, agones);
        var allocation = await AllocateAsync(fixture, "room-agones");
        var launch = Assert.Single(agones.Allocations);

        Assert.Equal("203.0.113.25", fixture.Manager.Get(allocation.ServerInstanceId)?.AdvertisedIp);
        Assert.Equal(30123, allocation.Port);
        Assert.Equal(allocation.ServerInstanceId, launch.ServerInstanceId);

        await fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            allocation.ServerInstanceId,
            new ConfirmRegistrationRequest(
                launch.RoomId, "203.0.113.25", 30123, launch.BuildVersion, launch.RegistrationCredential),
            CancellationToken.None);
        var stopped = await fixture.Manager.DrainAsync(allocation.ServerInstanceId, CancellationToken.None);

        Assert.Equal(GameServerInstanceState.Stopped, stopped.State);
        Assert.Equal(["guiyang-mahjong-test"], agones.Shutdowns);
        Assert.Equal(1, fixture.Ports.AvailableCount);
    }

    [Fact]
    public async Task RegistrationCredential_IsOneTimeAndInvalidCredentialIsRejected()
    {
        var fixture = CreateFixture();
        var allocation = await AllocateAsync(fixture, "room-registration");
        var launch = fixture.Launcher.Specs[allocation.ServerInstanceId];

        await Assert.ThrowsAsync<AllocatorOperationException>(() => fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            allocation.ServerInstanceId,
            NewRegistration(launch, "invalid"),
            CancellationToken.None));

        var acknowledged = await fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            allocation.ServerInstanceId,
            NewRegistration(launch, launch.RegistrationCredential),
            CancellationToken.None);

        Assert.True(acknowledged.Accepted);
        Assert.NotEmpty(acknowledged.HeartbeatCredential);
        await Assert.ThrowsAsync<AllocatorOperationException>(() => fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            allocation.ServerInstanceId,
            NewRegistration(launch, launch.RegistrationCredential),
            CancellationToken.None));
    }

    [Fact]
    public async Task HeartbeatTimeout_StopsProcessBeforePortIsReclaimed()
    {
        var fixture = CreateFixture(19100, 19100);
        var allocation = await AllocateAsync(fixture, "room-timeout");
        var launch = fixture.Launcher.Specs[allocation.ServerInstanceId];
        await fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            allocation.ServerInstanceId,
            NewRegistration(launch, launch.RegistrationCredential),
            CancellationToken.None);

        fixture.Time.Advance(TimeSpan.FromSeconds(16));
        await fixture.Manager.MonitorAsync(CancellationToken.None);

        var process = fixture.Launcher.Processes[allocation.ServerInstanceId];
        var snapshot = fixture.Manager.Get(allocation.ServerInstanceId);
        Assert.True(process.HasExited);
        Assert.Equal(GameServerInstanceState.Failed, snapshot?.State);
        Assert.Equal(1, fixture.Ports.AvailableCount);
        Assert.Single(fixture.Notifier.Failures);
    }

    [Fact]
    public async Task Drain_StopsInstanceAndMakesPortReusable()
    {
        var fixture = CreateFixture(19200, 19200);
        var first = await AllocateAsync(fixture, "room-drain");
        var launch = fixture.Launcher.Specs[first.ServerInstanceId];
        await fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            first.ServerInstanceId,
            NewRegistration(launch, launch.RegistrationCredential),
            CancellationToken.None);

        var stopped = await fixture.Manager.DrainAsync(first.ServerInstanceId, CancellationToken.None);
        var second = await AllocateAsync(fixture, "room-after-drain");

        Assert.Equal(GameServerInstanceState.Stopped, stopped.State);
        Assert.Equal(first.Port, second.Port);
        Assert.True(fixture.Launcher.Processes[first.ServerInstanceId].HasExited);
    }

    [Fact]
    public async Task AdminTerminationChecksExpectedStateAndIsIdempotent()
    {
        var fixture = CreateFixture(19205, 19205);
        var allocation = await AllocateAsync(fixture, "room-admin-terminate");
        await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            fixture.Manager.TerminateAbnormalAsync(
                allocation.ServerInstanceId,
                GameServerInstanceState.Allocated,
                CancellationToken.None));

        var first = await fixture.Manager.TerminateAbnormalAsync(
            allocation.ServerInstanceId,
            GameServerInstanceState.Starting,
            CancellationToken.None);
        var duplicate = await fixture.Manager.TerminateAbnormalAsync(
            allocation.ServerInstanceId,
            GameServerInstanceState.Starting,
            CancellationToken.None);

        Assert.Equal(GameServerInstanceState.Stopped, first.Instance.State);
        Assert.False(first.AlreadyStopped);
        Assert.Equal(GameServerInstanceState.Stopped, duplicate.Instance.State);
        Assert.True(duplicate.AlreadyStopped);
        Assert.True(
            fixture.Launcher.Processes[allocation.ServerInstanceId].HasExited);
        Assert.Equal(1, fixture.Ports.AvailableCount);
    }

    [Fact]
    public async Task Drain_CallerCancellationDuringShutdown_StillStopsAndReleasesPort()
    {
        var fixture = CreateFixture(19210, 19210);
        var allocation = await AllocateAsync(fixture, "room-cancelled-drain");
        var launch = fixture.Launcher.Specs[allocation.ServerInstanceId];
        await fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            allocation.ServerInstanceId,
            NewRegistration(launch, launch.RegistrationCredential),
            CancellationToken.None);
        using var callerCancellation = new CancellationTokenSource();
        fixture.Launcher.Processes[allocation.ServerInstanceId].BeforeStop = token =>
        {
            callerCancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };

        var stopped = await fixture.Manager.DrainAsync(
            allocation.ServerInstanceId, callerCancellation.Token);
        var replacement = await AllocateAsync(fixture, "room-after-cancelled-drain");

        Assert.True(callerCancellation.IsCancellationRequested);
        Assert.Equal(GameServerInstanceState.Stopped, stopped.State);
        Assert.Equal(allocation.Port, replacement.Port);
    }

    [Fact]
    public async Task Drain_RetriesPersistedDrainingInstance()
    {
        var fixture = CreateFixture(19220, 19220);
        var allocation = await AllocateAsync(fixture, "room-retry-drain");
        var launch = fixture.Launcher.Specs[allocation.ServerInstanceId];
        await fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            allocation.ServerInstanceId,
            NewRegistration(launch, launch.RegistrationCredential),
            CancellationToken.None);
        var process = fixture.Launcher.Processes[allocation.ServerInstanceId];
        process.BeforeStop = _ => throw new IOException("simulated one-time shutdown failure");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Manager.DrainAsync(allocation.ServerInstanceId, CancellationToken.None));
        Assert.Equal(
            GameServerInstanceState.Draining,
            fixture.Manager.Get(allocation.ServerInstanceId)?.State);

        process.BeforeStop = null;
        var stopped = await fixture.Manager.DrainAsync(
            allocation.ServerInstanceId, CancellationToken.None);

        Assert.Equal(GameServerInstanceState.Stopped, stopped.State);
        Assert.Equal(1, fixture.Ports.AvailableCount);
    }

    [Fact]
    public async Task Restart_ReattachesLiveProcess_ReservesPort_AndKeepsHeartbeatCredential()
    {
        var fixture = CreateFixture(19300, 19300);
        var allocation = await AllocateAsync(fixture, "room-restart");
        var launch = fixture.Launcher.Specs[allocation.ServerInstanceId];
        var registration = await fixture.Manager.ConfirmRegistrationAsync(
            Guid.NewGuid().ToString(),
            allocation.ServerInstanceId,
            NewRegistration(launch, launch.RegistrationCredential),
            CancellationToken.None);

        var restartedPorts = new PortLeasePool(fixture.Options);
        var restartedManager = new GameServerInstanceManager(
            restartedPorts,
            new InstanceCredentialService(),
            fixture.Launcher,
            new DisabledAgonesAllocationClient(),
            fixture.Notifier,
            fixture.StateStore,
            fixture.Options,
            fixture.Time,
            NullLogger<GameServerInstanceManager>.Instance);
        await restartedManager.InitializeAsync(CancellationToken.None);

        Assert.True(restartedManager.IsInitialized);
        Assert.Equal(0, restartedPorts.AvailableCount);
        Assert.Equal(GameServerInstanceState.Allocated,
            restartedManager.Get(allocation.ServerInstanceId)?.State);
        await restartedManager.RecordHeartbeatAsync(
            allocation.ServerInstanceId,
            new InstanceHeartbeatRequest(
                launch.RoomId,
                registration.HeartbeatCredential,
                1,
                "Waiting",
                0,
                launch.BuildVersion,
                fixture.Time.GetUtcNow()),
            CancellationToken.None);
    }

    [Fact]
    public async Task Restart_MissingProcess_FailsInstanceAndReleasesPort()
    {
        var fixture = CreateFixture(19400, 19400);
        var allocation = await AllocateAsync(fixture, "room-missing-process");
        fixture.Launcher.Processes[allocation.ServerInstanceId].Exit();
        var restartedPorts = new PortLeasePool(fixture.Options);
        var restartedManager = new GameServerInstanceManager(
            restartedPorts,
            new InstanceCredentialService(),
            fixture.Launcher,
            new DisabledAgonesAllocationClient(),
            fixture.Notifier,
            fixture.StateStore,
            fixture.Options,
            fixture.Time,
            NullLogger<GameServerInstanceManager>.Instance);

        await restartedManager.InitializeAsync(CancellationToken.None);

        Assert.Equal(GameServerInstanceState.Failed,
            restartedManager.Get(allocation.ServerInstanceId)?.State);
        Assert.Equal(1, restartedPorts.AvailableCount);
        Assert.Contains(fixture.Notifier.Failures,
            failure => failure.ServerInstanceId == allocation.ServerInstanceId);
    }

    [Fact]
    public async Task FailureNotification_IsPersistedAndRetriedAfterTransientError()
    {
        var fixture = CreateFixture(19500, 19500);
        var allocation = await AllocateAsync(fixture, "room-notification-retry");
        fixture.Notifier.FailuresRemaining = 1;
        fixture.Time.Advance(TimeSpan.FromSeconds(31));

        await fixture.Manager.MonitorAsync(CancellationToken.None);
        Assert.Empty(fixture.Notifier.Failures);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Manager.MonitorAsync(CancellationToken.None);

        Assert.Single(fixture.Notifier.Failures,
            failure => failure.ServerInstanceId == allocation.ServerInstanceId);
    }

    private static Task<AllocationResponse> AllocateAsync(Fixture fixture, string roomId) =>
        fixture.Manager.AllocateAsync(
            Guid.NewGuid().ToString(),
            new AllocationRequest(roomId, $"match-{roomId}", "test"),
            CancellationToken.None);

    private static ConfirmRegistrationRequest NewRegistration(GameServerLaunchSpec spec, string credential) => new(
        spec.RoomId,
        spec.AdvertisedIp,
        spec.Port,
        spec.BuildVersion,
        credential);

    private static Fixture CreateFixture(
        int portStart = 19000,
        int portEnd = 19010,
        AllocatorBackendMode backend = AllocatorBackendMode.LocalProcess,
        IAgonesAllocationClient? agones = null)
    {
        var allocatorOptions = Microsoft.Extensions.Options.Options.Create(new AllocatorOptions
        {
            Backend = backend,
            PortStart = portStart,
            PortEnd = portEnd,
            RegistrationTimeoutSeconds = 30,
            HeartbeatTimeoutSeconds = 15,
            HeartbeatIntervalSeconds = 3,
            AdvertisedIp = "127.0.0.1",
            LobbyInternalUrl = "http://127.0.0.1:18080",
            ServiceToken = "test-only-allocator-service-token-long-enough",
            LobbyCallbackToken = "test-only-lobby-callback-token-long-enough",
            JoinTicketSigningKey = "test-only-join-ticket-signing-key-long-enough"
        });
        var ports = new PortLeasePool(allocatorOptions);
        var launcher = new FakeProcessLauncher();
        var notifier = new RecordingFailureNotifier();
        var stateStore = new InMemoryAllocatorStateStore();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-18T00:00:00Z"));
        var manager = new GameServerInstanceManager(
            ports,
            new InstanceCredentialService(),
            launcher,
            agones ?? new DisabledAgonesAllocationClient(),
            notifier,
            stateStore,
            allocatorOptions,
            time,
            NullLogger<GameServerInstanceManager>.Instance);
        return new Fixture(manager, ports, launcher, notifier, stateStore, allocatorOptions, time);
    }

    private sealed record Fixture(
        GameServerInstanceManager Manager,
        PortLeasePool Ports,
        FakeProcessLauncher Launcher,
        RecordingFailureNotifier Notifier,
        InMemoryAllocatorStateStore StateStore,
        IOptions<AllocatorOptions> Options,
        MutableTimeProvider Time);

    private sealed class FakeProcessLauncher : IGameServerProcessLauncher
    {
        public ConcurrentDictionary<string, GameServerLaunchSpec> Specs { get; } = new();
        public ConcurrentDictionary<string, FakeManagedProcess> Processes { get; } = new();

        public Task<IManagedGameServerProcess> LaunchAsync(
            GameServerLaunchSpec spec,
            CancellationToken cancellationToken)
        {
            Specs[spec.ServerInstanceId] = spec;
            var process = new FakeManagedProcess(Processes.Count + 1000);
            Processes[spec.ServerInstanceId] = process;
            return Task.FromResult<IManagedGameServerProcess>(process);
        }

        public Task<IManagedGameServerProcess?> TryAttachAsync(
            int processId,
            DateTimeOffset expectedStartedAtUtc,
            CancellationToken cancellationToken)
        {
            var process = Processes.Values.FirstOrDefault(candidate =>
                candidate.ProcessId == processId
                && candidate.StartedAtUtc == expectedStartedAtUtc
                && !candidate.HasExited);
            return Task.FromResult<IManagedGameServerProcess?>(process);
        }
    }

    private sealed class FakeManagedProcess(int processId) : IManagedGameServerProcess
    {
        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public bool HasExited { get; private set; }
        public Action<CancellationToken>? BeforeStop { get; set; }

        public void Exit() => HasExited = true;

        public ValueTask StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
        {
            BeforeStop?.Invoke(cancellationToken);
            HasExited = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAgonesAllocationClient : IAgonesAllocationClient
    {
        public List<AgonesAllocationSpec> Allocations { get; } = [];
        public List<string> Shutdowns { get; } = [];

        public Task<AgonesAllocationResult> AllocateAsync(
            AgonesAllocationSpec spec, CancellationToken cancellationToken)
        {
            Allocations.Add(spec);
            return Task.FromResult(new AgonesAllocationResult(
                "guiyang-mahjong-test", "203.0.113.25", 30123));
        }

        public Task ShutdownAsync(string gameServerName, CancellationToken cancellationToken)
        {
            Shutdowns.Add(gameServerName);
            return Task.CompletedTask;
        }

        public Task<string?> GetGameServerStateAsync(
            string gameServerName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("Allocated");

        public Task<bool> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class InMemoryAllocatorStateStore : IAllocatorStateStore
    {
        private AllocatorStateDocument state = new(1, DateTimeOffset.MinValue, []);

        public Task<AllocatorStateDocument> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(state);

        public Task SaveAsync(AllocatorStateDocument state, CancellationToken cancellationToken)
        {
            this.state = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFailureNotifier : IInstanceFailureNotifier
    {
        public List<InstanceFailureNotification> Failures { get; } = [];
        public int FailuresRemaining { get; set; }
        public Task NotifyAsync(InstanceFailureNotification notification, CancellationToken cancellationToken)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new HttpRequestException("Simulated callback outage.");
            }
            Failures.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
