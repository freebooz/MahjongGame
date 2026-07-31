namespace GuiyangMahjong.Auth.Infrastructure;

/// <summary>
/// IdentityApp 存储生命周期端口。生产环境的 Initialize 只验证已迁移结构，
/// DDL 必须由独立迁移身份在应用启动前执行。
/// </summary>
public interface IIdentityStorageLifecycle
{
    /// <summary>初始化本地测试结构或验证生产结构；失败时阻止服务进入就绪状态。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>执行无副作用的存储可用性检查。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
}
