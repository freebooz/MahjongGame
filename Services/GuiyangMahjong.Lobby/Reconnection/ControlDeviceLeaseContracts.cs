namespace GuiyangMahjong.Lobby.Reconnection;

/// <summary>
/// 多端控制设备租约结果。
/// LeaseVersion 单调递增，使旧设备即使延迟恢复网络也不能覆盖新控制设备。
/// </summary>
public sealed record ControlDeviceLease(
    string PlayerId,
    string DeviceId,
    long LeaseVersion,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// 重连模块与 Sessions 模块之间的多端控制租约协调接口。
/// Lobby 只协调当前对局控制权，不持久化设备身份或长期登录历史。
/// </summary>
public interface IControlDeviceLeaseCoordinator
{
    /// <summary>
    /// 尝试取得玩家当前对局控制租约。
    /// ExpectedLeaseVersion 用于防止陈旧设备盲目抢占；冲突时返回当前租约而不修改状态。
    /// </summary>
    Task<ControlDeviceLease?> TryAcquireAsync(
        string playerId,
        string deviceId,
        long? expectedLeaseVersion,
        TimeSpan timeToLive,
        CancellationToken cancellationToken);

    /// <summary>仅持有相同 DeviceId 和 LeaseVersion 的调用方可以释放租约。</summary>
    Task<bool> ReleaseAsync(
        string playerId,
        string deviceId,
        long leaseVersion,
        CancellationToken cancellationToken);
}
