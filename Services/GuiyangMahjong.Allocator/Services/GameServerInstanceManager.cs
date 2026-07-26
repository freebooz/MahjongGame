using System.Collections.Concurrent;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Security;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Services;

public sealed class GameServerInstanceManager
{
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

    public bool IsInitialized => initialized;

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

    public async Task<GameServerInstanceSnapshot> DrainAsync(
        string serverInstanceId,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Once a drain owns the instance lock it must finish even if the HTTP caller
            // times out. Otherwise a successfully terminated process can remain persisted
            // as Draining forever and prevent deterministic port reuse.
            var cleanupToken = CancellationToken.None;
            if (!instances.TryGetValue(serverInstanceId, out var instance))
                throw new AllocatorOperationException("GameServer instance was not found.", 404);
            if (instance.State == GameServerInstanceState.Stopped) return instance.Snapshot();
            if (instance.State == GameServerInstanceState.Failed)
            {
                InstanceStateMachine.Transition(instance, GameServerInstanceState.Stopped);
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
                    "Only allocated or already draining instances can drain.", 409);
            }
            if (instance.Process is not null)
            {
                await instance.Process.StopAsync(
                    TimeSpan.FromSeconds(options.DrainGraceSeconds), cleanupToken);
            }
            else if (options.Backend == AllocatorBackendMode.Agones
                     && !string.IsNullOrWhiteSpace(instance.OrchestratorResourceName))
            {
                await agones.ShutdownAsync(instance.OrchestratorResourceName, cleanupToken);
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
        finally
        {
            gate.Release();
        }
    }

    public GameServerInstanceSnapshot? Get(string serverInstanceId) =>
        instances.TryGetValue(serverInstanceId, out var instance) ? instance.Snapshot() : null;

    public IReadOnlyList<GameServerInstanceSnapshot> List() =>
        instances.Values.Select(instance => instance.Snapshot()).OrderBy(x => x.Port).ToArray();

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
