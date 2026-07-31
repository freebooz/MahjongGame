using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GuiyangMahjong.EdgeGateway.Tests;

/// <summary>
/// Player EdgeGateway 端到端契约测试。
/// 通过临时 Kestrel 上游验证真实 YARP 转发，不引用 Auth、Lobby 或 PlayerData 内部实现。
/// </summary>
public sealed class EdgeGatewayApiTests : IAsyncLifetime
{
    private const string SigningKey =
        "test-only-edge-player-signing-key-long-enough-for-hmac";
    private UpstreamApplication? upstream;
    private EdgeGatewayWebApplicationFactory? factory;

    /// <summary>为每个测试类启动隔离上游和网关，地址只驻留测试进程。</summary>
    public async Task InitializeAsync()
    {
        upstream = await UpstreamApplication.StartAsync();
        factory = new EdgeGatewayWebApplicationFactory(
            upstream.BaseAddress);
    }

    /// <summary>先释放网关再停止临时上游，避免遗留监听端口。</summary>
    public async Task DisposeAsync()
    {
        if (factory is not null)
            await factory.DisposeAsync();
        if (upstream is not null)
            await upstream.DisposeAsync();
    }

    /// <summary>Auth 匿名路由必须删除 `/api` 前缀并保持请求方法。</summary>
    [Fact]
    public async Task AuthRoute_ForwardsAnonymousRequestAndTransformsPath()
    {
        using var client = CreateClient();
        using var response = await client.PostAsync(
            "/api/v1/auth/guest",
            JsonContent("""{"installationId":"test-installation"}"""));
        var echo = await ReadEchoAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/v1/auth/guest", echo.Path);
        Assert.Equal("POST", echo.Method);
        Assert.Equal("auth-v1", echo.Headers["X-Edge-Route"]);
    }

    /// <summary>大厅、房间和 game 兼容映射必须分别落到真实 Lobby v1 路径。</summary>
    [Theory]
    [InlineData("/api/v1/lobby/bootstrap", "/v1/lobby/bootstrap")]
    [InlineData("/api/v1/rooms/123456/route", "/v1/rooms/123456/route")]
    [InlineData("/api/v1/game/reconnect/route", "/v1/reconnect/route")]
    public async Task PlayerRoutes_ApplyConfirmedPathTransforms(
        string gatewayPath,
        string upstreamPath)
    {
        using var client = CreateClient(
            CreateLegacyToken());
        using var response = await client.GetAsync(gatewayPath);
        var echo = await ReadEchoAsync(response);

        Assert.Equal(upstreamPath, echo.Path);
    }

