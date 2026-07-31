using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using GuiyangMahjong.EdgeGateway.Options;
using GuiyangMahjong.EdgeGateway.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;

namespace GuiyangMahjong.EdgeGateway.Middleware;

/// <summary>统一网关错误响应；只表达接入层失败，不改写下游正常返回的业务错误。</summary>
public sealed record GatewayError(
    string Code,
    string Message,
    string RequestId,
    string CorrelationId,
    string TraceId);

/// <summary>网关错误序列化入口，保证各中间件使用同一响应结构。</summary>
public static class GatewayErrorWriter
{
    /// <summary>
    /// 在响应尚未开始时写入稳定 JSON 错误。
    /// 调用方负责选择 HTTP 状态码，错误正文不得包含异常、凭据或上游地址。
    /// </summary>
    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        // Retry-After 在限流器调用统一写入器前已经计算；Clear 后必须显式恢复该标准头。
        var retryAfter =
            context.Response.Headers.RetryAfter.ToString();
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        if (!string.IsNullOrWhiteSpace(retryAfter))
            context.Response.Headers.RetryAfter = retryAfter;
        await context.Response.WriteAsJsonAsync(
            new GatewayError(
                code,
                message,
                GatewayRequestContextMiddleware.GetRequestId(context),
                GatewayRequestContextMiddleware.GetCorrelationId(context),
                Activity.Current?.TraceId.ToString()
                    ?? context.TraceIdentifier),
            cancellationToken: context.RequestAborted);
    }
}

