using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Services;

namespace GuiyangMahjong.Allocator.Providers;

/// <summary>
/// Agones Provider：按游戏、地域、Build、RuleSet 与协议选择 Fleet 容量，并统一处理状态、排空和关闭。
/// Kubernetes 身份只存在于本 Provider，Lobby、Admin 与 Dedicated Server 均不能调用 Agones API。
/// </summary>
public sealed class AgonesGameServerProvider(IAgonesAllocationClient agones) : IGameServerProvider
{
    /// <inheritdoc/>
    public AllocatorBackendMode Mode => AllocatorBackendMode.Agones;

    /// <inheritdoc/>
    public async Task<GameServerProviderHandle> AllocateAsync(
        GameServerProviderRequest request,
        CancellationToken cancellationToken)
    {
        var spec = request.LaunchSpec;
        var allocation = await agones.AllocateAsync(new AgonesAllocationSpec(
            spec.RoomId,
            spec.MatchId,
            spec.ServerInstanceId,
            spec.RegistrationCredential,
            spec.LobbyInternalUrl,
            spec.BuildVersion,
            spec.RoomEpoch,
            request.GameType,
            request.Region,
            request.RuleSetVersion,
            request.ProtocolVersion,
            request.RequestedCapacity,
            request.FencingToken,
            spec.GameDataInternalUrl), cancellationToken);
        return new GameServerProviderHandle(
            Mode.ToString(),
            allocation.Address,
            allocation.Port,
            null,
            allocation.GameServerName);
    }

    /// <inheritdoc/>
    public async Task<GameServerProviderStatus> GetStatusAsync(
        GameServerProviderHandle handle,
        CancellationToken cancellationToken)
    {
        var state = string.IsNullOrWhiteSpace(handle.OrchestratorResourceName)
            ? null
            : await agones.GetGameServerStateAsync(
                handle.OrchestratorResourceName,
                cancellationToken);
        var exists = state is not null;
        var healthy = state is "Ready" or "Allocated" or "Reserved";
        return new GameServerProviderStatus(
            exists,
            healthy,
            state is "Ready" or "Allocated",
            state ?? "Missing");
    }

    /// <inheritdoc/>
    public Task DrainAsync(
        GameServerProviderHandle handle,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken) =>
        ShutdownAsync(handle, cancellationToken);

    /// <inheritdoc/>
    public Task TerminateAsync(
        GameServerProviderHandle handle,
        CancellationToken cancellationToken) =>
        ShutdownAsync(handle, cancellationToken);

    /// <inheritdoc/>
    public async Task RenewLeaseAsync(
        GameServerProviderHandle handle,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(handle, cancellationToken);
        if (!status.Healthy)
            throw new AllocatorOperationException("Agones GameServer is not healthy.", 409);
    }

    /// <inheritdoc/>
    public async Task ReportReadyAsync(
        GameServerProviderHandle handle,
        long fencingToken,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(handle, cancellationToken);
        if (!status.Ready)
            throw new AllocatorOperationException("Agones GameServer is not Ready or Allocated.", 409);
    }

    /// <inheritdoc/>
    public Task ReportUnhealthyAsync(
        GameServerProviderHandle handle,
        string reason,
        CancellationToken cancellationToken) =>
        ShutdownAsync(handle, cancellationToken);

    /// <inheritdoc/>
    public async Task<GameServerProviderHandle?> RecoverAsync(
        PersistedGameServerInstance instance,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.OrchestratorResourceName)) return null;
        var handle = new GameServerProviderHandle(
            Mode.ToString(),
            instance.AdvertisedIp,
            instance.Port,
            null,
            instance.OrchestratorResourceName);
        var status = await GetStatusAsync(handle, cancellationToken);
        return status.Exists && status.Healthy ? handle : null;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<int>> FindOrphanedAsync(
        IReadOnlySet<int> knownProcessIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<int>>([]);

    /// <inheritdoc/>
    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken) =>
        agones.CheckReadyAsync(cancellationToken);

    private Task ShutdownAsync(
        GameServerProviderHandle handle,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(handle.OrchestratorResourceName)
            ? Task.CompletedTask
            : agones.ShutdownAsync(handle.OrchestratorResourceName, cancellationToken);
}
