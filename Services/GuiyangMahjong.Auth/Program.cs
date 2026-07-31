using System.Threading.RateLimiting;
using GuiyangMahjong.Auth.Administration;
using GuiyangMahjong.Auth.Auth;
using GuiyangMahjong.Auth.Devices;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Infrastructure;
using GuiyangMahjong.Auth.Options;
using GuiyangMahjong.Auth.Players;
using GuiyangMahjong.Auth.Security;
using GuiyangMahjong.Auth.Services;
using GuiyangMahjong.Auth.Sessions;
using GuiyangMahjong.Auth.Storage;
using GuiyangMahjong.Observability;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.Auth");

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.PersistenceMode is "InMemory" or "Postgres",
        "Auth PersistenceMode must be InMemory or Postgres.")
    .Validate(options => options.PersistenceMode != "Postgres"
                         || !string.IsNullOrWhiteSpace(options.PostgresConnectionString),
        "Auth PostgreSQL connection string is required in Postgres mode.")
    .Validate(options => !builder.Environment.IsProduction()
                         || !options.ApplyDatabaseMigrations,
        "Production Auth runtime must not execute database migrations.")
    .Validate(options => string.IsNullOrEmpty(options.MonitoringReadOnlyToken)
                         || options.MonitoringReadOnlyToken.Length >= 32,
        "Auth:MonitoringReadOnlyToken must be empty or contain at least 32 characters.")
    .Validate(options => string.IsNullOrEmpty(options.ManagementCommandToken)
                         || options.ManagementCommandToken.Length >= 32,
        "Auth:ManagementCommandToken must be empty or contain at least 32 characters.")
    .Validate(options => string.IsNullOrEmpty(options.ManagementCommandToken)
                         || !string.Equals(
                             options.ManagementCommandToken,
                             options.MonitoringReadOnlyToken,
                             StringComparison.Ordinal),
        "Auth management and monitoring credentials must be different.")
    .Validate(options => !builder.Environment.IsProduction()
                         || (!options.TokenSigningKey.StartsWith(
                                 "development-only", StringComparison.OrdinalIgnoreCase)
                             && !options.GuestIdentityPepper.StartsWith(
                                 "development-only", StringComparison.OrdinalIgnoreCase)),
        "Production Auth must not use development-only signing material.")
    .ValidateOnStart();
