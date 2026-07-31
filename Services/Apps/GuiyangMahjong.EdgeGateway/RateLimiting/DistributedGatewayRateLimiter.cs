using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.EdgeGateway.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GuiyangMahjong.EdgeGateway.RateLimiting;

/// <summary>分布式限流决定；RetryAfterSeconds 仅在拒绝时供响应头使用。</summary>
public sealed record DistributedRateLimitDecision(
    bool Acquired,
    int RetryAfterSeconds);

/// <summary>
/// EdgeGateway 分布式限流抽象。
/// 实现只能保存短期计数，不得持久化玩家资料或被业务正确性依赖。
/// </summary>
public interface IDistributedGatewayRateLimiter
{
    /// <summary>为一个请求主体原子获取窗口许可；取消时必须停止等待。</summary>
    Task<DistributedRateLimitDecision> TryAcquireAsync(
        string subject,
        CancellationToken cancellationToken);

    /// <summary>检查限流后端可用性；禁用时返回 true。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>Redis 未启用时的显式实现；仅供本地开发和隔离测试。</summary>
public sealed class DisabledDistributedGatewayRateLimiter
    : IDistributedGatewayRateLimiter
{
    /// <inheritdoc/>
    public Task<DistributedRateLimitDecision> TryAcquireAsync(
        string subject,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DistributedRateLimitDecision(true, 0));

    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

/// <summary>
/// 使用 Redis Lua 实现固定窗口计数。
/// 键只包含不可逆主体摘要和时间窗口，TTL 到期后自动删除，不形成权威玩家状态。
/// </summary>
public sealed class RedisDistributedGatewayRateLimiter(
    IOptions<EdgeGatewayOptions> options,
    ILogger<RedisDistributedGatewayRateLimiter> logger)
    : IDistributedGatewayRateLimiter, IAsyncDisposable
{
    private const string AcquireScript =
        """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then
          redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        return count
        """;

    private readonly DistributedRateLimitOptions options =
        options.Value.DistributedRateLimit;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private ConnectionMultiplexer? connection;

    /// <inheritdoc/>
    public async Task<DistributedRateLimitDecision> TryAcquireAsync(
        string subject,
        CancellationToken cancellationToken)
    {
        try
        {
            var redis = await GetConnectionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var window = now.ToUnixTimeSeconds() /
                         options.WindowSeconds;
            var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(subject)))[..24];
            var key =
                $"{options.KeyPrefix}:{digest}:{window}";
            var ttlMilliseconds =
                checked((options.WindowSeconds + 5) * 1000);
            var value = (long)await redis.GetDatabase()
                .ScriptEvaluateAsync(
                    AcquireScript,
                    [key],
                    [ttlMilliseconds])
                .WaitAsync(cancellationToken);
            var retryAfter = options.WindowSeconds
                             - (int)(now.ToUnixTimeSeconds()
                                     % options.WindowSeconds);
            return new DistributedRateLimitDecision(
                value <= options.PermitLimit,
                retryAfter);
        }
        catch (Exception exception)
            when (!options.FailClosed
                  && exception is not OperationCanceledException)
        {
            // 显式 fail-open 仅供隔离开发；不输出连接字符串、键或主体。
            logger.LogWarning(
                exception,
                "Redis 分布式限流不可用，当前环境按配置临时放行");
            return new DistributedRateLimitDecision(true, 0);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var redis = await GetConnectionAsync(cancellationToken);
            _ = await redis.GetDatabase()
                .PingAsync()
                .WaitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Redis 分布式限流健康检查失败");
            return false;
        }
    }

    private async Task<ConnectionMultiplexer> GetConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (connection is { IsConnected: true }) return connection;
        await connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (connection is { IsConnected: true }) return connection;
            connection?.Dispose();
            connection = await ConnectionMultiplexer.ConnectAsync(
                    options.ConnectionString)
                .WaitAsync(cancellationToken);
            return connection;
        }
        finally
        {
            connectionGate.Release();
        }
    }

    /// <summary>应用停止时关闭 Redis 多路复用连接，不删除任何键。</summary>
    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync();
        connectionGate.Dispose();
    }
}
