using GuiyangMahjong.Auth.Storage;
using GuiyangMahjong.Auth.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Auth.Services;

/// <summary>
/// Auth 存储启动器。生产环境仅使用既有结构，不执行 DDL；数据库迁移由发布流水线负责。
/// </summary>
public sealed class AuthStoreInitializer(
    IAuthStore store,
    IOptions<AuthOptions> options,
    ILogger<AuthStoreInitializer> logger) : IHostedService
{
    /// <summary>显式允许时执行幂等结构初始化；关闭时跳过 DDL，支持最小权限运行身份。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.ApplyDatabaseMigrations)
        {
            await store.InitializeAsync(cancellationToken);
            return;
        }

        logger.LogInformation("Auth 数据库迁移已关闭，运行身份不会执行 DDL。");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
