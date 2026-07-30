// 游戏服进程契约：抽象启动、停止和进程观察能力，隔离 Windows、Linux 与 Agones 实现。
// 启动结果必须包含可追踪实例身份；停止操作需要幂等并对超时、进程已退出等情况给出明确语义。
namespace GuiyangMahjong.Allocator.Domain;

/// <summary>
/// 启动 Dedicated Server 所需的内部规格。
/// 端口为主机监听端口；注册/加入密钥只在受控进程环境中传递，禁止写入日志或监控快照；
/// MatchResultOutboxPath 必须位于配置允许的持久化根目录。
/// </summary>
public sealed record GameServerLaunchSpec(
    string RoomId,
    string MatchId,
    string ServerInstanceId,
    int Port,
    string LobbyInternalUrl,
    string RegistrationCredential,
    string JoinTicketSigningKey,
    string BuildVersion,
    string AdvertisedIp,
    string MatchResultOutboxPath);

/// <summary>
/// 可由 Allocator 管理的游戏服进程抽象。
/// 实现负责准确报告原始启动时间以避免 PID 复用误杀，并在宽限期内执行幂等停止。
/// </summary>
public interface IManagedGameServerProcess
{
    /// <summary>操作系统进程号，仅在当前节点内有意义，可能被系统复用。</summary>
    int ProcessId { get; }

    /// <summary>进程观测到的 UTC 启动时间，与 PID 共同验证进程身份。</summary>
    DateTimeOffset StartedAtUtc { get; }

    /// <summary>进程是否已退出；读取不得改变进程生命周期。</summary>
    bool HasExited { get; }

    /// <summary>
    /// 请求进程在宽限期内退出，超时后实现可升级为强制终止。
    /// 重复调用必须安全，取消只中止等待而不能遗留未受管进程。
    /// </summary>
    ValueTask StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken);
}

/// <summary>隔离本机和容器化进程启动/恢复策略的工厂接口。</summary>
public interface IGameServerProcessLauncher
{
    /// <summary>
    /// 按受校验规格启动新实例；失败时不得泄漏已分配端口、凭据或孤儿进程。
    /// </summary>
    Task<IManagedGameServerProcess> LaunchAsync(
        GameServerLaunchSpec spec,
        CancellationToken cancellationToken);

    /// <summary>
    /// 服务重启后尝试重新接管既有进程。
    /// PID 与预期启动时间任一不匹配都必须返回空，防止控制无关进程。
    /// </summary>
    Task<IManagedGameServerProcess?> TryAttachAsync(
        int processId,
        DateTimeOffset expectedStartedAtUtc,
        CancellationToken cancellationToken);
}