/// <summary>
/// 删除客户端伪造的身份、内部路由、方法覆盖和转发头。
/// 可信代理的 Forwarded Headers 会保留给框架消费，其他来源一律删除。
/// </summary>
public sealed class GatewayHeaderSanitizationMiddleware(
    RequestDelegate next,
    IOptions<EdgeGatewayOptions> options)
{
    private static readonly string[] AlwaysRemovedHeaders =
    [
        "X-Player-Id",
        "X-User-Id",
        "X-Account-Id",
        "X-Session-Id",
        "X-Role",
        "X-Permissions",
        "X-HTTP-Method-Override",
        "X-Method-Override",
        "X-Original-URL",
        "X-Edge-Route"
    ];

    private static readonly string[] ForwardedHeaders =
    [
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "X-Forwarded-Prefix",
        "X-Original-For",
        "X-Original-Host",
        "X-Original-Proto"
    ];

    private readonly RequestDelegate next = next;
    private readonly IPAddress[] trustedProxies = options.Value.TrustedProxies
        .Select(IPAddress.Parse)
        .ToArray();
    private readonly IPNetwork[] trustedNetworks =
        options.Value.TrustedProxyNetworks
            .Select(IPNetwork.Parse)
            .ToArray();

    /// <summary>清洗请求头后继续；仅依据连接层 RemoteIpAddress 判断代理可信度。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        foreach (var header in AlwaysRemovedHeaders)
            context.Request.Headers.Remove(header);
        foreach (var header in context.Request.Headers.Keys
                     .Where(header =>
                         header.StartsWith(
                             "X-Internal-",
                             StringComparison.OrdinalIgnoreCase)
                         || header.StartsWith(
                             "X-Service-",
                             StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            context.Request.Headers.Remove(header);
        }

        if (!IsTrustedProxy(context.Connection.RemoteIpAddress))
        {
            foreach (var header in ForwardedHeaders)
                context.Request.Headers.Remove(header);
        }

        await next(context);
    }

    private bool IsTrustedProxy(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return trustedProxies.Any(proxy =>
                   proxy.Equals(address)
                   || proxy.MapToIPv6().Equals(address.MapToIPv6()))
               || trustedNetworks.Any(network => network.Contains(address));
    }
}

/// <summary>
/// 建立 Request ID 与 Correlation ID，并覆盖客户端可能提供的非法值。
/// 标识会写入请求、响应和日志作用域，但不承载玩家身份或敏感信息。
/// </summary>
public sealed partial class GatewayRequestContextMiddleware(
    RequestDelegate next,
    ILogger<GatewayRequestContextMiddleware> logger)
{
    private const string RequestItemKey = "GuiyangEdge.RequestId";
    private const string CorrelationItemKey = "GuiyangEdge.CorrelationId";

    /// <summary>规范化请求标识、建立日志作用域并继续执行。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Normalize(
            context.Request.Headers["X-Request-Id"].ToString())
            ?? Guid.NewGuid().ToString("N");
        var correlationId = Normalize(
            context.Request.Headers["X-Correlation-Id"].ToString())
            ?? requestId;
        context.Items[RequestItemKey] = requestId;
        context.Items[CorrelationItemKey] = correlationId;
        context.TraceIdentifier = requestId;
        context.Request.Headers["X-Request-Id"] = requestId;
        context.Request.Headers["X-Correlation-Id"] = correlationId;
        context.Response.Headers["X-Request-Id"] = requestId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object?>
               {
                   ["RequestId"] = requestId,
                   ["CorrelationId"] = correlationId
               }))
        {
            Activity.Current?.SetTag("mahjong.request_id", requestId);
            Activity.Current?.SetTag(
                "mahjong.correlation_id",
                correlationId);
            await next(context);
        }
    }

    /// <summary>取得当前请求的网关 Request ID；早期异常时回退到 TraceIdentifier。</summary>
    public static string GetRequestId(HttpContext context) =>
        context.Items.TryGetValue(RequestItemKey, out var value)
            ? value?.ToString() ?? context.TraceIdentifier
            : context.TraceIdentifier;

    /// <summary>取得当前请求的 Correlation ID；早期异常时回退到 Request ID。</summary>
    public static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(CorrelationItemKey, out var value)
            ? value?.ToString() ?? GetRequestId(context)
            : GetRequestId(context);

    private static string? Normalize(string value)
    {
        var candidate = value.Trim();
        return candidate.Length is >= 8 and <= 128
               && SafeIdentifierPattern().IsMatch(candidate)
            ? candidate
            : null;
    }

    [GeneratedRegex(
        "^[A-Za-z0-9._:-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();
}

/// <summary>
/// 使用 `AllowedHosts` 执行可审计 Host 白名单并返回统一 400。
/// 校验发生在可信 Forwarded Host 被框架消费之后，避免攻击者绕过虚拟主机边界。
/// </summary>
public sealed class GatewayHostValidationMiddleware
{
    private readonly RequestDelegate next;
    private readonly string[] allowedHosts;

    /// <summary>启动时读取分号分隔白名单；空白名单按失败关闭处理。</summary>
    public GatewayHostValidationMiddleware(
        RequestDelegate next,
        IOptions<EdgeGatewayOptions> options)
    {
        this.next = next;
        allowedHosts = options.Value.AllowedHosts
            .Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);
    }

    /// <summary>验证当前 Host，不比较端口；支持 `*` 和 `*.example.com`。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (allowedHosts.Length == 0
            || !allowedHosts.Any(allowed =>
                MatchesHost(host, allowed)))
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "HOST_NOT_ALLOWED",
                "请求 Host 不在网关白名单中。");
            return;
        }
        await next(context);
    }

    private static bool MatchesHost(
        string host,
        string allowed)
    {
        if (allowed == "*") return true;
        if (allowed.StartsWith("*.", StringComparison.Ordinal)
            && host.EndsWith(
                allowed[1..],
                StringComparison.OrdinalIgnoreCase))
        {
            return host.Length > allowed.Length - 1;
        }
        return host.Equals(
            allowed.Trim('[', ']'),
            StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 校验 UE 客户端发布契约、请求体大小和 Content-Type。
/// 健康探针不属于玩家 API，不要求客户端头。
/// </summary>
public sealed class ClientContractMiddleware(
    RequestDelegate next,
    IOptions<EdgeGatewayOptions> options)
{
    private readonly EdgeGatewayOptions options = options.Value;

    /// <summary>对 `/api` 请求执行失败关闭校验；通过后把规范头继续转发给业务服务。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var bodyFeature =
            context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyFeature is { IsReadOnly: false })
            bodyFeature.MaxRequestBodySize =
                options.MaximumRequestBodyBytes;
        if (context.Request.ContentLength >
            options.MaximumRequestBodyBytes)
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "REQUEST_BODY_TOO_LARGE",
                "请求体超过网关允许的大小。");
            return;
        }

        if (HasBody(context.Request)
            && !IsSupportedJsonContentType(
                context.Request.ContentType))
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "CONTENT_TYPE_NOT_SUPPORTED",
                "请求体必须使用 application/json。");
            return;
        }

        var clientVersion =
            context.Request.Headers["X-Client-Version"].ToString().Trim();
        var protocolVersion =
            context.Request.Headers["X-Protocol-Version"].ToString().Trim();
        var platform =
            context.Request.Headers["X-Platform"].ToString().Trim();
        var channel =
            context.Request.Headers["X-Channel"].ToString().Trim();
        if (!Version.TryParse(clientVersion, out var parsedClientVersion)
            || !Version.TryParse(
                options.ClientContract.MinimumClientVersion,
                out var minimumClientVersion))
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "CLIENT_CONTRACT_INVALID",
                "客户端版本头缺失或格式无效。");
            return;
        }
        if (parsedClientVersion < minimumClientVersion)
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status426UpgradeRequired,
                "CLIENT_UPGRADE_REQUIRED",
                "客户端版本过低，必须升级后继续。");
            return;
        }
        if (!options.ClientContract.SupportedProtocolVersions.Contains(
                protocolVersion,
                StringComparer.Ordinal))
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status426UpgradeRequired,
                "PROTOCOL_UPGRADE_REQUIRED",
                "客户端协议版本不受支持。");
            return;
        }
        if (!options.ClientContract.AllowedPlatforms.Contains(
                platform,
                StringComparer.OrdinalIgnoreCase)
            || !options.ClientContract.AllowedChannels.Contains(
                channel,
                StringComparer.OrdinalIgnoreCase))
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "CLIENT_DISTRIBUTION_INVALID",
                "客户端平台或渠道不受支持。");
            return;
        }

        context.Request.Headers["X-Client-Version"] =
            parsedClientVersion.ToString();
        context.Request.Headers["X-Protocol-Version"] =
            protocolVersion;
        context.Request.Headers["X-Platform"] = platform;
        context.Request.Headers["X-Channel"] = channel;
        await next(context);
    }

    private static bool HasBody(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        || HttpMethods.IsPut(request.Method)
        || HttpMethods.IsPatch(request.Method)
            ? request.ContentLength is > 0
              || request.Headers.TransferEncoding.Count > 0
            : false;

    private static bool IsSupportedJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)
            || !MediaTypeHeaderValue.TryParse(
                contentType,
                out var parsed))
            return false;
        var mediaType = parsed.MediaType.Value ?? string.Empty;
        return mediaType.Equals(
                   "application/json",
                   StringComparison.OrdinalIgnoreCase)
               || (mediaType.StartsWith(
                       "application/",
                       StringComparison.OrdinalIgnoreCase)
                   && mediaType.EndsWith(
                       "+json",
                       StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>在本机限流之后执行 Redis 跨实例限流，避免单实例扩容绕过总容量限制。</summary>
public sealed class DistributedRateLimitMiddleware(
    RequestDelegate next,
    IDistributedGatewayRateLimiter limiter,
    ILogger<DistributedRateLimitMiddleware> logger)
{
    /// <summary>
    /// 使用玩家主体或连接 IP 的不可逆摘要分区。
    /// Redis 故障策略由配置决定；任何情况下都不记录原始玩家标识或 IP。
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var subject =
            context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        DistributedRateLimitDecision decision;
        try
        {
            decision = await limiter.TryAcquireAsync(
                subject,
                context.RequestAborted);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Redis 分布式限流检查失败，按配置执行失败关闭");
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "RATE_LIMIT_BACKEND_UNAVAILABLE",
                "网关容量保护暂时不可用。");
            return;
        }

        if (!decision.Acquired)
        {
            context.Response.Headers.RetryAfter =
                Math.Max(1, decision.RetryAfterSeconds).ToString();
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status429TooManyRequests,
                "RATE_LIMIT_EXCEEDED",
                "请求过于频繁，请稍后重试。");
            return;
        }

        await next(context);
    }
}

