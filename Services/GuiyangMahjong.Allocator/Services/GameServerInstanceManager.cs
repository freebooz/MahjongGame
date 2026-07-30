// 游戏服实例管理器：协调容量选择、端口租约、进程启动、注册超时、心跳和回收状态机。
// 每次转换必须幂等且记录原因；进程启动不确定、心跳过期和停止超时需要进入明确异常状态。
using System.Collections.Concurrent;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Security;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

/// <summary>
/// Dedicated Server 实例聚合与生命周期协调器。
/// gate 串行化端口、状态和持久化变更；外部进程/Agones 调用通过明确的补偿路径回收资源，
/// 任何公开操作都要求初始化完成并返回不含凭据的快照。
/// </summary>
public sealed class GameServerInstanceManager
{
    // instances 支持无锁只读快照；所有复合写入仍必须进入 gate 保证跨字段原子性。
    private readonly ConcurrentDictionary<string, GameServerInstance> instances = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly PortLeasePool ports;
    private readonly InstanceCredentialService credentials;
    private readonly IGameServerProcessLauncher launcher;
    private readonly IAgonesAllocationClient agones;
    private readonly IInstanceFailureNotifier failureNotifier;
    private readonly IAllocatorStateStore stateStore;
    private readonly AllocatorOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<GameServerInstanceManager> logger;
    private volatile bool initialized;
    private DateTimeOffset lastPersistedAtUtc = DateTimeOffset.MinValue;

    /// <summary>状态恢复完成标记；为 false 时分配和实例控制入口必须拒绝服务。</summary>
    public bool IsInitialized => initialized;

    /// <summary>注入端口池、凭据、启动后端、通知器、状态存储和可测试时间源。</summary>
    public GameServerInstanceManager(
        PortLeasePool ports,
        InstanceCredentialService credentials,
        IGameServerProcessLauncher launcher,
        IAgonesAllocationClient agones,
        IInstanceFailureNotifier failureNotifier,
        IAllocatorStateStore stateStore,
        IOptions<AllocatorOptions> options,
        TimeProvider timeProvider,
        ILogger<GameServerInstanceManager> logger)
    {
        this.ports = ports;
        this.credentials = credentials;
        this.launcher = launcher;
        this.agones = agones;
        this.failureNotifier = failureNotifier;
        this.stateStore = stateStore;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 从持久化文档恢复实例、重新占用端口并验证/接管仍存活的本机或 Agones 实例。
    /// 不兼容版本、端口冲突或不安全进程身份会使初始化失败；恢复出的故障会持久化并通知 Lobby。
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var document = await stateStore.LoadAsync(cancellationToken);
        if (document.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported allocator state schema {document.SchemaVersion}.");

        var failures = new List<InstanceFailureNotification>();
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var persisted in document.Instances)
            {
                if (instances.ContainsKey(persisted.ServerInstanceId)) continue;
                var instance = Restore(persisted);
                instances[instance.ServerInstanceId] = instance;
                if (instance.State is GameServerInstanceState.Stopped or GameServerInstanceState.Failed)
                {
                    instance.PortReleased = true;
                    continue;
                }

                if (options.Backend == AllocatorBackendMode.LocalProcess && !ports.TryReserve(instance.Port))
                {
                    throw new InvalidDataException(
                        $"Persisted allocator port {instance.Port} cannot be reserved safely.");
                }
                instance.PortReleased = options.Backend == AllocatorBackendMode.Agones;

                if (options.Backend == AllocatorBackendMode.Agones)
                {
                    if (string.IsNullOrWhiteSpace(instance.OrchestratorResourceName))
                    {
                        await MarkFailedAsync(instance, "Agones resource missing during allocator reconciliation", cancellationToken);
                    }
                    else if (!string.Equals(
                        await agones.GetGameServerStateAsync(instance.OrchestratorResourceName, cancellationToken),
                        "Allocated",
                        StringComparison.Ordinal))
                    {
                        await MarkFailedAsync(instance, "Agones GameServer is not allocated during reconciliation", cancellationToken);
                    }
                    else if (instance.State == GameServerInstanceState.Draining)
                    {
                        await agones.ShutdownAsync(instance.OrchestratorResourceName, cancellationToken);
                        InstanceStateMachine.Transition(instance, GameServerInstanceState.Stopped);
                    }
                    else if (instance.State == GameServerInstanceState.Allocated)
                    {
                        instance.LastHeartbeatAtUtc = timeProvider.GetUtcNow();
                    }
                    continue;
                }

                if (persisted.ProcessId is not null && persisted.ProcessStartedAtUtc is not null)
                {
                    instance.Process = await launcher.TryAttachAsync(
                        persisted.ProcessId.Value,
                        persisted.ProcessStartedAtUtc.Value,
                        cancellationToken);
                }

                string? failureReason = null;
                if (instance.Process is null || instance.Process.HasExited)
                    failureReason = "Process missing during allocator startup reconciliation";
                else if (instance.State == GameServerInstanceState.Starting
                         && timeProvider.GetUtcNow() >= instance.RegistrationExpireAtUtc)
                    failureReason = "Registration expired during allocator restart";

                if (failureReason is not null)
                {
                    await MarkFailedAsync(instance, failureReason, cancellationToken);
                }
                else if (instance.State == GameServerInstanceState.Draining)
                {
                    await instance.Process!.StopAsync(
                        TimeSpan.FromSeconds(options.DrainGraceSeconds), cancellationToken);
                    InstanceStateMachine.Transition(instance, GameServerInstanceState.Stopped);
                    ReleasePort(instance);
                }
                else
                {
                    if (instance.State == GameServerInstanceState.Allocated)
                        instance.LastHeartbeatAtUtc = timeProvider.GetUtcNow();
                    logger.LogInformation(
                        "Recovered GameServer InstanceId={InstanceId} ProcessId={ProcessId} Port={Port} State={State}",
                        instance.ServerInstanceId,
                        instance.Process!.ProcessId,
                        instance.Port,
                        instance.State);
                }
            }
            QueuePendingFailuresUnsafe(timeProvider.GetUtcNow(), failures);
            await PersistAsync(cancellationToken);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }

