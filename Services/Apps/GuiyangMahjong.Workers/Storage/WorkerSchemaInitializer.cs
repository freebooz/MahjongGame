using GuiyangMahjong.Schema;
using GuiyangMahjong.Workers.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GuiyangMahjong.Workers.Storage;

/// <summary>
/// 仅供本地开发和集成测试的 Worker Schema 初始化器。
/// 生产配置必须关闭，运行身份因此不需要 DDL；生产迁移由专用 Job 执行。
/// </summary>
public sealed class WorkerSchemaInitializer(
    NpgsqlDataSource dataSource,
    IOptions<WorkersOptions> options,
    ILogger<WorkerSchemaInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyDatabaseMigrations) return;
        var path = ServiceSchemaPath.Resolve(typeof(WorkerSchemaInitializer).Assembly);
        var sql = await File.ReadAllTextAsync(path, cancellationToken);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Workers 开发数据库迁移已执行");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
