using System.ComponentModel.DataAnnotations;

namespace GuiyangMahjong.Auth.Sessions;

/// <summary>
/// 登录会话并发策略。该策略只约束 IdentityApp 的 Refresh Token 会话，
/// 不代表玩家在大厅或 Dedicated Server 中的实时连接状态。
/// </summary>
public enum SessionPolicyMode
{
    /// <summary>同一玩家只保留最近创建的一个设备会话。</summary>
    SingleDevice,

    /// <summary>允许多个设备会话，但总数受到配置上限保护。</summary>
    MultiDevice
}

/// <summary>
/// 会话模块的启动配置。模式和上限由服务端环境配置决定，客户端不能覆盖；
/// 修改策略只影响后续登录，不会隐式修改房间或牌局状态。
/// </summary>
public sealed class SessionPolicyOptions
{
    public const string SectionName = "Sessions";

    /// <summary>会话并发模式，默认允许有限的多设备登录。</summary>
    [Required]
    public string Mode { get; init; } = nameof(SessionPolicyMode.MultiDevice);

    /// <summary>多设备模式允许的活跃 Refresh Token Family 数量，范围为 1 到 32。</summary>
    [Range(1, 32)]
    public int MaximumActiveSessions { get; init; } = 4;

    /// <summary>
    /// 将已校验的配置转换为不可变策略；若模式无效则失败关闭，避免启动后静默采用错误策略。
    /// </summary>
    public (SessionPolicyMode Mode, int MaximumActiveSessions) ToPolicy()
    {
        if (!Enum.TryParse<SessionPolicyMode>(Mode, ignoreCase: false, out var parsed))
            throw new InvalidOperationException($"Unsupported session policy mode '{Mode}'.");
        return (parsed, MaximumActiveSessions);
    }
}
