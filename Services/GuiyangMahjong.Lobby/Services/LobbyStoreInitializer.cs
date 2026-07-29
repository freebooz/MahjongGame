using GuiyangMahjong.Lobby.Storage;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>Lobby 存储启动器，负责隔离发布期 DDL 与房间运行流量。</summary>
public sealed class LobbyStoreInitializer(
    ILobbyStore store,
    IOptions<LobbyOptions> options,
    ILogger<LobbyStoreInitializer> logger) : IHostedService
{
    /// <summary>仅在显式启用迁移时初始化结构；禁用时使用发布阶段预置的结构。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Persistence.ApplyDatabaseMigrations)
        {
            await store.InitializeAsync(cancellationToken);
            logger.LogInformation("大厅存储初始化完成 Store={StoreType}", store.GetType().Name);
            return;
        }

        logger.LogInformation("Lobby 数据库迁移已关闭，运行身份不会执行 DDL。");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
