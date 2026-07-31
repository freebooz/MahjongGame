using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using GuiyangMahjong.EdgeGateway.Health;
using GuiyangMahjong.EdgeGateway.Configuration;
using GuiyangMahjong.EdgeGateway.Middleware;
using GuiyangMahjong.EdgeGateway.Options;
using GuiyangMahjong.EdgeGateway.RateLimiting;
using GuiyangMahjong.EdgeGateway.Security;
using GuiyangMahjong.Observability;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.EdgeGateway");

var configurationSnapshot = builder.Configuration
    .GetSection(EdgeGatewayOptions.SectionName)
    .Get<EdgeGatewayOptions>()
    ?? new EdgeGatewayOptions();

builder.Services
    .AddOptions<EdgeGatewayOptions>()
    .Bind(builder.Configuration.GetSection(
        EdgeGatewayOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.PlayerTokens.PreviousLegacyValidationKeys.All(
            key => key is { Length: >= 32 })
            && options.PlayerTokens.PreviousLegacyValidationKeys
                .Append(options.PlayerTokens.LegacySigningKey)
                .Distinct(StringComparer.Ordinal)
                .Count()
               == options.PlayerTokens.PreviousLegacyValidationKeys.Length + 1,
        "EdgeGateway previous legacy validation keys must be unique and contain at least 32 characters.")
    .Validate(
        options => Version.TryParse(
            options.ClientContract.MinimumClientVersion,
            out _) && Version.TryParse(options.ClientContract.RecommendedClientVersion, out _),
        "EdgeGateway minimum client version must be a valid System.Version.")
    .Validate(
        options => options.PlayerTokens.LegacySigningKey.Length >= 32,
        "EdgeGateway legacy player signing key must contain at least 32 characters.")
    .Validate(
        options => string.IsNullOrEmpty(
                       options.PlayerTokens.JwtSigningKey)
                   || options.PlayerTokens.JwtSigningKey.Length >= 32,
        "EdgeGateway JWT signing key must be empty or contain at least 32 characters.")
    .Validate(
        options => !options.DistributedRateLimit.Enabled
                   || !string.IsNullOrWhiteSpace(
                       options.DistributedRateLimit.ConnectionString),
        "Enabled distributed rate limiting requires a Redis connection string.")
    .Validate(
        options => !builder.Environment.IsProduction()
                   || (options.DistributedRateLimit.Enabled
                       && options.DistributedRateLimit.FailClosed),
        "Production EdgeGateway must enable fail-closed Redis rate limiting.")
    .Validate(options => !options.DynamicConfiguration.Enabled
                         || (Uri.TryCreate(options.DynamicConfiguration.BaseUrl, UriKind.Absolute, out _)
                             && options.DynamicConfiguration.ReadToken.Length >= 32
                             && options.DynamicConfiguration.SigningKey.Length >= 32),
        "Enabled dynamic configuration requires an absolute internal URL and isolated 32+ character secrets.")
    .Validate(
        options => options.TrustedProxies.All(
                       value => IPAddress.TryParse(value, out _))
                   && options.TrustedProxyNetworks.All(
                       value => System.Net.IPNetwork.TryParse(
                           value,
                           out _)),
        "Trusted proxy entries must be valid IP addresses or CIDR networks.")
    .ValidateOnStart();

builder.WebHost.ConfigureKestrel(options =>
{
    // 流式转发也受 Kestrel 读取上限保护，避免只依赖可伪造的 Content-Length。
    options.Limits.MaxRequestBodySize =
        configurationSnapshot.MaximumRequestBodyBytes;
});

builder.Services
    .AddHttpClient("configuration", client =>
        client.BaseAddress = new Uri(configurationSnapshot.DynamicConfiguration.BaseUrl));
builder.Services.AddSingleton<GatewayConfigurationState>();
builder.Services.AddHostedService<GatewayConfigurationPoller>();
builder.Services
    .AddOptions<ForwardedHeadersOptions>()
    .Configure<IOptions<EdgeGatewayOptions>>(
        (options, gatewayOptions) =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedHost
                | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 2;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in gatewayOptions.Value.TrustedProxies)
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            foreach (var network in gatewayOptions.Value
                         .TrustedProxyNetworks)
                options.KnownIPNetworks.Add(
                    System.Net.IPNetwork.Parse(network));
        });

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme =
            GatewaySecurityPolicies.PlayerAccessScheme;
        options.DefaultChallengeScheme =
            GatewaySecurityPolicies.PlayerAccessScheme;
    })
    .AddPolicyScheme(
        GatewaySecurityPolicies.PlayerAccessScheme,
        GatewaySecurityPolicies.PlayerAccessScheme,
        options =>
        {
            // 标准 JWT 有两个点；当前兼容令牌只有一个点，选择过程不解析或记录内容。
            options.ForwardDefaultSelector = context =>
            {
                var authorization =
                    context.Request.Headers.Authorization.ToString();
                var token = authorization.StartsWith(
                        "Bearer ",
                        StringComparison.OrdinalIgnoreCase)
                    ? authorization["Bearer ".Length..].Trim()
                    : string.Empty;
                return token.Count(character => character == '.') == 2
                    ? GatewaySecurityPolicies.JwtPlayerScheme
                    : GatewaySecurityPolicies.LegacyPlayerScheme;
            };
        })
    .AddScheme<
        AuthenticationSchemeOptions,
        LegacyPlayerTokenAuthenticationHandler>(
        GatewaySecurityPolicies.LegacyPlayerScheme,
        _ => { })
    .AddJwtBearer(
        GatewaySecurityPolicies.JwtPlayerScheme,
        options =>
        {
            options.MapInboundClaims = false;
            options.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    await GatewayErrorWriter.WriteAsync(
                        context.HttpContext,
                        StatusCodes.Status401Unauthorized,
                        "UNAUTHENTICATED",
                        "未提供有效的玩家登录凭据。");
                },
                OnForbidden = context =>
                    GatewayErrorWriter.WriteAsync(
                        context.HttpContext,
                        StatusCodes.Status403Forbidden,
                        "FORBIDDEN",
                        "当前身份无权访问该资源。")
            };
        });