builder.Services.AddOptions<SessionPolicyOptions>()
    .Bind(builder.Configuration.GetSection(SessionPolicyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => Enum.TryParse<SessionPolicyMode>(
            options.Mode,
            ignoreCase: false,
            out _),
        "Sessions:Mode must be SingleDevice or MultiDevice.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PlayerAccessTokenIssuer>();
builder.Services.AddSingleton<LocalPlayerNameGenerator>();
builder.Services.AddSingleton<IAuthStore>(provider =>
{
    var options = provider.GetRequiredService<IOptions<AuthOptions>>().Value;
    if (options.PersistenceMode == "InMemory") return new InMemoryAuthStore();
    var dataSource = NpgsqlDataSource.Create(options.PostgresConnectionString);
    return new PostgresAuthStore(dataSource);
});
// 模块仍共享一个部署单元和一个存储实例，但通过窄接口暴露玩家档案，
// 防止 Players 模块获得 Refresh Token 或签名密钥能力。
builder.Services.AddSingleton<IPlayerProfileReader>(provider =>
    (IPlayerProfileReader)provider.GetRequiredService<IAuthStore>());
builder.Services.AddSingleton<IIdentityRepository>(provider =>
    provider.GetRequiredService<IAuthStore>());
builder.Services.AddSingleton<ISessionRepository>(provider =>
    provider.GetRequiredService<IAuthStore>());
builder.Services.AddSingleton<IDeviceAuditWriter>(provider =>
    provider.GetRequiredService<IAuthStore>());
builder.Services.AddSingleton<IIdentityAdministrationStore>(provider =>
    provider.GetRequiredService<IAuthStore>());
builder.Services.AddSingleton<IPlayerDirectoryReader>(provider =>
    provider.GetRequiredService<IAuthStore>());
builder.Services.AddSingleton<IIdentityStorageLifecycle>(provider =>
    provider.GetRequiredService<IAuthStore>());
builder.Services.AddSingleton<AuthService>(provider => new AuthService(
    provider.GetRequiredService<IIdentityRepository>(),
    provider.GetRequiredService<ISessionRepository>(),
    provider.GetRequiredService<IDeviceAuditWriter>(),
    provider.GetRequiredService<PlayerAccessTokenIssuer>(),
    provider.GetRequiredService<LocalPlayerNameGenerator>(),
    provider.GetRequiredService<IOptions<AuthOptions>>(),
    provider.GetRequiredService<IOptions<SessionPolicyOptions>>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<AuthStoreInitializer>();
builder.Services.AddRateLimiter(options => options.AddPolicy("auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        })));

var app = builder.Build();
app.UseMahjongObservability(
    "GuiyangMahjong.Auth",
    app.Environment.EnvironmentName);
if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.EnableHttpsRedirection)
    app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (
    IIdentityStorageLifecycle store,
    CancellationToken cancellationToken) =>
    await store.CheckHealthAsync(cancellationToken)
        ? Results.Ok(new { status = "ready", identityStore = "ready" })
        : Results.Json(
            new { status = "not-ready", identityStore = "unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/openapi/v1.yaml", () => Results.File(
    Path.Combine(AppContext.BaseDirectory, "OpenAPI", "auth-v1.openapi.yaml"),
    "application/yaml"));

app.MapGet("/internal/identity/token-validation-config", (
    HttpContext context,
    IOptions<AuthOptions> options) =>
{
    if (!HasMonitoringCredential(context, options.Value.MonitoringReadOnlyToken))
        return Results.Unauthorized();
    // HMAC 没有可公开的验证公钥，因此这里只发布非敏感算法和轮换元数据；
    // EdgeGateway/Lobby 的实际验证密钥继续由各自生产身份从密钥系统注入。
    return Results.Ok(new
    {
        format = "base64url-json.hmac-sha256",
        algorithm = "HMAC-SHA256",
        activeKeyId = options.Value.ActiveSigningKeyId,
        accessTokenLifetimeMinutes = options.Value.AccessTokenMinutes,
        claims = new[]
        {
            "Sub", "Name", "Provider", "Iat", "Exp",
            "Sid", "SessionEpoch", "SecurityEpoch"
        }
    });
});

app.MapPost("/v1/auth/guest", async (
    GuestLoginRequest request,
    HttpContext context,
    AuthService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.LoginGuestAsync(
        request,
        new LoginObservation(
            MaskIp(context.Connection.RemoteIpAddress),
            context.Request.Headers.UserAgent.ToString()),
        cancellationToken)))
    .RequireRateLimiting("auth");

app.MapPost("/v1/auth/refresh", async (
    RefreshSessionRequest request,
    AuthService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.RefreshAsync(request, cancellationToken)))
    .RequireRateLimiting("auth");

app.MapPost("/v1/auth/logout", async (
    LogoutRequest request,
    AuthService service,
    CancellationToken cancellationToken) =>
{
    await service.LogoutAsync(request, cancellationToken);
    return Results.NoContent();
}).RequireRateLimiting("auth");

app.MapPost("/internal/admin/players/{playerId}/sessions/revoke", async (
    string playerId,
    AdminRevokePlayerSessionsRequest request,
    HttpContext context,
    IIdentityAdministrationStore store,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (!HasMonitoringCredential(context, options.Value.ManagementCommandToken))
        return Results.Unauthorized();
    var commandId = context.Request.Headers["Idempotency-Key"].ToString().Trim();
    var now = timeProvider.GetUtcNow();
    if (commandId.Length is < 16 or > 128
        || playerId.Length is < 1 or > 80
        || (request.Reason ?? string.Empty).Trim().Length is < 5 or > 500
        || (request.TraceId ?? string.Empty).Trim().Length is < 8 or > 64
        || request.EffectiveAtUtc < now.AddHours(-24)
        || request.EffectiveAtUtc > now.AddMinutes(1))
    {
        return Results.BadRequest(new
        {
            code = "INVALID_ADMIN_COMMAND",
            message = "Management command validation failed."
        });
    }
    return Results.Ok(await store.RevokePlayerSessionsAsync(
        commandId,
        playerId,
        request.EffectiveAtUtc,
        cancellationToken));
});

app.MapPost("/internal/admin/players/{playerId}/controls", async (
    string playerId,
    AdminUpdatePlayerControlRequest request,
    HttpContext context,
    IIdentityAdministrationStore store,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (!HasMonitoringCredential(context, options.Value.ManagementCommandToken))
        return Results.Unauthorized();
    var commandId = context.Request.Headers["Idempotency-Key"].ToString().Trim();
    var now = timeProvider.GetUtcNow();
    if (!Enum.TryParse<AdminPlayerControlAction>(
            request.ActionType,
            out var action)
        || !ValidatePlayerControlCommand(
            commandId,
            playerId,
            request,
            action,
            now))
    {
        return Results.BadRequest(new
        {
            code = "INVALID_ADMIN_COMMAND",
            message = "Player control command validation failed."
        });
    }
    var result = await store.ApplyPlayerControlAsync(
        commandId,
        playerId,
        action,
        request.ExpectedVersion,
        request.Reason.Trim(),
        request.TraceId.Trim(),
        request.TicketId.Trim(),
        request.RequestedBy.Trim(),
        request.ApprovedBy.Trim(),
        request.EffectiveAtUtc,
        request.ExpiresAtUtc,
        request.RiskLabel?.Trim(),
        cancellationToken);
    return result.Status switch
    {
        AdminPlayerControlStatus.Applied or
        AdminPlayerControlStatus.Duplicate => Results.Ok(result.Result),
        AdminPlayerControlStatus.PlayerNotFound => Results.NotFound(new
        {
            code = "PLAYER_NOT_FOUND",
            message = result.Error
        }),
        _ => Results.Conflict(new
        {
            code = result.Status.ToString().ToUpperInvariant(),
            message = result.Error,
            currentState = result.CurrentState
        })
    };
});

app.MapGet("/internal/monitoring/players", async (
    HttpContext context,
    IPlayerDirectoryReader store,
    IOptions<AuthOptions> options,
    string? search,
    string? cursor,
    int? pageSize,
    int? limit,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (!HasMonitoringCredential(context, options.Value.MonitoringReadOnlyToken))
        return Results.Unauthorized();
    var normalizedSearch = search?.Trim() ?? string.Empty;
    if (!TryReadMonitoringCursor(
            cursor,
            normalizedSearch,
            out var afterCreatedAtUtc,
            out var afterPlayerId))
    {
        return Results.BadRequest(new
        {
            code = "INVALID_CURSOR",
            message = "Cursor is invalid or belongs to another filter."
        });
    }
    var safePageSize = Math.Clamp(pageSize ?? limit ?? 100, 1, 200);
    if (limit.HasValue)
        context.Response.Headers["Deprecation"] = "true";
    var loaded = await store.ListPlayersAsync(
        normalizedSearch,
        safePageSize + 1,
        afterCreatedAtUtc,
        afterPlayerId,
        timeProvider.GetUtcNow(),
        cancellationToken);
    var items = loaded.Take(safePageSize).ToArray();
    var nextCursor = loaded.Count > safePageSize && items.Length > 0
        ? WriteMonitoringCursor(
            items[^1].CreatedAtUtc,
            items[^1].PlayerId,
            normalizedSearch)
        : null;
    // 旧 Admin 在滚动升级窗口仍可解析数组，但页大小已受 200 上限保护；新版本必须改用 pageSize/cursor。
    if (limit.HasValue && pageSize is null && string.IsNullOrWhiteSpace(cursor))
        return Results.Ok(items);
    return Results.Ok(new
    {
        items,
        nextCursor,
        hasMore = nextCursor is not null,
        pageSize = safePageSize
    });
});

app.MapGet("/internal/monitoring/players/{playerId}", async (
    string playerId,
    HttpContext context,
    IPlayerDirectoryReader store,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (!HasMonitoringCredential(context, options.Value.MonitoringReadOnlyToken))
        return Results.Unauthorized();
    var player = await store.GetPlayerDetailAsync(
        playerId, timeProvider.GetUtcNow(), cancellationToken);
    return player is null ? Results.NotFound() : Results.Ok(player);
});

app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (AuthOperationException exception)
    {
        context.Response.StatusCode = exception.StatusCode;
        await context.Response.WriteAsJsonAsync(new
        {
            code = exception.Code,
            message = exception.Message,
            traceId = context.TraceIdentifier
        }, cancellationToken: context.RequestAborted);
    }
});

app.Run();

static bool HasMonitoringCredential(HttpContext context, string expectedToken)
{
    if (expectedToken.Length < 32) return false;
    var authorization = context.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    var supplied = Encoding.UTF8.GetBytes(authorization[7..].Trim());
    var expected = Encoding.UTF8.GetBytes(expectedToken);
    var valid = supplied.Length == expected.Length
        && CryptographicOperations.FixedTimeEquals(supplied, expected);
    CryptographicOperations.ZeroMemory(supplied);
    return valid;
}

static string MaskIp(IPAddress? address)
{
    if (address is null) return "Unknown";
    if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
    var bytes = address.GetAddressBytes();
    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.*";
    for (var index = 6; index < bytes.Length; index++) bytes[index] = 0;
    return $"{new IPAddress(bytes)}/48";
}

static bool ValidatePlayerControlCommand(
    string commandId,
    string playerId,
    AdminUpdatePlayerControlRequest request,
    AdminPlayerControlAction action,
    DateTimeOffset now)
{
    var reason = request.Reason?.Trim() ?? string.Empty;
    var traceId = request.TraceId?.Trim() ?? string.Empty;
    var ticketId = request.TicketId?.Trim() ?? string.Empty;
    var requestedBy = request.RequestedBy?.Trim() ?? string.Empty;
    var approvedBy = request.ApprovedBy?.Trim() ?? string.Empty;
    var riskLabel = request.RiskLabel?.Trim();
    if (commandId.Length is < 16 or > 128
        || playerId.Length is < 3 or > 80
        || request.ExpectedVersion < 0
        || reason.Length is < 10 or > 500
        || traceId.Length is < 8 or > 64
        || ticketId.Length is < 3 or > 128
        || requestedBy.Length is < 3 or > 128
        || approvedBy.Length is < 3 or > 128
        || requestedBy == approvedBy
        || request.EffectiveAtUtc < now.AddHours(-24)
        || request.EffectiveAtUtc > now.AddMinutes(1))
    {
        return false;
    }
    var timedAction = action is
        AdminPlayerControlAction.TemporaryFreezePlayer
        or AdminPlayerControlAction.MutePlayer
        or AdminPlayerControlAction.MarkRiskAccount;
    if (timedAction
        && (request.ExpiresAtUtc <= request.EffectiveAtUtc.AddMinutes(1)
            || request.ExpiresAtUtc > request.EffectiveAtUtc.AddDays(
                action == AdminPlayerControlAction.MarkRiskAccount ? 365 : 30)))
    {
        return false;
    }
    if (!timedAction && request.ExpiresAtUtc is not null) return false;
    if (action == AdminPlayerControlAction.MarkRiskAccount)
    {
        return riskLabel is { Length: >= 3 and <= 64 }
            && riskLabel.All(character =>
                char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-');
    }
    return riskLabel is null;
}

// 内部监控游标使用不可变创建时间和唯一 ID，并绑定搜索条件；无效或跨过滤器复用时默认拒绝。
static bool TryReadMonitoringCursor(
    string? cursor,
    string filter,
    out DateTimeOffset? createdAtUtc,
    out string? id)
{
    createdAtUtc = null;
    id = null;
    if (string.IsNullOrWhiteSpace(cursor)) return true;
    try
    {
        var payload = JsonSerializer.Deserialize<AuthMonitoringCursor>(
            Convert.FromBase64String(cursor));
        if (payload is null
            || !string.Equals(payload.Filter, filter, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.Id))
            return false;
        createdAtUtc = payload.CreatedAtUtc;
        id = payload.Id;
        return true;
    }
    catch (FormatException)
    {
        return false;
    }
    catch (JsonException)
    {
        return false;
    }
}

// 游标内容不包含凭据或个人信息；Base64 仅作为不透明传输编码，服务端仍严格校验字段。
static string WriteMonitoringCursor(
    DateTimeOffset createdAtUtc,
    string id,
    string filter) =>
    Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
        new AuthMonitoringCursor(createdAtUtc, id, filter)));

/// <summary>
/// WebApplicationFactory 集成测试可发现的 Auth 程序入口标记。
/// 运行时身份、令牌和存储初始化由上方顶级语句完成，该 partial 类型不持有秘密。
/// </summary>
public partial class Program;

/// <summary>Auth 玩家键集分页游标；创建时间与 PlayerId 共同形成确定性排序边界。</summary>
internal sealed record AuthMonitoringCursor(
    DateTimeOffset CreatedAtUtc,
    string Id,
    string Filter);
