// Lobby 持久化连接容器：集中拥有 PostgreSQL 数据源与 Redis 连接的创建和释放生命周期。
// 生产启动必须验证连接配置和最小权限身份，不允许连接失败时自动降级为内存存储。
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Storage;

/// <summary>
/// Lobby 外部持久化连接的延迟创建容器。
/// Redis 与 PostgreSQL 连接仅在首次访问时线程安全创建；该实例拥有二者生命周期，
/// 生产连接串必须使用最小权限身份且不能出现在日志中。
/// </summary>
public sealed class LobbyPersistenceConnections : IAsyncDisposable
{
    // Lazy 避免未启用路径提前建立连接，并保证并发首次访问只创建一个共享客户端。
    private readonly Lazy<IConnectionMultiplexer> redis;
    private readonly Lazy<NpgsqlDataSource> postgres;

    /// <summary>捕获已验证的持久化配置；实际网络连接延迟到对应属性首次访问。</summary>
    public LobbyPersistenceConnections(IOptions<LobbyOptions> options)
    {
        var persistence = options.Value.Persistence;
        redis = new Lazy<IConnectionMultiplexer>(
            () => ConnectionMultiplexer.Connect(persistence.RedisConnectionString),
            LazyThreadSafetyMode.ExecutionAndPublication);
        postgres = new Lazy<NpgsqlDataSource>(
            () => NpgsqlDataSource.Create(persistence.PostgresConnectionString),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>获取进程共享的 Redis 多路复用连接；调用方不得自行释放。</summary>
    public IConnectionMultiplexer Redis => redis.Value;

    /// <summary>获取 Lobby 专用 PostgreSQL 数据源/连接池；调用方按命令释放连接而非数据源。</summary>
    public NpgsqlDataSource Postgres => postgres.Value;

    /// <summary>只释放实际创建过的客户端，先关闭 PostgreSQL 再优雅关闭 Redis。</summary>
    public async ValueTask DisposeAsync()
    {
        if (postgres.IsValueCreated) await postgres.Value.DisposeAsync();
        if (redis.IsValueCreated)
        {
            await redis.Value.CloseAsync();
            redis.Value.Dispose();
        }
    }
}