builder.Services
    .AddOptions<JwtBearerOptions>(
        GatewaySecurityPolicies.JwtPlayerScheme)
    .Configure<IOptions<EdgeGatewayOptions>>(
        (options, gatewayOptions) =>
        {
            var tokenOptions =
                gatewayOptions.Value.PlayerTokens;
            var signingKey = string.IsNullOrWhiteSpace(
                tokenOptions.JwtSigningKey)
                ? tokenOptions.LegacySigningKey
                : tokenOptions.JwtSigningKey;
            options.TokenValidationParameters =
                new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(
                        tokenOptions.ClockSkewSeconds),
                    ValidateIssuer = !string.IsNullOrWhiteSpace(
                        tokenOptions.JwtIssuer),
                    ValidIssuer = tokenOptions.JwtIssuer,
                    ValidateAudience = !string.IsNullOrWhiteSpace(
                        tokenOptions.JwtAudience),
                    ValidAudience = tokenOptions.JwtAudience,
                    NameClaimType = "sub"
                };
        });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        GatewaySecurityPolicies.PlayerPolicy,
        policy =>
        {
            policy.AddAuthenticationSchemes(
                GatewaySecurityPolicies.PlayerAccessScheme);
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("sub");
        });
    options.AddPolicy(
        GatewaySecurityPolicies.ManagementPolicy,
        policy =>
        {
            policy.AddAuthenticationSchemes(
                GatewaySecurityPolicies.JwtPlayerScheme);
            policy.RequireAuthenticatedUser();
            policy.RequireClaim(
                ClaimTypes.Role,
                "GatewayAdministrator");
        });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode =
        StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(
                MetadataName.RetryAfter,
                out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(
                    retryAfter.TotalSeconds)).ToString();
        else
            context.HttpContext.Response.Headers.RetryAfter =
                context.HttpContext.RequestServices
                    .GetRequiredService<
                        IOptions<EdgeGatewayOptions>>()
                    .Value.LocalRateLimit.WindowSeconds
                    .ToString();
        await GatewayErrorWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "RATE_LIMIT_EXCEEDED",
            "请求过于频繁，请稍后重试。");
    };
    options.AddPolicy(
        "gateway-anonymous",
        context => CreateFixedWindowPartition(
            context,
            context.RequestServices
                .GetRequiredService<
                    IOptions<EdgeGatewayOptions>>()
                .Value.LocalRateLimit
                .AnonymousPermitLimit,
            context.RequestServices
                .GetRequiredService<
                    IOptions<EdgeGatewayOptions>>()
                .Value.LocalRateLimit
                .WindowSeconds));
    options.AddPolicy(
        "gateway-player",
        context => CreateFixedWindowPartition(
            context,
            context.RequestServices
                .GetRequiredService<
                    IOptions<EdgeGatewayOptions>>()
                .Value.LocalRateLimit
                .PlayerPermitLimit,
            context.RequestServices
                .GetRequiredService<
                    IOptions<EdgeGatewayOptions>>()
                .Value.LocalRateLimit
                .WindowSeconds));
});
builder.Services.AddRequestTimeouts();
builder.Services
    .AddOptions<RequestTimeoutOptions>()
    .Configure<IOptions<EdgeGatewayOptions>>(
        (options, gatewayOptions) =>
            options.AddPolicy(
                "gateway-default",
                TimeSpan.FromMilliseconds(
                    gatewayOptions.Value
                        .RouteTimeoutMilliseconds)));

