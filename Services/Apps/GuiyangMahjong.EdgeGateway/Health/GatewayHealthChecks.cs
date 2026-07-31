using System.Text.Json;
using GuiyangMahjong.EdgeGateway.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GuiyangMahjong.EdgeGateway.Health;

/// <summary>
/// 记录应用是否完成启动。
/// Startup 探针只反映管线已建立，不替代 Ready 对上游和 Redis 的检查。
/// </summary>
public sealed class GatewayStartupState
{
    private int started;

    /// <summary>应用生命周期触发 Started 后将状态永久置为已启动。</summary>
    public void MarkStarted() => Interlocked.Exchange(ref started, 1);

    /// <summary>当前进程是否已经完成 ASP.NET Core 启动。</summary>
    public bool IsStarted => Volatile.Read(ref started) == 1;
}

/// <summary>将 GatewayStartupState 暴露给 ASP.NET Core 健康检查框架。</summary>
public sealed class GatewayStartupHealthCheck(
    GatewayStartupState state)
    : IHealthCheck
{
    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            state.IsStarted
                ? HealthCheckResult.Healthy("gateway-started")
                : HealthCheckResult.Unhealthy("gateway-starting"));
}

/// <summary>
/// 读取 YARP Cluster Destination 地址并探测各业务服务 `/health/ready`。
/// 健康响应只包含集群 ID，不暴露内部地址或异常详情。
/// </summary>
public sealed class GatewayUpstreamHealthCheck(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<GatewayUpstreamHealthCheck> logger)
    : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var clusters = configuration
            .GetSection("ReverseProxy:Clusters")
            .GetChildren()
            .ToArray();
        var failed = new List<string>();
        var client = httpClientFactory.CreateClient(
            nameof(GatewayUpstreamHealthCheck));
        foreach (var cluster in clusters)
        {
            var address = cluster
                .GetSection("Destinations")
                .GetChildren()
                .Select(destination =>
                    destination["Address"])
                .FirstOrDefault(value =>
                    !string.IsNullOrWhiteSpace(value));
            if (!Uri.TryCreate(
                    address,
                    UriKind.Absolute,
                    out var baseUri))
            {
                failed.Add(cluster.Key);
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(baseUri, "/health/ready"));
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (!response.IsSuccessStatusCode)
                    failed.Add(cluster.Key);
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                failed.Add(cluster.Key);
                logger.LogWarning(
                    exception,
                    "上游健康检查失败 ClusterId={ClusterId}",
                    cluster.Key);
            }
        }

        return failed.Count == 0
            ? HealthCheckResult.Healthy("all-upstreams-ready")
            : HealthCheckResult.Unhealthy(
                $"unavailable-clusters:{string.Join(',', failed)}");
    }
}

/// <summary>把 Redis 分布式限流可用性纳入 Ready 探针。</summary>
public sealed class GatewayRateLimitHealthCheck(
    IDistributedGatewayRateLimiter limiter)
    : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await limiter.CheckHealthAsync(cancellationToken)
            ? HealthCheckResult.Healthy("rate-limiter-ready")
            : HealthCheckResult.Unhealthy("rate-limiter-unavailable");
}

/// <summary>健康端点 JSON 响应器；不输出异常、内部地址或配置值。</summary>
public static class GatewayHealthResponseWriter
{
    /// <summary>写入总体状态及各检查项的低敏感结果。</summary>
    public static Task WriteAsync(
        HttpContext context,
        HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                checks = report.Entries.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.Status
                        .ToString()
                        .ToLowerInvariant())
            }),
            context.RequestAborted);
    }
}
