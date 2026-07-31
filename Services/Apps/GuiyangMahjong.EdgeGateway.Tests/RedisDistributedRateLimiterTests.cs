using GuiyangMahjong.EdgeGateway.Options;
using GuiyangMahjong.EdgeGateway.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GuiyangMahjong.EdgeGateway.Tests;

/// <summary>
/// 只有提供隔离 Redis 连接时才执行的测试标记。
/// 默认构建不会访问开发者本机或共享环境。
/// </summary>
public sealed class EdgeRedisFactAttribute : FactAttribute
{
    /// <summary>缺少显式连接字符串时把测试标为跳过，而不是隐式连接 localhost。</summary>
    public EdgeRedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "EDGE_GATEWAY_TEST_REDIS")))
        {
            Skip =
                "Set EDGE_GATEWAY_TEST_REDIS to run the isolated Redis limiter test.";
        }
    }
}

/// <summary>验证 Redis Lua 固定窗口、健康检查和短期独立键前缀。</summary>
public sealed class RedisDistributedRateLimiterTests
{
    /// <summary>同一窗口的第二次请求必须被拒绝，不同测试使用唯一前缀避免互相污染。</summary>
    [EdgeRedisFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task RedisLimiter_AtomicallyRejectsRequestOverLimit()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                "EDGE_GATEWAY_TEST_REDIS")
            ?? throw new InvalidOperationException(
                "EDGE_GATEWAY_TEST_REDIS is required.");
        var options =
            Microsoft.Extensions.Options.Options.Create(
                new EdgeGatewayOptions
        {
            PlayerTokens = new PlayerTokenOptions
            {
                LegacySigningKey =
                    "test-only-edge-signing-key-long-enough-for-validation"
            },
            DistributedRateLimit =
                new DistributedRateLimitOptions
                {
                    Enabled = true,
                    ConnectionString = connectionString,
                    KeyPrefix =
                        $"guiyang:edge:test:{Guid.NewGuid():N}",
                    PermitLimit = 1,
                    WindowSeconds = 2,
                    FailClosed = true
                }
                });
        await using var limiter =
            new RedisDistributedGatewayRateLimiter(
                options,
                NullLogger<
                    RedisDistributedGatewayRateLimiter>.Instance);

        Assert.True(await limiter.CheckHealthAsync(
            CancellationToken.None));
        var first = await limiter.TryAcquireAsync(
            "player-redis-contract",
            CancellationToken.None);
        var second = await limiter.TryAcquireAsync(
            "player-redis-contract",
            CancellationToken.None);

        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
        Assert.InRange(second.RetryAfterSeconds, 1, 2);
    }
}