builder.Services.AddSingleton<GatewayStartupState>();
builder.Services.AddSingleton<
    IDistributedGatewayRateLimiter>(provider =>
    provider.GetRequiredService<IOptions<EdgeGatewayOptions>>()
        .Value.DistributedRateLimit.Enabled
        ? ActivatorUtilities.CreateInstance<
            RedisDistributedGatewayRateLimiter>(provider)
        : new DisabledDistributedGatewayRateLimiter());
builder.Services.AddHttpClient(
    nameof(GatewayUpstreamHealthCheck),
    client => client.Timeout = TimeSpan.FromSeconds(3));
builder.Services
    .AddHealthChecks()
    .AddCheck<GatewayStartupHealthCheck>(
        "startup",
        tags: ["startup"])
    .AddCheck<GatewayUpstreamHealthCheck>(
        "upstreams",
        tags: ["ready"])
    .AddCheck<GatewayRateLimitHealthCheck>(
        "distributed-rate-limiter",
        tags: ["ready"]);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(
        builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        // 客户端不能指定路由身份；每次配置重载都按受控 RouteId 覆盖。
        context.AddRequestTransform(transform =>
        {
            transform.ProxyRequest.Headers.Remove("X-Edge-Route");
            transform.ProxyRequest.Headers.TryAddWithoutValidation(
                "X-Edge-Route",
                context.Route.RouteId);
            transform.ProxyRequest.Headers.Remove("X-Request-Id");
            transform.ProxyRequest.Headers.TryAddWithoutValidation(
                "X-Request-Id",
                GatewayRequestContextMiddleware.GetRequestId(
                    transform.HttpContext));
            transform.ProxyRequest.Headers.Remove(
                "X-Correlation-Id");
            transform.ProxyRequest.Headers.TryAddWithoutValidation(
                "X-Correlation-Id",
                GatewayRequestContextMiddleware.GetCorrelationId(
                    transform.HttpContext));
            return ValueTask.CompletedTask;
        });
    });

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() =>
    app.Services.GetRequiredService<GatewayStartupState>()
        .MarkStarted());

// 顺序属于安全边界：先清洗，再消费可信 Forwarded Headers，随后建立日志与认证上下文。
app.UseMiddleware<GatewayHeaderSanitizationMiddleware>();
app.UseForwardedHeaders();
app.UseMiddleware<GatewayRequestContextMiddleware>();
app.UseMiddleware<GatewayHostValidationMiddleware>();
app.UseMahjongObservability(
    "GuiyangMahjong.EdgeGateway",
    app.Environment.EnvironmentName);
app.UseMiddleware<GatewayErrorMiddleware>();
app.UseMiddleware<ClientContractMiddleware>();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.UseMiddleware<DistributedRateLimitMiddleware>();

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = _ => false,
        ResponseWriter = GatewayHealthResponseWriter.WriteAsync
    });
app.MapHealthChecks(
    "/health/startup",
    new HealthCheckOptions
    {
        Predicate = registration =>
            registration.Tags.Contains("startup"),
        ResponseWriter = GatewayHealthResponseWriter.WriteAsync
    });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = registration =>
            registration.Tags.Contains("ready"),
        ResponseWriter = GatewayHealthResponseWriter.WriteAsync
    });
app.MapReverseProxy();
app.Run();

static RateLimitPartition<string> CreateFixedWindowPartition(
    HttpContext context,
    int permitLimit,
    int windowSeconds)
{
    // 已认证玩家按 sub 分区；匿名登录按 RemoteIpAddress 分区，未知连接共享保守分区。
    var key = context.User.FindFirstValue("sub")
              ?? context.Connection.RemoteIpAddress?.ToString()
              ?? "unknown";
    return RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true
        });
}

/// <summary>
/// WebApplicationFactory 集成测试可发现的 EdgeGateway 程序入口标记。
/// 运行时不保存业务状态，也不得被其他生产服务程序集引用。
/// </summary>
public partial class Program;