/// <summary>
/// 捕获接入层异常并识别 YARP 转发错误。
/// 只有存在 IForwarderErrorFeature 时才规范 502/503/504，业务服务主动返回的错误保持原样。
/// </summary>
public sealed class GatewayErrorMiddleware(
    RequestDelegate next,
    ILogger<GatewayErrorMiddleware> logger)
{
    /// <summary>执行下游管线并将未开始的网关异常映射为稳定错误。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
            var forwarderError =
                context.GetForwarderErrorFeature();
            if (forwarderError is not null
                && !context.Response.HasStarted)
            {
                var statusCode =
                    forwarderError.Error is
                        ForwarderError.RequestTimedOut
                        or ForwarderError.RequestCanceled
                        ? StatusCodes.Status504GatewayTimeout
                        : context.Response.StatusCode switch
                {
                    StatusCodes.Status503ServiceUnavailable =>
                        StatusCodes.Status503ServiceUnavailable,
                    StatusCodes.Status504GatewayTimeout =>
                        StatusCodes.Status504GatewayTimeout,
                    _ => StatusCodes.Status502BadGateway
                };
                await GatewayErrorWriter.WriteAsync(
                    context,
                    statusCode,
                    statusCode switch
                    {
                        StatusCodes.Status503ServiceUnavailable =>
                            "UPSTREAM_UNAVAILABLE",
                        StatusCodes.Status504GatewayTimeout =>
                            "UPSTREAM_TIMEOUT",
                        _ => "UPSTREAM_CONNECTION_FAILED"
                    },
                    statusCode switch
                    {
                        StatusCodes.Status503ServiceUnavailable =>
                            "没有健康的上游服务。",
                        StatusCodes.Status504GatewayTimeout =>
                            "上游服务响应超时。",
                        _ => "无法连接上游服务。"
                    });
            }
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode ==
                  StatusCodes.Status413PayloadTooLarge)
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "REQUEST_BODY_TOO_LARGE",
                "请求体超过网关允许的大小。");
        }
        catch (OperationCanceledException)
            when (!context.RequestAborted.IsCancellationRequested)
        {
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status504GatewayTimeout,
                "UPSTREAM_TIMEOUT",
                "上游服务响应超时。");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "网关处理请求时发生未处理异常");
            await GatewayErrorWriter.WriteAsync(
                context,
                StatusCodes.Status502BadGateway,
                "GATEWAY_FAILURE",
                "网关暂时无法处理请求。");
        }
    }
}
