// 验证游戏服实例管理状态机、端口租约、注册超时、心跳过期、并发分配和幂等回收。
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Providers;
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
        Assert.Equal("guiyang-zhua-ji", launch.GameType);
        Assert.Equal("local", launch.Region);
        Assert.Equal("guiyang-zhuoji-v1", launch.RuleSetVersion);
        Assert.Equal("1", launch.ProtocolVersion);
        Assert.Equal(4, launch.RequestedCapacity);
        Assert.Equal(allocation.RoomEpoch, launch.FencingToken);

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

    /// <summary>Agones 无兼容 Ready 实例时必须返回可重试的 503，而不是伪造地址。</summary>
    [Fact]
    public async Task AgonesWithoutCapacity_ReturnsServiceUnavailable()
    {
        var agones = new FakeAgonesAllocationClient
        {
            AllocationException = new AllocatorOperationException(
                "Agones has no compatible capacity.", 503)
        };
        var fixture = CreateFixture(19000, 19000, AllocatorBackendMode.Agones, agones);

        var exception = await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            AllocateAsync(fixture, "room-agones-no-capacity"));

        Assert.Equal(503, exception.StatusCode);
        Assert.Empty(fixture.Manager.List());
    }

    /// <summary>Agones 请求超过 Allocation Service 启动硬超时后必须映射为 504。</summary>
    [Fact]
    public async Task AgonesRequestTimeout_ReturnsGatewayTimeout()
    {
        var agones = new FakeAgonesAllocationClient { WaitForCancellation = true };
        var fixture = CreateFixture(
            19000,
            19000,
            AllocatorBackendMode.Agones,
            agones,
            startupTimeoutSeconds: 1);

        var exception = await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            AllocateAsync(fixture, "room-agones-timeout"));

        Assert.Equal(504, exception.StatusCode);
        Assert.Empty(fixture.Manager.List());
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
    public async Task Reallocation_CreatesNewEpochInstance_AndRejectsStaleEpochRegistration()
    {
        var fixture = CreateFixture(19200, 19201);
        var first = await fixture.Manager.AllocateAsync(
            "reallocate-room-epoch-1",
            new AllocationRequest(
                "room-reallocate",
                "match-reallocate",
                "test",
                1),
            CancellationToken.None);
        var second = await fixture.Manager.AllocateAsync(
            "reallocate-room-epoch-2",
            new AllocationRequest(
                "room-reallocate",
                "match-reallocate",
                "test",
                2),
            CancellationToken.None);
        var secondLaunch = fixture.Launcher.Specs[second.ServerInstanceId];

        Assert.NotEqual(first.ServerInstanceId, second.ServerInstanceId);
        Assert.Equal(1, first.RoomEpoch);
        Assert.Equal(2, second.RoomEpoch);
        Assert.Equal(2, secondLaunch.RoomEpoch);
        await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            fixture.Manager.ConfirmRegistrationAsync(
                "stale-registration",
                second.ServerInstanceId,
                NewRegistration(
                    secondLaunch with { RoomEpoch = 1 },
                    secondLaunch.RegistrationCredential),
                CancellationToken.None));

        var accepted = await fixture.Manager.ConfirmRegistrationAsync(
            "current-registration",
            second.ServerInstanceId,
            NewRegistration(
                secondLaunch,
                secondLaunch.RegistrationCredential),
            CancellationToken.None);
        Assert.Equal(2, accepted.RoomEpoch);
    }

    /// <summary>相同 allocation_id 和幂等键在响应丢失后必须返回同一实例，并可通过分配标识查询。</summary>
    [Fact]
    public async Task DuplicateAllocation_AfterResponseLoss_ReturnsPersistedResult()
    {
        var fixture = CreateFixture(19230, 19230);
        var request = new AllocationRequest(
            "room-response-loss",
            "match-response-loss",
            "test",
            1,
            "allocation-response-loss",
            IdempotencyKey: "idempotency-response-loss");

        var first = await fixture.Manager.AllocateAsync(
            "request-response-loss-1", request, CancellationToken.None);
        var duplicate = await fixture.Manager.AllocateAsync(
            "request-response-loss-2", request, CancellationToken.None);

        Assert.Equal(first.ServerInstanceId, duplicate.ServerInstanceId);
        Assert.Equal(
            first.ServerInstanceId,
            fixture.Manager.GetByAllocationId("allocation-response-loss")?.ServerInstanceId);
        Assert.Single(fixture.Launcher.Specs);
    }

    /// <summary>相同幂等键携带不同 Build 或规则约束时必须冲突，不能静默复用错误实例。</summary>
    [Fact]
    public async Task DuplicateIdempotencyKey_WithDifferentParameters_IsRejected()
    {
        var fixture = CreateFixture(19231, 19232);
        var first = new AllocationRequest(
            "room-idempotency-conflict",
            "match-idempotency-conflict",
            "build-a",
            IdempotencyKey: "same-idempotency-key");
        await fixture.Manager.AllocateAsync("request-conflict-a", first, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            fixture.Manager.AllocateAsync(
                "request-conflict-b",
                first with { BuildVersion = "build-b" },
                CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    /// <summary>房间出现更高 Epoch 后，任何较旧 Epoch 的新分配请求都必须被永久 fencing。</summary>
    [Fact]
    public async Task OlderEpochAllocation_IsRejectedAfterNewerEpochExists()
    {
        var fixture = CreateFixture(19233, 19234);
        await fixture.Manager.AllocateAsync(
            "newer-epoch",
            new AllocationRequest("room-old-epoch", "match-old-epoch", "test", 2),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            fixture.Manager.AllocateAsync(
                "older-epoch",
                new AllocationRequest("room-old-epoch", "match-old-epoch", "test", 1),
                CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    /// <summary>新 Epoch 分配完成后，旧实例延迟 Ready 回调不能进入 Allocated 状态。</summary>
    [Fact]
    public async Task OlderInstanceReadyCallback_CannotOverrideNewAllocation()
    {
        var fixture = CreateFixture(19235, 19236);
        var oldAllocation = await fixture.Manager.AllocateAsync(
            "ready-epoch-1",
            new AllocationRequest("room-stale-ready", "match-stale-ready", "test", 1),
            CancellationToken.None);
        await fixture.Manager.AllocateAsync(
            "ready-epoch-2",
            new AllocationRequest("room-stale-ready", "match-stale-ready", "test", 2),
            CancellationToken.None);
        var oldLaunch = fixture.Launcher.Specs[oldAllocation.ServerInstanceId];

        var exception = await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            fixture.Manager.ConfirmRegistrationAsync(
                "stale-ready",
                oldAllocation.ServerInstanceId,
                NewRegistration(oldLaunch, oldLaunch.RegistrationCredential),
                CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    /// <summary>旧实例已经 Ready 后再发生重分配，其后续心跳也不能刷新当前房间租约。</summary>
    [Fact]
    public async Task OlderInstanceHeartbeat_CannotRenewAfterNewAllocation()
    {
        var fixture = CreateFixture(19237, 19238);
        var oldAllocation = await fixture.Manager.AllocateAsync(
            "heartbeat-epoch-1",
            new AllocationRequest("room-stale-heartbeat", "match-stale-heartbeat", "test", 1),
            CancellationToken.None);
        var oldLaunch = fixture.Launcher.Specs[oldAllocation.ServerInstanceId];
        var registration = await fixture.Manager.ConfirmRegistrationAsync(
            "heartbeat-register-1",
            oldAllocation.ServerInstanceId,
            NewRegistration(oldLaunch, oldLaunch.RegistrationCredential),
            CancellationToken.None);
        await fixture.Manager.AllocateAsync(
            "heartbeat-epoch-2",
            new AllocationRequest("room-stale-heartbeat", "match-stale-heartbeat", "test", 2),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            fixture.Manager.RecordHeartbeatAsync(
                oldAllocation.ServerInstanceId,
                new InstanceHeartbeatRequest(
                    oldLaunch.RoomId,
                    registration.HeartbeatCredential,
                    1,
                    "Playing",
                    1,
                    oldLaunch.BuildVersion,
                    fixture.Time.GetUtcNow(),
                    oldLaunch.RoomEpoch,
                    oldLaunch.FencingToken),
                CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
    }

    /// <summary>本地启动失败或端口冲突时必须归还端口，不得留下半分配记录。</summary>
    [Fact]
    public async Task LocalProcessLaunchFailure_ReleasesPortAndDoesNotPersistAllocation()
    {
        var fixture = CreateFixture(19239, 19239);
        fixture.Launcher.LaunchException = new IOException("simulated port conflict");

        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.AllocateAsync(
            "port-conflict",
            new AllocationRequest("room-port-conflict", "match-port-conflict", "test"),
            CancellationToken.None));

        Assert.Equal(1, fixture.Ports.AvailableCount);
        Assert.Empty(fixture.Manager.List());
    }

    /// <summary>端口池首个候选被外部进程占用时，本地 Provider 必须原子跳过并使用下一端口。</summary>
    [Fact]
    public async Task LocalProcessPortConflict_SkipsExternallyOccupiedPort()
    {
        using var occupied = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var occupiedPort = ((IPEndPoint)occupied.LocalEndPoint!).Port;
        if (occupiedPort == 65535) return;
        var fixture = CreateFixture(
            occupiedPort,
            occupiedPort + 1,
            validateOperatingSystemPorts: true);

        var allocation = await AllocateAsync(fixture, "room-os-port-conflict");

        Assert.Equal(occupiedPort + 1, allocation.Port);
        Assert.Equal(1, fixture.Ports.AvailableCount);
    }

    /// <summary>进程创建后立即退出必须按启动失败处理并回收端口。</summary>
    [Fact]
    public async Task LocalProcessExitsImmediately_IsRejectedAndReleasesPort()
    {
        var fixture = CreateFixture(19240, 19240);
        fixture.Launcher.ExitImmediately = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.AllocateAsync(
            "immediate-exit",
            new AllocationRequest("room-immediate-exit", "match-immediate-exit", "test"),
            CancellationToken.None));

        Assert.Equal(1, fixture.Ports.AvailableCount);
        Assert.Empty(fixture.Manager.List());
    }

    /// <summary>Provider 启动超过硬超时必须返回 504 语义并释放端口。</summary>
    [Fact]
    public async Task LocalProcessStartupTimeout_IsClassifiedAndReleasesPort()
    {
        var fixture = CreateFixture(19241, 19241, startupTimeoutSeconds: 1);
        fixture.Launcher.WaitForCancellation = true;

        var exception = await Assert.ThrowsAsync<AllocatorOperationException>(() =>
            fixture.Manager.AllocateAsync(
                "startup-timeout",
                new AllocationRequest("room-startup-timeout", "match-startup-timeout", "test"),
                CancellationToken.None));

        Assert.Equal(504, exception.StatusCode);
        Assert.Equal(1, fixture.Ports.AvailableCount);
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

    /// <summary>实例未在 Ready/注册期限内回调时必须失败、终止进程并释放端口。</summary>
    [Fact]
    public async Task ReadyTimeout_FailsStartingInstanceAndReleasesPort()
    {
        var fixture = CreateFixture(19110, 19110);
        var allocation = await AllocateAsync(fixture, "room-ready-timeout");

        fixture.Time.Advance(TimeSpan.FromSeconds(31));
        await fixture.Manager.MonitorAsync(CancellationToken.None);

        Assert.Equal(
            GameServerInstanceState.Failed,
            fixture.Manager.Get(allocation.ServerInstanceId)?.State);
        Assert.True(fixture.Launcher.Processes[allocation.ServerInstanceId].HasExited);
        Assert.Equal(1, fixture.Ports.AvailableCount);
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
        Assert.Equal(
            TimeSpan.FromSeconds(3),
            fixture.Launcher.Processes[first.ServerInstanceId].LastGracePeriod);
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
        Assert.Equal(
            TimeSpan.Zero,
            fixture.Launcher.Processes[allocation.ServerInstanceId].LastGracePeriod);
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
        var restartedProvider = new LocalProcessGameServerProvider(
            restartedPorts,
            fixture.Launcher,
            fixture.Options,
            NullLogger<LocalProcessGameServerProvider>.Instance);
        var restartedManager = new GameServerInstanceManager(
            new InstanceCredentialService(),
            restartedProvider,
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

    /// <summary>服务重启后重复 allocation_id 必须返回持久化实例，不能再次启动进程或占用新端口。</summary>
    [Fact]
    public async Task Restart_DuplicateAllocationReturnsRecoveredInstance()
    {
        var fixture = CreateFixture(19310, 19311);
        var request = new AllocationRequest(
            "room-restart-idempotency",
            "match-restart-idempotency",
            "test",
            AllocationId: "allocation-restart-idempotency",
            IdempotencyKey: "key-restart-idempotency");
        var first = await fixture.Manager.AllocateAsync(
            "restart-idempotency-request", request, CancellationToken.None);
        var restartedPorts = new PortLeasePool(fixture.Options);
        var restartedProvider = new LocalProcessGameServerProvider(
            restartedPorts,
            fixture.Launcher,
            fixture.Options,
            NullLogger<LocalProcessGameServerProvider>.Instance);
        var restartedManager = new GameServerInstanceManager(
            new InstanceCredentialService(),
            restartedProvider,
            fixture.Notifier,
            fixture.StateStore,
            fixture.Options,
            fixture.Time,
            NullLogger<GameServerInstanceManager>.Instance);
        await restartedManager.InitializeAsync(CancellationToken.None);

        var duplicate = await restartedManager.AllocateAsync(
            "restart-idempotency-retry", request, CancellationToken.None);

        Assert.Equal(first.ServerInstanceId, duplicate.ServerInstanceId);
        Assert.Single(fixture.Launcher.Specs);
        Assert.Equal(1, restartedPorts.AvailableCount);
    }

    [Fact]
    public async Task Restart_MissingProcess_FailsInstanceAndReleasesPort()
    {
        var fixture = CreateFixture(19400, 19400);
        var allocation = await AllocateAsync(fixture, "room-missing-process");
        fixture.Launcher.Processes[allocation.ServerInstanceId].Exit();
        var restartedPorts = new PortLeasePool(fixture.Options);
        var restartedProvider = new LocalProcessGameServerProvider(
            restartedPorts,
            fixture.Launcher,
            fixture.Options,
            NullLogger<LocalProcessGameServerProvider>.Instance);
        var restartedManager = new GameServerInstanceManager(
            new InstanceCredentialService(),
            restartedProvider,
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

    /// <summary>启动核对必须反向报告状态文件之外的疑似托管进程，但不能自动终止它。</summary>
    [Fact]
    public async Task Restart_DetectsOrphanProcessWithoutTerminatingIt()
    {
        var fixture = CreateFixture(19410, 19410);
        fixture.Launcher.ObservedProcesses =
        [new ManagedGameServerProcessObservation(4242, fixture.Time.GetUtcNow())];

        await fixture.Manager.InitializeAsync(CancellationToken.None);

        Assert.Equal([4242], fixture.Manager.OrphanProcessIds);
    }

    /// <summary>活动租约恢复时 Provider 不匹配必须拒绝启动，防止同一房间运行中切换后端。</summary>
    [Fact]
    public async Task Restart_WithDifferentProvider_IsRejected()
    {
        var fixture = CreateFixture(19420, 19420);
        await AllocateAsync(fixture, "room-provider-switch");
        var agonesProvider = new AgonesGameServerProvider(new FakeAgonesAllocationClient());
        var restartedManager = new GameServerInstanceManager(
            new InstanceCredentialService(),
            agonesProvider,
            fixture.Notifier,
            fixture.StateStore,
            fixture.Options,
            fixture.Time,
            NullLogger<GameServerInstanceManager>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            restartedManager.InitializeAsync(CancellationToken.None));
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
        credential,
        spec.RoomEpoch,
        spec.FencingToken);

    private static Fixture CreateFixture(
        int portStart = 19000,
        int portEnd = 19010,
        AllocatorBackendMode backend = AllocatorBackendMode.LocalProcess,
        IAgonesAllocationClient? agones = null,
        int startupTimeoutSeconds = 30,
        bool validateOperatingSystemPorts = false)
    {
        var allocatorOptions = Microsoft.Extensions.Options.Options.Create(new AllocatorOptions
        {
            Backend = backend,
            PortStart = portStart,
            PortEnd = portEnd,
            ValidateOperatingSystemPortAvailability = validateOperatingSystemPorts,
            RegistrationTimeoutSeconds = 30,
            StartupTimeoutSeconds = startupTimeoutSeconds,
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
        IGameServerProvider provider = backend == AllocatorBackendMode.Agones
            ? new AgonesGameServerProvider(
                agones ?? throw new InvalidOperationException("Agones test provider is required."))
            : new LocalProcessGameServerProvider(
                ports,
                launcher,
                allocatorOptions,
                NullLogger<LocalProcessGameServerProvider>.Instance);
        var manager = new GameServerInstanceManager(
            new InstanceCredentialService(),
            provider,
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
        public Exception? LaunchException { get; set; }
        public bool ExitImmediately { get; set; }
        public bool WaitForCancellation { get; set; }
        public IReadOnlyList<ManagedGameServerProcessObservation> ObservedProcesses { get; set; } = [];

        public async Task<IManagedGameServerProcess> LaunchAsync(
            GameServerLaunchSpec spec,
            CancellationToken cancellationToken)
        {
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (LaunchException is not null) throw LaunchException;
            Specs[spec.ServerInstanceId] = spec;
            var process = new FakeManagedProcess(Processes.Count + 1000);
            if (ExitImmediately) process.Exit();
            Processes[spec.ServerInstanceId] = process;
            return process;
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

        public Task<IReadOnlyList<ManagedGameServerProcessObservation>> ListManagedProcessesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(ObservedProcesses);
    }

    private sealed class FakeManagedProcess(int processId) : IManagedGameServerProcess
    {
        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public bool HasExited { get; private set; }
        public Action<CancellationToken>? BeforeStop { get; set; }
        public TimeSpan? LastGracePeriod { get; private set; }

        public void Exit() => HasExited = true;

        public ValueTask StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken)
        {
            BeforeStop?.Invoke(cancellationToken);
            LastGracePeriod = gracePeriod;
            HasExited = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAgonesAllocationClient : IAgonesAllocationClient
    {
        public List<AgonesAllocationSpec> Allocations { get; } = [];
        public List<string> Shutdowns { get; } = [];
        public Exception? AllocationException { get; init; }
        public bool WaitForCancellation { get; init; }

        public async Task<AgonesAllocationResult> AllocateAsync(
            AgonesAllocationSpec spec, CancellationToken cancellationToken)
        {
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (AllocationException is not null) throw AllocationException;
            Allocations.Add(spec);
            return new AgonesAllocationResult(
                "guiyang-mahjong-test", "203.0.113.25", 30123);
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
