using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 提供多 Lobby 副本共享的 Redis 在线状态实现。
/// 使用有序集合分数保存毫秒级最后观测时间，并在计数时回收过期成员。
/// </summary>
public sealed class RedisOnlinePresenceService : IOnlinePresenceService
{
    /// <summary>当前 Redis 逻辑数据库连接。</summary>
    private readonly IDatabase database;

    /// <summary>保存玩家最后观测时间的部署隔离有序集合键。</summary>
    private readonly RedisKey presenceKey;

    /// <summary>超过此时间未产生心跳的玩家按离线处理。</summary>
    private readonly TimeSpan timeout;

    /// <summary>提供可测试 UTC 时间的统一时钟。</summary>
    private readonly TimeProvider timeProvider;

    /// <summary>写入状态快照的当前 Lobby 实例标识。</summary>
    private readonly string lobbyId;

    /// <summary>使用集中连接、Lobby 配置和统一时钟构造在线状态服务。</summary>
    public RedisOnlinePresenceService(
        LobbyPersistenceConnections connections,
        IOptions<LobbyOptions> options,
        TimeProvider timeProvider)
    {
        database = connections.Redis.GetDatabase();
        presenceKey = $"{options.Value.Persistence.RedisKeyPrefix}:presence";
        timeout = TimeSpan.FromSeconds(options.Value.PresenceTimeoutSeconds);
        lobbyId = options.Value.LobbyId;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task TouchAsync(string playerId, CancellationToken cancellationToken)
    {
        var score = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await database.SortedSetAddAsync(presenceKey, playerId, score).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string playerId, CancellationToken cancellationToken) =>
        await database.SortedSetRemoveAsync(presenceKey, playerId).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<long> GetOnlineCountAsync(CancellationToken cancellationToken)
    {
        var cutoff = (timeProvider.GetUtcNow() - timeout).ToUnixTimeMilliseconds();
        await database.SortedSetRemoveRangeByScoreAsync(
            presenceKey, double.NegativeInfinity, cutoff).WaitAsync(cancellationToken);
        return await database.SortedSetLengthAsync(presenceKey).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PlayerPresenceSnapshot>> GetPlayersAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now - timeout;
        var distinct = playerIds.Distinct(StringComparer.Ordinal).ToArray();
        var scores = await Task.WhenAll(distinct.Select(playerId =>
            database.SortedSetScoreAsync(presenceKey, playerId).WaitAsync(cancellationToken)));
        return distinct.Select((playerId, index) =>
        {
            var observedAt = scores[index].HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)scores[index]!.Value)
                : (DateTimeOffset?)null;
            return new PlayerPresenceSnapshot(
                playerId,
                observedAt >= cutoff,
                observedAt,
                lobbyId);
        }).ToArray();
    }
}
