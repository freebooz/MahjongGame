namespace GuiyangMahjong.Admin.Storage;

using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

/// <summary>Admin 持久化启动器；生产运行身份不承担 DDL，避免管理权限扩大到结构所有权。</summary>
public sealed class AdminActionStoreInitializer(
    IAdminActionStore store,
    IAdminCaseStore caseStore,
    IPlayerAssetOperationStore assetOperationStore,
    IPlayerEvidenceStore evidenceStore,
    IOptions<AdminOptions> options,
    ILogger<AdminActionStoreInitializer> logger) : IHostedService
{
    /// <summary>显式启用时执行幂等结构迁移；禁用时直接使用发布流水线准备的结构。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Management.ApplyDatabaseMigrations)
        {
            logger.LogInformation("Admin 数据库迁移已关闭，运行身份不会执行 DDL。");
            return;
        }

        await store.InitializeAsync(cancellationToken);
        await caseStore.InitializeAsync(cancellationToken);
        await assetOperationStore.InitializeAsync(cancellationToken);
        await evidenceStore.InitializeAsync(cancellationToken);
        logger.LogInformation("Admin management store initialized.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