    /// <summary>标准 JWT 兼容入口接受签名、issuer、audience 和时效均有效的令牌。</summary>
    [Fact]
    public async Task JwtToken_WhenValid_ForwardsAuthorizedRoute()
    {
        using var client = CreateClient(CreateJwt());
        using var response = await client.GetAsync(
            "/api/v1/lobby/bootstrap");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>网关在有界密钥轮换窗口内继续接受旧 HMAC 密钥签发的有效访问令牌。</summary>
    [Fact]
    public async Task LegacyToken_PreviousRotationKey_ForwardsAuthorizedRoute()
    {
        const string previousKey =
            "test-only-edge-previous-signing-key-long-enough";
        using var rotationFactory = new EdgeGatewayWebApplicationFactory(
            upstream?.BaseAddress
                ?? throw new InvalidOperationException("Test upstream is not running."),
            previousLegacyValidationKey: previousKey);
        using var client = CreateClient(
            CreateLegacyToken(previousKey),
            rotationFactory);

        using var response = await client.GetAsync("/api/v1/lobby/bootstrap");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>篡改 JWT 签名必须在网关返回统一 401，不能到达上游。</summary>
    [Fact]
    public async Task JwtToken_WhenInvalid_ReturnsUnifiedUnauthorized()
    {
        using var client = CreateClient(
            CreateJwt()[..^1] + "x");
        using var response = await client.GetAsync(
            "/api/v1/lobby/bootstrap");
        var error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHENTICATED", error.Code);
    }

    /// <summary>Auth 登录和刷新入口允许匿名访问，但仍要求客户端契约头。</summary>
    [Fact]
    public async Task AnonymousAuthEndpoint_DoesNotRequireBearer()
    {
        using var client = CreateClient();
        using var response = await client.PostAsync(
            "/api/v1/auth/refresh",
            JsonContent("""{"refreshToken":"opaque-test-value"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>除 Auth 外的玩家路由必须要求本地验证成功的 Access Token。</summary>
    [Fact]
    public async Task PlayerEndpoint_WithoutToken_ReturnsUnauthorized()
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(
            "/api/v1/rooms");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>玩家身份、权限和内部服务头必须在转发前全部移除。</summary>
    [Fact]
    public async Task InternalIdentityHeaders_AreRemovedBeforeForwarding()
    {
        using var client = CreateClient(CreateLegacyToken());
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/rooms");
        request.Headers.TryAddWithoutValidation(
            "X-Player-Id",
            "forged-player");
        request.Headers.TryAddWithoutValidation(
            "X-Internal-Secret",
            "forged");
        request.Headers.TryAddWithoutValidation(
            "X-Service-Role",
            "admin");
        request.Headers.TryAddWithoutValidation(
            "X-HTTP-Method-Override",
            "DELETE");
        using var response = await client.SendAsync(request);
        var echo = await ReadEchoAsync(response);

        Assert.DoesNotContain("X-Player-Id", echo.Headers.Keys);
        Assert.DoesNotContain("X-Internal-Secret", echo.Headers.Keys);
        Assert.DoesNotContain("X-Service-Role", echo.Headers.Keys);
        Assert.DoesNotContain(
            "X-HTTP-Method-Override",
            echo.Headers.Keys);
    }

    /// <summary>非可信来源提供的 Forwarded 头不能以攻击者值到达上游。</summary>
    [Fact]
    public async Task UntrustedForwardedHeaders_AreNotPropagatedAsSupplied()
    {
        using var client = CreateClient(CreateLegacyToken());
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Forwarded-For",
            "203.0.113.55");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Forwarded-Host",
            "attacker.invalid");
        using var response = await client.GetAsync(
            "/api/v1/rooms");
        var echo = await ReadEchoAsync(response);

        Assert.DoesNotContain(
            "203.0.113.55",
            echo.Headers.GetValueOrDefault("X-Forwarded-For")
                ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "attacker.invalid",
            echo.Headers.GetValueOrDefault("X-Forwarded-Host")
                ?? string.Empty,
            StringComparison.Ordinal);
    }

    /// <summary>低于最低版本的客户端必须收到 426。</summary>
    [Fact]
    public async Task OutdatedClientVersion_ReturnsUpgradeRequired()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Remove("X-Client-Version");
        client.DefaultRequestHeaders.Add(
            "X-Client-Version",
            "0.9.0");
        using var response = await client.PostAsync(
            "/api/v1/auth/guest",
            JsonContent("{}"));

        Assert.Equal(
            HttpStatusCode.UpgradeRequired,
            response.StatusCode);
    }

    /// <summary>不在白名单内的协议版本必须收到 426。</summary>
    [Fact]
    public async Task UnsupportedProtocolVersion_ReturnsUpgradeRequired()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Remove("X-Protocol-Version");
        client.DefaultRequestHeaders.Add(
            "X-Protocol-Version",
            "999");
        using var response = await client.PostAsync(
            "/api/v1/auth/guest",
            JsonContent("{}"));

        Assert.Equal(
            HttpStatusCode.UpgradeRequired,
            response.StatusCode);
    }

    /// <summary>平台或渠道不在发布白名单时必须在转发前拒绝。</summary>
    [Fact]
    public async Task UnsupportedPlatformOrChannel_ReturnsBadRequest()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Remove("X-Platform");
        client.DefaultRequestHeaders.Add(
            "X-Platform",
            "UntrustedPlatform");
        using var response = await client.PostAsync(
            "/api/v1/auth/guest",
            JsonContent("{}"));
        var error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CLIENT_DISTRIBUTION_INVALID", error.Code);
    }

    /// <summary>Host 不在显式白名单时由 HostFiltering 在进入代理前拒绝。</summary>
    [Fact]
    public async Task HostOutsideAllowList_IsRejected()
    {
        await using var hostFactory =
            new EdgeGatewayWebApplicationFactory(
                upstream!.BaseAddress,
                allowedHosts: "api.example.test");
        using var client = CreateClient(null, hostFactory);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/guest");
        request.Headers.Host = "attacker.invalid";
        request.Content = JsonContent("{}");
        using var response = await client.SendAsync(request);
        var error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("HOST_NOT_ALLOWED", error.Code);
    }

    /// <summary>进程内固定窗口超过许可数后返回统一 429 和 Retry-After。</summary>
    [Fact]
    public async Task LocalRateLimit_WhenExceeded_ReturnsTooManyRequests()
    {
        await using var limitedFactory =
            new EdgeGatewayWebApplicationFactory(
                upstream!.BaseAddress,
                anonymousPermitLimit: 1);
        using var client = CreateClient(
            token: null,
            limitedFactory);
        using var first = await client.PostAsync(
            "/api/v1/auth/guest",
            JsonContent("{}"));
        using var second = await client.PostAsync(
            "/api/v1/auth/guest",
            JsonContent("{}"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            second.StatusCode);
        Assert.True(second.Headers.RetryAfter is not null);
    }

    /// <summary>已知 Content-Length 超过上限时在读取或转发前返回 413。</summary>
    [Fact]
    public async Task OversizedRequestBody_ReturnsPayloadTooLarge()
    {
        await using var smallFactory =
            new EdgeGatewayWebApplicationFactory(
                upstream!.BaseAddress,
                maximumBodyBytes: 1024);
        using var client = CreateClient(null, smallFactory);
        using var content = new ByteArrayContent(new byte[2048]);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(
            "/api/v1/auth/guest",
            content);

        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            response.StatusCode);
    }

    /// <summary>带正文的玩家写请求只接受 JSON 媒体类型。</summary>
    [Fact]
    public async Task UnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        using var client = CreateClient();
        using var response = await client.PostAsync(
            "/api/v1/auth/guest",
            new StringContent(
                "plain",
                Encoding.UTF8,
                "text/plain"));

        Assert.Equal(
            HttpStatusCode.UnsupportedMediaType,
            response.StatusCode);
    }

    /// <summary>无法建立上游连接时只能返回 502 或无健康目标的 503。</summary>
    [Fact]
    public async Task UnavailableUpstream_ReturnsGatewayFailure()
    {
        await using var unavailableFactory =
            new EdgeGatewayWebApplicationFactory(
                new Uri("http://127.0.0.1:1"));
        using var client = CreateClient(null, unavailableFactory);
        using var response = await client.PostAsync(
            "/api/v1/auth/guest",
            JsonContent("{}"));

        Assert.Contains(
            response.StatusCode,
            new[]
            {
                HttpStatusCode.BadGateway,
                HttpStatusCode.ServiceUnavailable
            });
    }

    /// <summary>路由超时必须转换为 504，且不透明重试 POST。</summary>
    [Fact]
    public async Task UpstreamTimeout_ReturnsGatewayTimeout()
    {
        await using var timeoutFactory =
            new EdgeGatewayWebApplicationFactory(
                upstream!.BaseAddress,
                routeTimeoutMilliseconds: 100);
        using var client = CreateClient(
            CreateLegacyToken(),
            timeoutFactory);
        using var response = await client.GetAsync(
            "/api/v1/game/slow");

        Assert.Equal(
            HttpStatusCode.GatewayTimeout,
            response.StatusCode);
    }

    /// <summary>合法 Request ID 与 Correlation ID 必须到达上游并回写响应。</summary>
    [Fact]
    public async Task RequestIdentifiers_ArePropagated()
    {
        using var client = CreateClient(CreateLegacyToken());
        client.DefaultRequestHeaders.Remove("X-Request-Id");
        client.DefaultRequestHeaders.Add(
            "X-Request-Id",
            "request-contract-0001");
        client.DefaultRequestHeaders.Remove("X-Correlation-Id");
        client.DefaultRequestHeaders.Add(
            "X-Correlation-Id",
            "correlation-contract-0001");
        using var response = await client.GetAsync(
            "/api/v1/rooms");
        var echo = await ReadEchoAsync(response);

        Assert.Equal(
            "request-contract-0001",
            GetHeader(echo, "X-Request-Id"));
        Assert.Equal(
            "correlation-contract-0001",
            GetHeader(echo, "X-Correlation-Id"));
        Assert.Equal(
            "request-contract-0001",
            response.Headers.GetValues("X-Request-Id").Single());
    }

    /// <summary>有效 W3C traceparent 必须跨 YARP 转发到业务服务。</summary>
    [Fact]
    public async Task W3CTraceContext_IsPropagated()
    {
        const string traceParent =
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        using var client = CreateClient(CreateLegacyToken());
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "traceparent",
            traceParent);
        using var response = await client.GetAsync(
            "/api/v1/rooms");
        var echo = await ReadEchoAsync(response);

        Assert.StartsWith(
            "00-4bf92f3577b34da6a3ce929d0e0e4736-",
            echo.Headers["traceparent"],
            StringComparison.Ordinal);
    }

    /// <summary>Live、Startup 和 Ready 三类探针均可独立执行。</summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/startup")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ReturnSuccess(string path)
    {
        using var client = factory!.CreateClient();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>网关程序集不得引用业务服务或 PostgreSQL 驱动。</summary>
    [Fact]
    public void GatewayAssembly_HasNoBusinessServiceOrDatabaseReference()
    {
        var references = typeof(Program).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToArray();

        Assert.DoesNotContain(
            references,
            name => name.StartsWith(
                "GuiyangMahjong.Auth",
                StringComparison.Ordinal)
                || name.StartsWith(
                    "GuiyangMahjong.Lobby",
                    StringComparison.Ordinal)
                || name.StartsWith(
                    "GuiyangMahjong.Allocator",
                    StringComparison.Ordinal)
                || name.StartsWith(
                    "GuiyangMahjong.PlayerData",
                    StringComparison.Ordinal));
        Assert.DoesNotContain("Npgsql", references);
    }

    private HttpClient CreateClient(
        string? token = null,
        EdgeGatewayWebApplicationFactory? selectedFactory = null)
    {
        var client = (selectedFactory ?? factory!).CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("http://localhost"),
                AllowAutoRedirect = false
            });
        client.DefaultRequestHeaders.Add(
            "X-Client-Version",
            "1.2.3");
        client.DefaultRequestHeaders.Add(
            "X-Protocol-Version",
            "1");
        client.DefaultRequestHeaders.Add(
            "X-Platform",
            "Windows");
        client.DefaultRequestHeaders.Add(
            "X-Channel",
            "development");
        client.DefaultRequestHeaders.Add(
            "X-Request-Id",
            Guid.NewGuid().ToString("N"));
        client.DefaultRequestHeaders.Add(
            "X-Correlation-Id",
            Guid.NewGuid().ToString("N"));
        if (token is not null)
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent JsonContent(string value) =>
        new(value, Encoding.UTF8, "application/json");

    private static async Task<UpstreamEcho> ReadEchoAsync(
        HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UpstreamEcho>()
               ?? throw new InvalidDataException(
                   "临时上游未返回测试回显。");
    }

    private static async Task<GatewayErrorProjection> ReadErrorAsync(
        HttpResponseMessage response) =>
        await response.Content
            .ReadFromJsonAsync<GatewayErrorProjection>()
        ?? throw new InvalidDataException("网关错误正文为空。");

    private static string GetHeader(
        UpstreamEcho echo,
        string name) =>
        echo.Headers.Single(pair =>
                pair.Key.Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
            .Value;

    private static string CreateLegacyToken(string signingKey = SigningKey)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Sub = "player-edge-test",
            Name = "边缘测试玩家",
            Provider = "Guest",
            Iat = now.ToUnixTimeMilliseconds(),
            Exp = now.AddMinutes(15).ToUnixTimeSeconds()
        });
        var encodedPayload = Base64Url(payload);
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingKey),
            Encoding.ASCII.GetBytes(encodedPayload));
        return $"{encodedPayload}.{Base64Url(signature)}";
    }

    private static string CreateJwt()
    {
        var now = DateTimeOffset.UtcNow;
        var header = Base64Url(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "HS256",
                typ = "JWT"
            }));
        var payload = Base64Url(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                sub = "player-jwt-test",
                iss = "test-auth",
                aud = "mahjong-player",
                iat = now.ToUnixTimeSeconds(),
                nbf = now.AddSeconds(-1).ToUnixTimeSeconds(),
                exp = now.AddMinutes(15).ToUnixTimeSeconds()
            }));
        var unsigned = $"{header}.{payload}";
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SigningKey),
            Encoding.ASCII.GetBytes(unsigned));
        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

/// <summary>测试读取的网关错误最小投影。</summary>
public sealed record GatewayErrorProjection(string Code);

/// <summary>临时上游回显的路径、方法和请求头。</summary>
public sealed record UpstreamEcho(
    string Path,
    string Method,
    Dictionary<string, string> Headers);

/// <summary>
/// 覆盖网关上游地址和非敏感测试策略。
/// 不注册数据库、真实 Redis 或业务服务测试替身。
/// </summary>
public sealed class EdgeGatewayWebApplicationFactory(
    Uri upstreamAddress,
    int anonymousPermitLimit = 1000,
    long maximumBodyBytes = 1024 * 1024,
    int routeTimeoutMilliseconds = 10_000,
    string allowedHosts = "*",
    string? previousLegacyValidationKey = null)
    : WebApplicationFactory<Program>
{
    /// <inheritdoc/>
    protected override void ConfigureWebHost(
        IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                // 框架 HostFiltering 保持通配，业务白名单由网关统一错误中间件负责。
                ["AllowedHosts"] = "*",
                ["EdgeGateway:AllowedHosts"] = allowedHosts,
                ["EdgeGateway:MaximumRequestBodyBytes"] =
                    maximumBodyBytes.ToString(),
                ["EdgeGateway:RouteTimeoutMilliseconds"] =
                    routeTimeoutMilliseconds.ToString(),
                ["EdgeGateway:PlayerTokens:LegacySigningKey"] =
                    "test-only-edge-player-signing-key-long-enough-for-hmac",
                ["EdgeGateway:PlayerTokens:JwtSigningKey"] =
                    "test-only-edge-player-signing-key-long-enough-for-hmac",
                ["EdgeGateway:PlayerTokens:JwtIssuer"] =
                    "test-auth",
                ["EdgeGateway:PlayerTokens:JwtAudience"] =
                    "mahjong-player",
                ["EdgeGateway:LocalRateLimit:AnonymousPermitLimit"] =
                    anonymousPermitLimit.ToString(),
                ["EdgeGateway:LocalRateLimit:PlayerPermitLimit"] =
                    "1000",
                ["EdgeGateway:DistributedRateLimit:Enabled"] =
                    "false",
                ["ReverseProxy:Clusters:auth:Destinations:primary:Address"] =
                    upstreamAddress.ToString(),
                ["ReverseProxy:Clusters:lobby:Destinations:primary:Address"] =
                    upstreamAddress.ToString()
            };
            if (previousLegacyValidationKey is not null)
            {
                values["EdgeGateway:PlayerTokens:PreviousLegacyValidationKeys:0"] =
                    previousLegacyValidationKey;
            }
            configuration.AddInMemoryCollection(values);
        });
    }
}

/// <summary>
/// 真实 loopback Kestrel 上游。
/// 回显只服务于测试，确保 Path Transform、请求头和超时经过完整网络栈。
/// </summary>
public sealed class UpstreamApplication(
    WebApplication application,
    Uri baseAddress)
    : IAsyncDisposable
{
    /// <summary>网关测试使用的临时上游基地址。</summary>
    public Uri BaseAddress { get; } = baseAddress;

    /// <summary>绑定随机 loopback 端口并返回已启动应用。</summary>
    public static async Task<UpstreamApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet(
            "/health/ready",
            () => Results.Ok(new { status = "ready" }));
        app.MapMethods(
            "/{**path}",
            ["GET", "POST", "PUT", "PATCH", "DELETE"],
            async context =>
            {
                if (context.Request.Path == "/v1/slow")
                    await Task.Delay(
                        TimeSpan.FromSeconds(2),
                        context.RequestAborted);
                await context.Response.WriteAsJsonAsync(
                    new UpstreamEcho(
                        context.Request.Path.Value
                        ?? string.Empty,
                        context.Request.Method,
                        context.Request.Headers.ToDictionary(
                            header => header.Key,
                            header => header.Value.ToString(),
                            StringComparer.OrdinalIgnoreCase)),
                    context.RequestAborted);
            });
        await app.StartAsync();
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;
        var address = addresses?.Single()
                      ?? throw new InvalidOperationException(
                          "临时上游没有监听地址。");
        return new UpstreamApplication(
            app,
            new Uri(address));
    }

    /// <summary>停止 Kestrel 并释放宿主资源。</summary>
    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
    }
}
