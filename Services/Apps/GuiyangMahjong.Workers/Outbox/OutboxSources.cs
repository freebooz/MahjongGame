using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.BuildingBlocks.Persistence;
using GuiyangMahjong.Workers.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GuiyangMahjong.Workers.Outbox;

/// <summary>一个服务自有标准 Outbox 的运行句柄；只暴露集成表操作，不暴露业务表。</summary>
public sealed record OutboxSource(
    string Name,
    PostgresOutboxStore Store);

/// <summary>
/// 从受控配置建立多个 Outbox 数据源。每个连接应使用只能领取、标记和归档指定
/// integration.platform_outbox 的最小权限账号，禁止复用业务服务写账号。
/// </summary>
public sealed class OutboxSourceRegistry : IAsyncDisposable
{
    private readonly List<NpgsqlDataSource> dataSources = [];

    public OutboxSourceRegistry(IOptions<WorkersOptions> options)
    {
        var sources = new List<OutboxSource>();
        foreach (var configured in options.Value.OutboxSources)
        {
            var names = new PersistenceTableNames(configured.Schema);
            var dataSource = NpgsqlDataSource.Create(configured.ConnectionString);
            dataSources.Add(dataSource);
            sources.Add(new OutboxSource(
                configured.Name,
                new PostgresOutboxStore(dataSource, names)));
        }
        Sources = sources;
    }

    /// <summary>已通过启动配置验证的 Outbox 来源。</summary>
    public IReadOnlyCollection<OutboxSource> Sources { get; }

    /// <summary>释放所有来源连接池；不会终止来源服务正在执行的事务。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var dataSource in dataSources)
        {
            await dataSource.DisposeAsync();
        }
    }
}
