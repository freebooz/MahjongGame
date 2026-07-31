namespace GuiyangMahjong.Auth.Auth;

/// <summary>
/// Auth 模块边界标记。该模块只负责身份验证、账号绑定和 Token 签发，
/// 不拥有玩家长期档案、房间状态、对局结果、资产或 Dedicated Server 生命周期。
/// </summary>
public static class AuthModuleBoundary
{
    /// <summary>模块稳定名称，用于架构测试、Trace 标签和后续渐进式迁移。</summary>
    public const string Name = "Auth";
}
