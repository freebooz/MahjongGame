// 游戏服实例管理器：协调容量选择、端口租约、进程启动、注册超时、心跳和回收状态机。
// 每次转换必须幂等且记录原因；进程启动不确定、心跳过期和停止超时需要进入明确异常状态。
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Providers;
using GuiyangMahjong.Allocator.Security;
using GuiyangMahjong.Observability;
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
    private readonly InstanceCredentialService credentials;
    private readonly IGameServerProvider provider;
    private readonly IInstanceFailureNotifier failureNotifier;
    private readonly IAllocatorStateStore stateStore;
    private readonly AllocatorOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<GameServerInstanceManager> logger;
    private volatile bool initialized;
    private IReadOnlyList<int> orphanProcessIds = [];
    private DateTimeOffset lastPersistedAtUtc = DateTimeOffset.MinValue;

    /// <summary>状态恢复完成标记；为 false 时分配和实例控制入口必须拒绝服务。</summary>
    public bool IsInitialized => initialized;

    /// <summary>最近一次启动核对发现的疑似孤儿 PID，只用于监控和人工调查，不会自动误杀进程。</summary>
    public IReadOnlyList<int> OrphanProcessIds => orphanProcessIds;

    /// <summary>注入端口池、凭据、启动后端、通知器、状态存储和可测试时间源。</summary>
    public GameServerInstanceManager(
        InstanceCredentialService credentials,
        IGameServerProvider provider,
        IInstanceFailureNotifier failureNotifier,
        IAllocatorStateStore stateStore,
        IOptions<AllocatorOptions> options,
        TimeProvider timeProvider,
        ILogger<GameServerInstanceManager> logger)
    {
        this.credentials = credentials;
        this.provider = provider;
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
        if (document.SchemaVersion is not 1 and not 2)
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

                if (!string.Equals(instance.Provider, provider.Mode.ToString(), StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Active allocation {instance.AllocationId} belongs to provider {instance.Provider}; "
                        + $"the configured provider is {provider.Mode}.");
                }

                var recovered = await provider.RecoverAsync(persisted, cancellationToken);
                if (recovered is null)
                {
                    await MarkFailedAsync(
                        instance,
                        "Provider resource missing during allocator startup reconciliation",
                        cancellationToken);
                }
                else
                {
                    instance.Process = recovered.Process;
                    instance.ProcessStartedAtUtc = recovered.Process?.StartedAtUtc
                                                   ?? instance.ProcessStartedAtUtc;
                    instance.OrchestratorResourceName = recovered.OrchestratorResourceName;
                    instance.PortReleased = provider.Mode == AllocatorBackendMode.Agones;
                    if (instance.State == GameServerInstanceState.Draining)
                    {
                        await provider.DrainAsync(
                            recovered,
                            TimeSpan.FromSeconds(options.DrainGraceSeconds),
                            cancellationToken);
                        InstanceStateMachine.Transition(instance, GameServerInstanceState.Stopped);
                        instance.PortReleased = true;
                    }
                    else if (instance.State == GameServerInstanceState.Starting
                             && timeProvider.GetUtcNow() >= instance.RegistrationExpireAtUtc)
                    {
                        await MarkFailedAsync(
                            instance,
                            "Registration expired during allocator restart",
                            cancellationToken);
                    }
                    else
                    {
                        if (instance.State == GameServerInstanceState.Allocated)
                            instance.LastHeartbeatAtUtc = timeProvider.GetUtcNow();
                        logger.LogInformation(
                            "Recovered GameServer InstanceId={InstanceId} Provider={Provider} Port={Port} State={State}",
                            instance.ServerInstanceId,
                            instance.Provider,
                            instance.Port,
                            instance.State);
                    }
                }
            }
            ValidateRestoredUniqueness();
            var knownProcessIds = instances.Values
                .Select(instance => instance.Process?.ProcessId)
                .Where(processId => processId.HasValue)
                .Select(processId => processId!.Value)
                .ToHashSet();
            orphanProcessIds = await provider.FindOrphanedAsync(
                knownProcessIds,
                cancellationToken);
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
        ValidateAllocationRequest(request);
        var allocationId = NormalizeStableKey(request.AllocationId, requestId, "AllocationId");
        var idempotencyKey = NormalizeStableKey(request.IdempotencyKey, requestId, "IdempotencyKey");
        var fingerprint = ComputeRequestFingerprint(request);

        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = instances.Values.FirstOrDefault(instance =>
                string.Equals(instance.AllocationId, allocationId, StringComparison.Ordinal)
                || string.Equals(instance.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
                || (
                instance.RoomId == request.RoomId
                && instance.RoomEpoch == request.RoomEpoch));
            if (existing is not null)
            {
                if (!string.Equals(existing.AllocationId, allocationId, StringComparison.Ordinal)
                    || !string.Equals(existing.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(existing.RequestFingerprint),
                        Encoding.UTF8.GetBytes(fingerprint)))
                {
                    throw new AllocatorOperationException(
                        "AllocationId, IdempotencyKey, or room epoch was reused with different parameters.",
                        409);
                }
                return ToAllocationResponse(requestId, existing);
            }

            var greatestEpoch = instances.Values
                .Where(instance => instance.RoomId == request.RoomId)
                .Select(instance => instance.RoomEpoch)
                .DefaultIfEmpty(0)
                .Max();
            if (request.RoomEpoch < greatestEpoch)
                throw new AllocatorOperationException("A newer room epoch has already been allocated.", 409);

            var registration = credentials.Generate();
            var now = timeProvider.GetUtcNow();
            var serverInstanceId = Guid.NewGuid().ToString();
            var launchSpec = new GameServerLaunchSpec(
                request.RoomId,
                request.MatchId,
                serverInstanceId,
                0,
                options.LobbyInternalUrl,
                registration.Plaintext,
                options.JoinTicketSigningKey,
                request.BuildVersion,
                options.AdvertisedIp,
                MatchResultOutboxPaths.GetInstancePath(options, serverInstanceId),
                request.RoomEpoch,
                request.RoomEpoch,
                request.RuleSetVersion,
                request.ProtocolVersion,
                options.GameDataInternalUrl,
                options.SettlementSigningKey);
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
            GameServerProviderHandle handle;
            using var activity = MahjongTelemetry.ActivitySource.StartActivity(
                "Allocation.ProviderAllocate",
                ActivityKind.Internal);
            activity?.SetTag("mahjong.allocation.provider", provider.Mode.ToString());
            activity?.SetTag("mahjong.room_epoch", request.RoomEpoch);
            var providerStarted = Stopwatch.GetTimestamp();
            var providerOutcome = "success";
            try
            {
                handle = await provider.AllocateAsync(
                    new GameServerProviderRequest(
                        launchSpec,
                        allocationId,
                        request.GameType,
                        request.Region,
                        request.RuleSetVersion,
                        request.ProtocolVersion,
                        request.RequestedCapacity,
                        request.RoomEpoch),
                    startupTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                providerOutcome = "timeout";
                activity?.SetStatus(ActivityStatusCode.Error, "provider timeout");
                throw new AllocatorOperationException("GameServer provider allocation timed out.", 504);
            }
            catch
            {
                providerOutcome = "failure";
                activity?.SetStatus(ActivityStatusCode.Error, "provider failure");
                throw;
            }
            finally
            {
                MahjongTelemetry.RecordAllocationProvider(
                    provider.Mode.ToString(),
                    providerOutcome,
                    Stopwatch.GetElapsedTime(providerStarted).TotalMilliseconds);
            }
            var instance = new GameServerInstance
            {
                ServerInstanceId = serverInstanceId,
                RoomId = request.RoomId,
                MatchId = request.MatchId,
                AllocationId = allocationId,
                IdempotencyKey = idempotencyKey,
                RequestFingerprint = fingerprint,
                RoomEpoch = request.RoomEpoch,
                FencingToken = request.RoomEpoch,
                Provider = provider.Mode.ToString(),
                GameType = request.GameType,
                Region = request.Region,
                RuleSetVersion = request.RuleSetVersion,
                ProtocolVersion = request.ProtocolVersion,
                RequestedCapacity = request.RequestedCapacity,
                Port = handle.Port,
                AdvertisedIp = handle.AdvertisedIp,
                RegistrationCredentialHash = registration.Hash,
                RegistrationExpireAtUtc = now.AddSeconds(options.RegistrationTimeoutSeconds),
                StartedAtUtc = now,
                BuildVersion = request.BuildVersion,
                PortReleased = provider.Mode == AllocatorBackendMode.Agones,
                OrchestratorResourceName = handle.OrchestratorResourceName,
                Process = handle.Process,
                ProcessStartedAtUtc = handle.Process?.StartedAtUtc
            };
            instances[instance.ServerInstanceId] = instance;
            try
            {
                await PersistAsync(cancellationToken);
            }
            catch
            {
                instances.TryRemove(instance.ServerInstanceId, out _);
                try
                {
                    await provider.ReportUnhealthyAsync(
                        handle,
                        "Allocation state persistence failed",
                        CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    logger.LogCritical(
                        cleanupException,
                        "Provider compensation failed after allocation persistence failure InstanceId={InstanceId}",
                        instance.ServerInstanceId);
                }
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
            if (!IsCurrentFencingLease(instance))
                throw new AllocatorOperationException("A newer room allocation fences this Ready callback.", 409);
            if (timeProvider.GetUtcNow() >= instance.RegistrationExpireAtUtc)
                throw new AllocatorOperationException("GameServer registration credential expired.", 401);
            if (instance.RoomId != request.RoomId
                || instance.Port != request.ListenPort
                || instance.AdvertisedIp != request.ListenIp
                || instance.BuildVersion != request.BuildVersion
                || !AcceptsFencing(instance, request.RoomEpoch, request.FencingToken))
                throw new AllocatorOperationException("Registration does not match the allocation.", 400);
            if (!credentials.Verify(request.RegistrationCredential, instance.RegistrationCredentialHash))
                throw new AllocatorOperationException("GameServer registration credential is invalid.", 401);

            await provider.ReportReadyAsync(
                ToProviderHandle(instance),
                instance.FencingToken,
                cancellationToken);
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
                heartbeat.Plaintext,
                instance.RoomEpoch,
                instance.FencingToken);
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
            if (instance.State != GameServerInstanceState.Allocated
                || instance.RoomId != request.RoomId
                || !IsCurrentFencingLease(instance)
                || !AcceptsFencing(instance, request.RoomEpoch, request.FencingToken))
                throw new AllocatorOperationException("GameServer cannot heartbeat in its current state.", 409);
            if (instance.HeartbeatCredentialHash is null
                || !credentials.Verify(request.HeartbeatCredential, instance.HeartbeatCredentialHash))
                throw new AllocatorOperationException("GameServer heartbeat credential is invalid.", 401);
            await provider.RenewLeaseAsync(
                ToProviderHandle(instance),
                instance.FencingToken,
                cancellationToken);
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
            await provider.TerminateAsync(
                ToProviderHandle(instance),
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
        await provider.DrainAsync(
            ToProviderHandle(instance),
            TimeSpan.FromSeconds(options.DrainGraceSeconds),
            cleanupToken);
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

    /// <summary>按业务 allocation_id 查询稳定结果，供请求响应丢失后的安全恢复使用。</summary>
    public GameServerInstanceSnapshot? GetByAllocationId(string allocationId) =>
        instances.Values.FirstOrDefault(instance =>
            string.Equals(instance.AllocationId, allocationId, StringComparison.Ordinal))?.Snapshot();

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
                GameServerProviderStatus? providerStatus = null;
                if (instance.State is GameServerInstanceState.Starting
                    or GameServerInstanceState.Ready
                    or GameServerInstanceState.Allocated)
                {
                    providerStatus = await provider.GetStatusAsync(
                        ToProviderHandle(instance),
                        cancellationToken);
                }
                if (instance.State == GameServerInstanceState.Starting
                    && (providerStatus is { Exists: false } || now >= instance.RegistrationExpireAtUtc))
                {
                    reason = providerStatus is { Exists: false }
                        ? "Provider resource exited before registration"
                        : "Registration timed out";
                }
                else if (instance.State == GameServerInstanceState.Allocated
                    && (providerStatus is { Healthy: false }
                        || instance.LastHeartbeatAtUtc is null
                        || now - instance.LastHeartbeatAtUtc.Value
                            >= TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds)))
                {
                    reason = providerStatus is { Healthy: false }
                        ? "GameServer provider resource became unhealthy"
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
        await provider.ReportUnhealthyAsync(
            ToProviderHandle(instance),
            reason,
            cancellationToken);
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
        // 端口或 Agones 资源的实际回收由当前 Provider 完成；管理器只持久化回收标记防止重复副作用。
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
                instance.FailureReason ?? "GameServer failed",
                instance.RoomEpoch));
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
                instance.OrchestratorResourceName,
                instance.RoomEpoch,
                instance.AllocationId,
                instance.IdempotencyKey,
                instance.RequestFingerprint,
                instance.Provider,
                instance.GameType,
                instance.Region,
                instance.RuleSetVersion,
                instance.ProtocolVersion,
                instance.RequestedCapacity,
                instance.FencingToken))
            .ToArray();
        await stateStore.SaveAsync(
            // 仅追加可选字段，不提升根 SchemaVersion；阶段 4 镜像会忽略未知字段，可直接应用回滚。
            new AllocatorStateDocument(1, now, records), cancellationToken);
        lastPersistedAtUtc = now;
    }

    private static GameServerInstance Restore(PersistedGameServerInstance persisted) => new()
    {
        ServerInstanceId = persisted.ServerInstanceId,
        RoomId = persisted.RoomId,
        MatchId = persisted.MatchId,
        AllocationId = string.IsNullOrWhiteSpace(persisted.AllocationId)
            ? persisted.ServerInstanceId
            : persisted.AllocationId,
        IdempotencyKey = string.IsNullOrWhiteSpace(persisted.IdempotencyKey)
            ? persisted.ServerInstanceId
            : persisted.IdempotencyKey,
        RequestFingerprint = string.IsNullOrWhiteSpace(persisted.RequestFingerprint)
            ? ComputePersistedFingerprint(persisted)
            : persisted.RequestFingerprint,
        RoomEpoch = persisted.RoomEpoch,
        FencingToken = persisted.FencingToken > 0
            ? persisted.FencingToken
            : persisted.RoomEpoch,
        Provider = string.IsNullOrWhiteSpace(persisted.Provider)
            ? AllocatorBackendMode.LocalProcess.ToString()
            : persisted.Provider,
        GameType = persisted.GameType,
        Region = persisted.Region,
        RuleSetVersion = persisted.RuleSetVersion,
        ProtocolVersion = persisted.ProtocolVersion,
        RequestedCapacity = persisted.RequestedCapacity,
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

    /// <summary>把内部实例转换为 Provider 私有句柄；调用方不能获得凭据哈希或可变聚合引用。</summary>
    private static GameServerProviderHandle ToProviderHandle(GameServerInstance instance) => new(
        instance.Provider,
        instance.AdvertisedIp,
        instance.Port,
        instance.Process,
        instance.OrchestratorResourceName);

    /// <summary>
    /// 兼容窗口只允许 Epoch=1 的旧 DS 缺省 Fencing；发生重新分配后必须同时匹配 Epoch 和显式 Token。
    /// </summary>
    private bool AcceptsFencing(
        GameServerInstance instance,
        long suppliedRoomEpoch,
        long suppliedFencingToken)
    {
        var legacyInitial = options.AllowLegacyInitialFencingToken
                            && instance.RoomEpoch == 1
                            && suppliedRoomEpoch == 0
                            && suppliedFencingToken == 0;
        return legacyInitial
               || (suppliedRoomEpoch == instance.RoomEpoch
                   && suppliedFencingToken == instance.FencingToken);
    }

    /// <summary>当前租约必须等于房间已见最大 Epoch；终止后的新实例仍永久 fencing 旧实例。</summary>
    private bool IsCurrentFencingLease(GameServerInstance instance) =>
        instance.RoomEpoch == instances.Values
            .Where(candidate => candidate.RoomId == instance.RoomId)
            .Select(candidate => candidate.RoomEpoch)
            .DefaultIfEmpty(instance.RoomEpoch)
            .Max();

    /// <summary>
    /// 启动恢复必须重新验证 allocation_id、幂等键与 room+epoch 三组唯一约束；
    /// 状态文档若被并发旧版本或人工操作破坏，服务拒绝就绪而不是选择任意记录。
    /// </summary>
    private void ValidateRestoredUniqueness()
    {
        static string? Duplicate(IEnumerable<string> values) => values
            .GroupBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;

        var allocationDuplicate = Duplicate(instances.Values.Select(item => item.AllocationId));
        if (allocationDuplicate is not null)
            throw new InvalidDataException($"Duplicate allocation_id detected: {allocationDuplicate}");
        var idempotencyDuplicate = Duplicate(instances.Values.Select(item => item.IdempotencyKey));
        if (idempotencyDuplicate is not null)
            throw new InvalidDataException($"Duplicate idempotency key detected: {idempotencyDuplicate}");
        var roomEpochDuplicate = instances.Values
            .GroupBy(item => (item.RoomId, item.RoomEpoch))
            .FirstOrDefault(group => group.Count() > 1);
        if (roomEpochDuplicate is not null)
        {
            throw new InvalidDataException(
                $"Duplicate room epoch detected: {roomEpochDuplicate.Key.RoomId}/{roomEpochDuplicate.Key.RoomEpoch}");
        }
    }

    /// <summary>验证调度输入边界，阻止未经约束的标签、容量或版本进入进程参数和 Agones Selector。</summary>
    private static void ValidateAllocationRequest(AllocationRequest request)
    {
        static bool ValidText(string value, int maximum) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximum
            && value.All(character => char.IsLetterOrDigit(character)
                                      || character is '-' or '_' or '.');

        if (!ValidText(request.RoomId, 128) || !ValidText(request.MatchId, 128))
            throw new AllocatorOperationException("RoomId and MatchId are invalid.", 400);
        if (request.RoomEpoch < 1)
            throw new AllocatorOperationException("RoomEpoch must be positive.", 400);
        if (!ValidText(request.BuildVersion, 80)
            || !ValidText(request.GameType, 80)
            || !ValidText(request.Region, 80)
            || !ValidText(request.RuleSetVersion, 80)
            || !ValidText(request.ProtocolVersion, 40))
        {
            throw new AllocatorOperationException("Allocation routing metadata is invalid.", 400);
        }
        if (request.RequestedCapacity is < 1 or > 16)
            throw new AllocatorOperationException("RequestedCapacity must be between 1 and 16.", 400);
    }

    /// <summary>规范化调用方稳定键；空值回退到旧 X-Request-Id，确保升级期间重复调用仍可查询。</summary>
    private static string NormalizeStableKey(
        string? supplied,
        string fallback,
        string fieldName)
    {
        var value = string.IsNullOrWhiteSpace(supplied) ? fallback.Trim() : supplied.Trim();
        if (value.Length is < 8 or > 128
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new AllocatorOperationException($"{fieldName} is invalid.", 400);
        }
        return value;
    }

    /// <summary>计算不含凭据的稳定请求指纹，用于识别同一幂等键下的参数冲突。</summary>
    private static string ComputeRequestFingerprint(AllocationRequest request) =>
        ComputeFingerprint(
            request.RoomId,
            request.MatchId,
            request.RoomEpoch,
            request.GameType,
            request.Region,
            request.BuildVersion,
            request.RuleSetVersion,
            request.ProtocolVersion,
            request.RequestedCapacity);

    /// <summary>为旧状态文档补算指纹，升级不需要重启或丢弃仍存活实例。</summary>
    private static string ComputePersistedFingerprint(PersistedGameServerInstance instance) =>
        ComputeFingerprint(
            instance.RoomId,
            instance.MatchId,
            instance.RoomEpoch,
            instance.GameType,
            instance.Region,
            instance.BuildVersion,
            instance.RuleSetVersion,
            instance.ProtocolVersion,
            instance.RequestedCapacity);

    private static string ComputeFingerprint(
        string roomId,
        string matchId,
        long roomEpoch,
        string gameType,
        string region,
        string buildVersion,
        string ruleSetVersion,
        string protocolVersion,
        int requestedCapacity)
    {
        var payload = JsonSerializer.Serialize(new
        {
            roomId,
            matchId,
            roomEpoch,
            gameType,
            region,
            buildVersion,
            ruleSetVersion,
            protocolVersion,
            requestedCapacity
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static AllocationResponse ToAllocationResponse(string requestId, GameServerInstance instance) => new(
        requestId,
        instance.RoomId,
        instance.ServerInstanceId,
        instance.Port,
        instance.State,
        instance.RoomEpoch,
        instance.AllocationId,
        instance.FencingToken);
}
