using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 提供多 Lobby 副本共享的 Redis 访问撤销存储。
/// Lua 脚本以原子方式推进水位并刷新 TTL，避免并发写入造成回退。
/// </summary>
public sealed class RedisPlayerAccessRevocationStore : IPlayerAccessRevocationStore
{
    /// <summary>原子比较并写入较新撤销水位，同时刷新记录过期时间。</summary>
    private const string RevokeScript =
        """
        local current = redis.call('GET', KEYS[1])
        local incoming = tonumber(ARGV[1])
        if not current or tonumber(current) < incoming then
            redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
            return incoming
        end
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return tonumber(current)
        """;

    /// <summary>当前 Redis 逻辑数据库连接。</summary>
    private readonly IDatabase database;

    /// <summary>隔离部署环境并按玩家分片撤销记录的键前缀。</summary>
    private readonly string keyPrefix;

    /// <summary>撤销记录保留时长，单位为毫秒。</summary>
    private readonly long ttlMilliseconds;

    /// <summary>使用集中连接和 Lobby 配置构造 Redis 撤销存储。</summary>
    public RedisPlayerAccessRevocationStore(
        LobbyPersistenceConnections connections,
        IOptions<LobbyOptions> options)
    {
        database = connections.Redis.GetDatabase();
        keyPrefix = $"{options.Value.Persistence.RedisKeyPrefix}:access-revoked-before:";
        ttlMilliseconds = checked((long)TimeSpan
            .FromMinutes(options.Value.AccessRevocationTtlMinutes)
            .TotalMilliseconds);
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset> RevokeBeforeAsync(
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken)
    {
        var result = await database.ScriptEvaluateAsync(
            RevokeScript,
            [GetKey(playerId)],
            [effectiveAtUtc.ToUnixTimeMilliseconds(), ttlMilliseconds])
            .WaitAsync(cancellationToken);
        return DateTimeOffset.FromUnixTimeMilliseconds((long)result);
    }

    /// <inheritdoc />
    public async Task<bool> IsRevokedAsync(
        string playerId,
        DateTimeOffset issuedAtUtc,
        CancellationToken cancellationToken)
    {
        var value = await database.StringGetAsync(GetKey(playerId))
            .WaitAsync(cancellationToken);
        return value.HasValue
            && issuedAtUtc.ToUnixTimeMilliseconds() <= (long)value;
    }

    /// <summary>生成隔离到单个玩家的 Redis 键；调用方必须传入已验证的玩家标识。</summary>
    private RedisKey GetKey(string playerId) => $"{keyPrefix}{playerId}";
}