        await DeliverFailuresAsync(failures, cancellationToken);
    }

    /// <summary>
    /// 按 requestId 幂等分配实例，原子租用端口并启动本机进程或 Agones 资源。
    /// 启动失败会回收端口、记录失败原因并持久化，不会返回半初始化实例。
    /// </summary>
    public async Task<AllocationResponse> AllocateAsync(
        string requestId,
        AllocationRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.MatchId))
        {
            throw new AllocatorOperationException("RoomId and MatchId are required.", 400);
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = instances.Values.FirstOrDefault(instance =>
                instance.RoomId == request.RoomId
                && instance.State is not GameServerInstanceState.Stopped and not GameServerInstanceState.Failed);
            if (existing is not null) return ToAllocationResponse(requestId, existing);

            var registration = credentials.Generate();
            var now = timeProvider.GetUtcNow();
            var serverInstanceId = Guid.NewGuid().ToString();
            AgonesAllocationResult? agonesAllocation = null;
            var port = 0;
            var advertisedIp = options.AdvertisedIp;
            if (options.Backend == AllocatorBackendMode.Agones)
            {
                agonesAllocation = await agones.AllocateAsync(new AgonesAllocationSpec(
                    request.RoomId,
                    request.MatchId,
                    serverInstanceId,
                    registration.Plaintext,
                    options.LobbyInternalUrl,
                    request.BuildVersion), cancellationToken);
                port = agonesAllocation.Port;
                advertisedIp = agonesAllocation.Address;
            }
            else
            {
                port = ports.Acquire();
            }
            var instance = new GameServerInstance
            {
                ServerInstanceId = serverInstanceId,
                RoomId = request.RoomId,
                MatchId = request.MatchId,
                Port = port,
                AdvertisedIp = advertisedIp,
                RegistrationCredentialHash = registration.Hash,
                RegistrationExpireAtUtc = now.AddSeconds(options.RegistrationTimeoutSeconds),
                StartedAtUtc = now,
                BuildVersion = request.BuildVersion,
                PortReleased = options.Backend == AllocatorBackendMode.Agones,
                OrchestratorResourceName = agonesAllocation?.GameServerName
            };
            instances[instance.ServerInstanceId] = instance;
            await PersistAsync(cancellationToken);

            if (options.Backend == AllocatorBackendMode.LocalProcess) try
            {
                instance.Process = await launcher.LaunchAsync(new GameServerLaunchSpec(
                    instance.RoomId,
                    instance.MatchId,
                    instance.ServerInstanceId,
                    instance.Port,
                    options.LobbyInternalUrl,
                    registration.Plaintext,
                    options.JoinTicketSigningKey,
                    request.BuildVersion,
                    instance.AdvertisedIp,
                    MatchResultOutboxPaths.GetInstancePath(options, instance.ServerInstanceId)), cancellationToken);
                instance.ProcessStartedAtUtc = instance.Process.StartedAtUtc;
                await PersistAsync(cancellationToken);
            }
            catch
            {
                await MarkFailedAsync(instance, "Process launch failed", cancellationToken);
                await PersistAsync(cancellationToken);
                throw;
            }

            return ToAllocationResponse(requestId, instance);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 校验一次性注册凭据、房间/版本/端口绑定并迁移实例到 Ready。
    /// 成功后生成独立心跳凭据；重复注册返回稳定回执而不重新签发状态。
    /// </summary>
    public async Task<ConfirmRegistrationResponse> ConfirmRegistrationAsync(
        string requestId,
        string serverInstanceId,
        ConfirmRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!instances.TryGetValue(serverInstanceId, out var instance))
                throw new AllocatorOperationException("GameServer instance was not found.", 404);
            if (instance.State != GameServerInstanceState.Starting)
                throw new AllocatorOperationException("GameServer cannot register in its current state.", 409);
            if (timeProvider.GetUtcNow() >= instance.RegistrationExpireAtUtc)
                throw new AllocatorOperationException("GameServer registration credential expired.", 401);
            if (instance.RoomId != request.RoomId
                || instance.Port != request.ListenPort
                || instance.AdvertisedIp != request.ListenIp
                || instance.BuildVersion != request.BuildVersion)
                throw new AllocatorOperationException("Registration does not match the allocation.", 400);
            if (!credentials.Verify(request.RegistrationCredential, instance.RegistrationCredentialHash))
                throw new AllocatorOperationException("GameServer registration credential is invalid.", 401);

            instance.RegistrationCredentialHash = [];
            InstanceStateMachine.Transition(instance, GameServerInstanceState.Ready);
            var heartbeat = credentials.Generate();
            instance.HeartbeatCredentialHash = heartbeat.Hash;
            instance.RegisteredAtUtc = timeProvider.GetUtcNow();
            instance.LastHeartbeatAtUtc = instance.RegisteredAtUtc;
            InstanceStateMachine.Transition(instance, GameServerInstanceState.Allocated);
            await PersistAsync(cancellationToken);
            logger.LogInformation(
                "GameServer registered InstanceId={InstanceId} RoomId={RoomId} Port={Port}",
                instance.ServerInstanceId,
                instance.RoomId,
                instance.Port);
            return new ConfirmRegistrationResponse(
                requestId,
                instance.ServerInstanceId,
                true,
                options.HeartbeatIntervalSeconds,
                heartbeat.Plaintext);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 验证实例心跳凭据和房间绑定，刷新最后心跳及生命周期观察值。
    /// 陈旧、错误实例或终态实例心跳会被拒绝，凭据永不进入日志。
    /// </summary>
    public async Task RecordHeartbeatAsync(
        string serverInstanceId,
        InstanceHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!instances.TryGetValue(serverInstanceId, out var instance))
                throw new AllocatorOperationException("GameServer instance was not found.", 404);
            if (instance.State != GameServerInstanceState.Allocated || instance.RoomId != request.RoomId)
                throw new AllocatorOperationException("GameServer cannot heartbeat in its current state.", 409);
            if (instance.HeartbeatCredentialHash is null
                || !credentials.Verify(request.HeartbeatCredential, instance.HeartbeatCredentialHash))
                throw new AllocatorOperationException("GameServer heartbeat credential is invalid.", 401);
            instance.LastHeartbeatAtUtc = timeProvider.GetUtcNow();
            await PersistAsync(cancellationToken, force: false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>将实例迁移到 Draining 并持久化，重复请求返回当前快照。</summary>
    public async Task<GameServerInstanceSnapshot> DrainAsync(
        string serverInstanceId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await DrainLockedAsync(
                serverInstanceId,
                null,
                CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 仅在状态仍与管理员确认快照一致时终止异常实例。
    /// 本机进程或 Agones 关闭完成后释放端口并进入 Stopped；终态重放返回 AlreadyStopped。
    /// </summary>
    public async Task<(GameServerInstanceSnapshot Instance, bool AlreadyStopped)>
        TerminateAbnormalAsync(
            string serverInstanceId,
            GameServerInstanceState expectedState,
            CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!instances.TryGetValue(serverInstanceId, out var instance))
                throw new AllocatorOperationException(
                    "GameServer instance was not found.",
                    404);
            var alreadyStopped = instance.State == GameServerInstanceState.Stopped;
            var snapshot = await DrainLockedAsync(
                serverInstanceId,
                expectedState,
                CancellationToken.None);
            return (snapshot, alreadyStopped);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GameServerInstanceSnapshot> DrainLockedAsync(
        string serverInstanceId,
        GameServerInstanceState? expectedState,
        CancellationToken cleanupToken)
    {
        // Once termination owns the instance lock it must finish even if the HTTP
        // caller times out. Otherwise a terminated process can remain persisted as
        // Draining forever and prevent deterministic port reuse.
        if (!instances.TryGetValue(serverInstanceId, out var instance))
            throw new AllocatorOperationException("GameServer instance was not found.", 404);
        if (instance.State == GameServerInstanceState.Stopped) return instance.Snapshot();
        if (expectedState.HasValue && instance.State != expectedState.Value)
        {
            throw new AllocatorOperationException(
                $"GameServer state changed from {expectedState.Value} to {instance.State}.",
                409);
        }
        if (instance.State == GameServerInstanceState.Failed)
        {
            InstanceStateMachine.Transition(instance, GameServerInstanceState.Stopped);
            ReleasePort(instance);
            await PersistAsync(cleanupToken);
            return instance.Snapshot();
        }
        if (expectedState.HasValue
            && instance.State is
                GameServerInstanceState.Starting or GameServerInstanceState.Ready)
        {
            if (instance.Process is not null)
                await instance.Process.StopAsync(TimeSpan.Zero, cleanupToken);
            else if (options.Backend == AllocatorBackendMode.Agones
                     && !string.IsNullOrWhiteSpace(instance.OrchestratorResourceName))
                await agones.ShutdownAsync(
                    instance.OrchestratorResourceName,
                    cleanupToken);
            InstanceStateMachine.Transition(
                instance,
                GameServerInstanceState.Failed);
            InstanceStateMachine.Transition(
                instance,
                GameServerInstanceState.Stopped);
            ReleasePort(instance);
            await PersistAsync(cleanupToken);
            return instance.Snapshot();
        }
        if (instance.State == GameServerInstanceState.Allocated)
        {
            InstanceStateMachine.Transition(instance, GameServerInstanceState.Draining);
            await PersistAsync(cleanupToken);
        }
        else if (instance.State != GameServerInstanceState.Draining)
        {
            throw new AllocatorOperationException(
                "Only allocated, failed, or already draining instances can terminate.",
                409);
        }
        if (instance.Process is not null)
        {
            await instance.Process.StopAsync(
                TimeSpan.FromSeconds(options.DrainGraceSeconds),
                cleanupToken);
        }
        else if (options.Backend == AllocatorBackendMode.Agones
                 && !string.IsNullOrWhiteSpace(instance.OrchestratorResourceName))
        {
            await agones.ShutdownAsync(
                instance.OrchestratorResourceName,
                cleanupToken);
        }
        InstanceStateMachine.Transition(instance, GameServerInstanceState.Stopped);
        ReleasePort(instance);
        await PersistAsync(cleanupToken);
        logger.LogInformation(
            "GameServer stopped and port returned InstanceId={InstanceId} Port={Port}",
            instance.ServerInstanceId,
            instance.Port);
        return instance.Snapshot();
    }

    /// <summary>按实例标识读取无凭据快照；不存在返回空。</summary>
    public GameServerInstanceSnapshot? Get(string serverInstanceId) =>
        instances.TryGetValue(serverInstanceId, out var instance) ? instance.Snapshot() : null;

    /// <summary>返回按端口稳定排序的实例快照数组；调用方不能修改内部聚合。</summary>
    public IReadOnlyList<GameServerInstanceSnapshot> List() =>
        instances.Values.Select(instance => instance.Snapshot()).OrderBy(x => x.Port).ToArray();

    /// <summary>
    /// 周期检测注册超时、心跳超时和进程退出，迁移失败状态、回收资源并通知 Lobby。
    /// 单次扫描形成一个持久化批次；通知失败保留重试标记而不回滚已确认故障。
    /// </summary>
    public async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var failures = new List<InstanceFailureNotification>();
        var changed = false;
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            foreach (var instance in instances.Values)
            {
                string? reason = null;
                if (instance.State == GameServerInstanceState.Starting
                    && (instance.Process?.HasExited == true || now >= instance.RegistrationExpireAtUtc))
                {
                    reason = instance.Process?.HasExited == true
                        ? "Process exited before registration"
                        : "Registration timed out";
                }
                else if (instance.State == GameServerInstanceState.Allocated
                    && (instance.Process?.HasExited == true
                        || instance.LastHeartbeatAtUtc is null
                        || now - instance.LastHeartbeatAtUtc.Value
                            >= TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds)))
                {
                    reason = instance.Process?.HasExited == true
                        ? "GameServer process exited"
                        : "Heartbeat timed out";
                }

                if (reason is null) continue;
                await MarkFailedAsync(instance, reason, cancellationToken);
                changed = true;
            }
            changed |= QueuePendingFailuresUnsafe(now, failures);
            if (changed) await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        await DeliverFailuresAsync(failures, cancellationToken);
    }

    private async Task MarkFailedAsync(
        GameServerInstance instance,
        string reason,
        CancellationToken cancellationToken)
    {
        if (instance.State is GameServerInstanceState.Failed or GameServerInstanceState.Stopped) return;
        if (instance.Process is { HasExited: false })
        {
            await instance.Process.StopAsync(TimeSpan.Zero, cancellationToken);
        }
        if (options.Backend == AllocatorBackendMode.Agones
            && !string.IsNullOrWhiteSpace(instance.OrchestratorResourceName))
        {
            await agones.ShutdownAsync(instance.OrchestratorResourceName, cancellationToken);
        }
        InstanceStateMachine.Transition(instance, GameServerInstanceState.Failed);
        instance.FailureReason = reason;
        instance.FailureNotified = false;
        instance.FailureNotificationAttemptedAtUtc = null;
        ReleasePort(instance);
        logger.LogWarning(
            "GameServer failed InstanceId={InstanceId} RoomId={RoomId} Reason={Reason}",
            instance.ServerInstanceId,
            instance.RoomId,
            reason);
    }

    private void ReleasePort(GameServerInstance instance)
    {
        if (instance.PortReleased) return;
        ports.Release(instance.Port);
        instance.PortReleased = true;
    }

    private bool QueuePendingFailuresUnsafe(
        DateTimeOffset now,
        List<InstanceFailureNotification> failures)
    {
        var changed = false;
        var retryInterval = TimeSpan.FromSeconds(options.FailureNotificationRetrySeconds);
        foreach (var instance in instances.Values.Where(instance =>
                     instance.State == GameServerInstanceState.Failed
                     && !instance.FailureNotified
                     && (instance.FailureNotificationAttemptedAtUtc is null
                         || now - instance.FailureNotificationAttemptedAtUtc.Value >= retryInterval)))
        {
            failures.Add(new InstanceFailureNotification(
                instance.ServerInstanceId,
                instance.RoomId,
                instance.FailureReason ?? "GameServer failed"));
            instance.FailureNotificationAttemptedAtUtc = now;
            changed = true;
        }
        return changed;
    }

    private async Task DeliverFailuresAsync(
        IReadOnlyList<InstanceFailureNotification> failures,
        CancellationToken cancellationToken)
    {
        var delivered = new List<string>();
        foreach (var failure in failures)
        {
            try
            {
                await failureNotifier.NotifyAsync(failure, cancellationToken);
                delivered.Add(failure.ServerInstanceId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "GameServer failure notification will be retried InstanceId={InstanceId}",
                    failure.ServerInstanceId);
            }
        }

        if (delivered.Count == 0) return;
        await gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var instanceId in delivered)
            {
                if (instances.TryGetValue(instanceId, out var instance)) instance.FailureNotified = true;
            }
            await PersistAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken, bool force = true)
    {
        var now = timeProvider.GetUtcNow();
        if (!force && now - lastPersistedAtUtc < TimeSpan.FromSeconds(options.StateCheckpointSeconds))
            return;
        var records = instances.Values
            .OrderBy(instance => instance.ServerInstanceId, StringComparer.Ordinal)
            .Select(instance => new PersistedGameServerInstance(
                instance.ServerInstanceId,
                instance.RoomId,
                instance.MatchId,
                instance.Port,
                instance.AdvertisedIp,
                Convert.ToBase64String(instance.RegistrationCredentialHash),
                instance.HeartbeatCredentialHash is null
                    ? null
                    : Convert.ToBase64String(instance.HeartbeatCredentialHash),
                instance.RegistrationExpireAtUtc,
                instance.StartedAtUtc,
                instance.ProcessStartedAtUtc,
                instance.Process?.ProcessId,
                instance.RegisteredAtUtc,
                instance.LastHeartbeatAtUtc,
                instance.BuildVersion,
                instance.State,
                instance.FailureReason,
                instance.FailureNotified,
                instance.FailureNotificationAttemptedAtUtc,
                instance.PortReleased,
                instance.OrchestratorResourceName))
            .ToArray();
        await stateStore.SaveAsync(
            new AllocatorStateDocument(1, now, records), cancellationToken);
        lastPersistedAtUtc = now;
    }

    private static GameServerInstance Restore(PersistedGameServerInstance persisted) => new()
    {
        ServerInstanceId = persisted.ServerInstanceId,
        RoomId = persisted.RoomId,
        MatchId = persisted.MatchId,
        Port = persisted.Port,
        AdvertisedIp = persisted.AdvertisedIp,
        RegistrationCredentialHash = Convert.FromBase64String(persisted.RegistrationCredentialHash),
        HeartbeatCredentialHash = persisted.HeartbeatCredentialHash is null
            ? null
            : Convert.FromBase64String(persisted.HeartbeatCredentialHash),
        RegistrationExpireAtUtc = persisted.RegistrationExpireAtUtc,
        StartedAtUtc = persisted.StartedAtUtc,
        ProcessStartedAtUtc = persisted.ProcessStartedAtUtc,
        RegisteredAtUtc = persisted.RegisteredAtUtc,
        LastHeartbeatAtUtc = persisted.LastHeartbeatAtUtc,
        BuildVersion = persisted.BuildVersion,
        State = persisted.State,
        FailureReason = persisted.FailureReason,
        FailureNotified = persisted.FailureNotified,
        FailureNotificationAttemptedAtUtc = persisted.FailureNotificationAttemptedAtUtc,
        PortReleased = persisted.PortReleased,
        OrchestratorResourceName = persisted.OrchestratorResourceName
    };

    private static AllocationResponse ToAllocationResponse(string requestId, GameServerInstance instance) => new(
        requestId,
        instance.RoomId,
        instance.ServerInstanceId,
        instance.Port,
        instance.State);
}
