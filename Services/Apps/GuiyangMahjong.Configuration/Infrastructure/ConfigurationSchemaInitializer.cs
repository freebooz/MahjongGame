using GuiyangMahjong.Configuration.Options;
using GuiyangMahjong.Schema;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GuiyangMahjong.Configuration.Infrastructure;

/// <summary>
/// 仅供本地开发使用的 Configuration Schema 初始化器。生产环境必须关闭运行时 DDL，
/// 并由独立迁移 Job 以 migration 身份执行同一份、带摘要校验的 Schema 文件。
/// </summary>
public sealed class ConfigurationSchemaInitializer(
    IServiceProvider services,
    IOptions<ConfigurationOptions> options,
    ILogger<ConfigurationSchemaInitializer> logger) : IHostedService
{
    /// <summary>应用启动时按显式开关执行幂等 Schema；缺少 PostgreSQL 数据源时失败关闭。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ApplyDatabaseMigrations) return;
        var dataSource = services.GetService<NpgsqlDataSource>()
            ?? throw new InvalidOperationException("Postgres 模式才允许执行 Configuration Schema 初始化。");
        var path = ServiceSchemaPath.Resolve(typeof(ConfigurationSchemaInitializer).Assembly);
        var sql = await File.ReadAllTextAsync(path, cancellationToken);
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Configuration 本地开发 Schema 已完成幂等初始化。SchemaPath={SchemaPath}", path);
    }

    /// <summary>初始化器不持有后台资源，停止阶段无需额外操作。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
