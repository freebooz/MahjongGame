namespace GuiyangMahjong.Auth.Infrastructure;

/// <summary>
/// IdentityApp 基础设施模块边界。承载 PostgreSQL、密钥配置和外部集成适配，
/// 不能反向决定认证、会话、玩家或管理模块的业务策略。
/// </summary>
public static class InfrastructureModuleBoundary
{
    /// <summary>模块稳定名称，用于启动诊断和架构测试。</summary>
    public const string Name = "Infrastructure";
}
