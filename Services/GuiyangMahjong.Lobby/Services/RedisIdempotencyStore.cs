using System.Text.Json;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 提供多 Lobby 副本共享的 Redis 幂等存储。
/// 分布式锁只保护首次执行窗口，成功结果按配置的 TTL 独立保存。
/// </summary>
public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    /// <summary>仅允许锁所有者释放锁，防止过期锁被后继请求误删。</summary>
    private const string ReleaseLockScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    /// <summary>与 ASP.NET Web 默认 JSON 约定一致的序列化配置。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>当前 Redis 逻辑数据库连接。</summary>
    private readonly IDatabase database;

    /// <summary>隔离当前部署环境键空间的前缀。</summary>
    private readonly string prefix;

    /// <summary>成功响应的保留时长。</summary>
    private readonly TimeSpan resultTtl;

    /// <summary>首次执行分布式锁的最大持有时长。</summary>
    private readonly TimeSpan lockTtl;

    /// <summary>
    /// 使用集中连接和 Lobby 配置构造 Redis 幂等存储。
    /// </summary>
    public RedisIdempotencyStore(
        LobbyPersistenceConnections connections,
        IOptions<LobbyOptions> options)
    {
        database = connections.Redis.GetDatabase();
        prefix = options.Value.Persistence.RedisKeyPrefix;
        resultTtl = TimeSpan.FromSeconds(options.Value.IdempotencyTtlSeconds);
        lockTtl = TimeSpan.FromSeconds(options.Value.IdempotencyLockSeconds);
    }

    /// <inheritdoc />
    public async Task<IdempotentHttpResponse> ExecuteAsync(
        string key,
        Func<Task<IdempotentHttpResponse>> operation,
        CancellationToken cancellationToken)
    {
        var resultKey = $"{prefix}:idempotency:result:{key}";
        var lockKey = $"{prefix}:idempotency:lock:{key}";
        while (true)
        {
            var cached = await database.StringGetAsync(resultKey).WaitAsync(cancellationToken);
            if (cached.HasValue)
                return Deserialize(cached!);

            var owner = Guid.NewGuid().ToString("N");
            if (!await database.StringSetAsync(
                    lockKey, owner, lockTtl, When.NotExists).WaitAsync(cancellationToken))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
                continue;
            }

            try
            {
                // 获得锁后再次读取，覆盖前一个所有者在竞争窗口内刚刚写入结果的情况。
                cached = await database.StringGetAsync(resultKey).WaitAsync(cancellationToken);
                if (cached.HasValue) return Deserialize(cached!);
                var response = await operation();
                var payload = JsonSerializer.Serialize(response, JsonOptions);
                await database.StringSetAsync(resultKey, payload, resultTtl).WaitAsync(cancellationToken);
                return response;
            }
            finally
            {
                await database.ScriptEvaluateAsync(
                    ReleaseLockScript,
                    [new RedisKey(lockKey)],
                    [new RedisValue(owner)]);
            }
        }
    }

    /// <summary>
    /// 将 Redis 中的受信响应载荷恢复为领域响应；损坏数据会显式失败而不是伪造默认响应。
    /// </summary>
    private static IdempotentHttpResponse Deserialize(RedisValue value) =>
        JsonSerializer.Deserialize<IdempotentHttpResponse>((string)value!, JsonOptions)
        ?? throw new InvalidDataException("Redis idempotency response is invalid.");
}
