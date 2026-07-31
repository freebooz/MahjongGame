using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Devices;

/// <summary>
/// Devices 模块的登录信号写入端口。实现可以维护设备摘要和切换历史，
/// 但设备信号不能改变已经完成的身份认证结果。
/// </summary>
public interface IDeviceAuditWriter
{
    /// <summary>追加脱敏登录事实；事件标识必须幂等，敏感凭证不得进入事件。</summary>
    Task RecordLoginAsync(
        AuthLoginEvent loginEvent,
        CancellationToken cancellationToken);
}
