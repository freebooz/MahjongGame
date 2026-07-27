namespace GuiyangMahjong.Admin.Storage;

public sealed class AdminActionStoreInitializer(
    IAdminActionStore store,
    IAdminCaseStore caseStore,
    IPlayerAssetOperationStore assetOperationStore,
    IPlayerEvidenceStore evidenceStore,
    ILogger<AdminActionStoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        await caseStore.InitializeAsync(cancellationToken);
        await assetOperationStore.InitializeAsync(cancellationToken);
        await evidenceStore.InitializeAsync(cancellationToken);
        logger.LogInformation("Admin management store initialized.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
