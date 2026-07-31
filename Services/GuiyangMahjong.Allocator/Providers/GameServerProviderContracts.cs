using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Services;

namespace GuiyangMahjong.Allocator.Providers;

/// <summary>
/// Provider 接收的不可变分配规格；它只描述运行环境需要的启动信息，不包含玩家、手牌或结算数据。
/// FencingToken 在同一房间的重新分配中必须单调递增，旧租约不得执行 Ready、续租或健康回报。
/// </summary>
public sealed record GameServerProviderRequest(
    GameServerLaunchSpec LaunchSpec,
    string AllocationId,
    string GameType,
    string Region,
    string RuleSetVersion,
    string ProtocolVersion,
    int RequestedCapacity,
    long FencingToken);

/// <summary>
/// Provider 返回的运行句柄；进程句柄仅存在于 LocalProcess 模式，编排资源名仅存在于 Agones 模式。
/// 该对象属于 Allocation Service，不得序列化到外部响应或交给 LobbyControl。
/// </summary>
public sealed record GameServerProviderHandle(
    string ProviderName,
    string AdvertisedIp,
    int Port,
    IManagedGameServerProcess? Process,
    string? OrchestratorResourceName);

/// <summary>Provider 可观察状态；Exists=false 表示底层进程或 Agones GameServer 已不存在。</summary>
public sealed record GameServerProviderStatus(
    bool Exists,
    bool Healthy,
    bool Ready,
    string State,
    int? ProcessId = null);

/// <summary>
/// 本地子进程和 Agones 的统一生命周期边界。
/// 所有方法必须支持取消；Provider 只管理运行资源，分配幂等、房间 Epoch 和业务审计由上层协调器负责。
/// </summary>
public interface IGameServerProvider
{
    /// <summary>当前显式配置的 Provider 模式；活动实例恢复时必须与持久化模式一致。</summary>
    AllocatorBackendMode Mode { get; }

    /// <summary>创建一个运行资源；失败时必须补偿已取得的端口或编排资源。</summary>
    Task<GameServerProviderHandle> AllocateAsync(
        GameServerProviderRequest request,
        CancellationToken cancellationToken);

    /// <summary>读取运行资源状态，不改变实例生命周期。</summary>
    Task<GameServerProviderStatus> GetStatusAsync(
        GameServerProviderHandle handle,
        CancellationToken cancellationToken);

    /// <summary>请求优雅排空并终止资源；重复调用必须安全。</summary>
    Task DrainAsync(
        GameServerProviderHandle handle,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken);

    /// <summary>立即终止异常资源；不得等待玩家或局状态。</summary>
    Task TerminateAsync(
        GameServerProviderHandle handle,
        CancellationToken cancellationToken);

    /// <summary>验证 Fencing 后续租运行租约；Provider 不得自行延长业务房间生命周期。</summary>
    Task RenewLeaseAsync(
        GameServerProviderHandle handle,
        long fencingToken,
        CancellationToken cancellationToken);

    /// <summary>验证运行资源已就绪；旧实例回调由上层 Fencing 校验拒绝。</summary>
    Task ReportReadyAsync(
        GameServerProviderHandle handle,
        long fencingToken,
        CancellationToken cancellationToken);

    /// <summary>报告资源不健康并确保其不能继续承载玩家。</summary>
    Task ReportUnhealthyAsync(
        GameServerProviderHandle handle,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>服务重启后核对并尝试重新接管持久化租约；无法安全接管时返回空。</summary>
    Task<GameServerProviderHandle?> RecoverAsync(
        PersistedGameServerInstance instance,
        CancellationToken cancellationToken);

    /// <summary>返回疑似由本 Allocator 启动但未出现在已知 PID 集合中的孤儿进程。</summary>
    Task<IReadOnlyList<int>> FindOrphanedAsync(
        IReadOnlySet<int> knownProcessIds,
        CancellationToken cancellationToken);

    /// <summary>检查 Provider 依赖、身份和容量是否满足接收新分配的条件。</summary>
    Task<bool> CheckReadyAsync(CancellationToken cancellationToken);
}
