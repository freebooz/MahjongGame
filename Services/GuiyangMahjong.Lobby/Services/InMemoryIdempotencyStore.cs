using System.Collections.Concurrent;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 提供单进程开发与测试环境使用的幂等存储。
/// 数据生命周期限定为当前 Lobby 进程，不适用于多副本生产部署。
/// </summary>
public sealed class InMemoryIdempotencyStore(
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider) : IIdempotencyStore
{
    /// <summary>保存进程内共享任务，保证并发请求复用同一业务操作。</summary>
    private readonly ConcurrentDictionary<string, Entry> operations = new(StringComparer.Ordinal);

    /// <summary>成功结果允许被重放的最长时间。</summary>
    private readonly TimeSpan ttl = TimeSpan.FromSeconds(options.Value.IdempotencyTtlSeconds);

    /// <inheritdoc />
    public async Task<IdempotentHttpResponse> ExecuteAsync(
        string key,
        Func<Task<IdempotentHttpResponse>> operation,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (operations.TryGetValue(key, out var expired) && now - expired.CreatedAtUtc >= ttl)
            operations.TryRemove(new KeyValuePair<string, Entry>(key, expired));

        // Lazy 以线程安全模式保存真实任务，避免并发命中同一键时重复执行有副作用的业务逻辑。
        var entry = operations.GetOrAdd(
            key,
            _ => new Entry(
                now,
                new Lazy<Task<IdempotentHttpResponse>>(
                    operation, LazyThreadSafetyMode.ExecutionAndPublication)));
        try
        {
            return await entry.Operation.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            // 失败任务必须移除，否则瞬时故障会被永久重放并阻断合法重试。
            operations.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            throw;
        }
    }

    /// <summary>保存操作创建时间和唯一共享任务的进程内条目。</summary>
    private sealed record Entry(
        DateTimeOffset CreatedAtUtc,
        Lazy<Task<IdempotentHttpResponse>> Operation);
}
