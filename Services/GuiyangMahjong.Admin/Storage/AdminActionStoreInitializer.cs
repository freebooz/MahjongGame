namespace GuiyangMahjong.Admin.Storage;

public sealed class AdminActionStoreInitializer(
    IAdminActionStore store,
    ILogger<AdminActionStoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        logger.LogInformation("Admin management store initialized.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
