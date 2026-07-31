using GuiyangMahjong.GameData.Options;
using GuiyangMahjong.Schema;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GuiyangMahjong.GameData.Infrastructure;

/// <summary>
/// GameData 本地开发迁移入口。生产配置强制关闭，运行账号因此只需要 DML 权限；
/// Schema 文件由中央构建规则复制到 GameData 独立输出路径。
/// </summary>
public sealed class GameDataSchemaInitializer(
    IServiceProvider serviceProvider,
    IOptions<GameDataOptions> options,
    ILogger<GameDataSchemaInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyDatabaseMigrations) return;
        var dataSource = serviceProvider.GetService<NpgsqlDataSource>();
        if (dataSource is null)
            throw new InvalidOperationException("只有 Postgres 模式可以执行 GameData 迁移。");
        var path = ServiceSchemaPath.Resolve(typeof(GameDataSchemaInitializer).Assembly);
        var sql = await File.ReadAllTextAsync(path, cancellationToken);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("GameData 开发数据库迁移已执行。");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
