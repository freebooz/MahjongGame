using System.Threading.RateLimiting;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Options;
using GuiyangMahjong.Auth.Security;
using GuiyangMahjong.Auth.Services;
using GuiyangMahjong.Auth.Storage;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Net;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.PersistenceMode is "InMemory" or "Postgres",
        "Auth PersistenceMode must be InMemory or Postgres.")
    .Validate(options => options.PersistenceMode != "Postgres"
                         || !string.IsNullOrWhiteSpace(options.PostgresConnectionString),
        "Auth PostgreSQL connection string is required in Postgres mode.")
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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PlayerAccessTokenIssuer>();
builder.Services.AddSingleton<LocalPlayerNameGenerator>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<IAuthStore>(provider =>
{
    var options = provider.GetRequiredService<IOptions<AuthOptions>>().Value;
    if (options.PersistenceMode == "InMemory") return new InMemoryAuthStore();
    var dataSource = NpgsqlDataSource.Create(options.PostgresConnectionString);
    return new PostgresAuthStore(dataSource);
});
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
if (app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.EnableHttpsRedirection)
    app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (
    IAuthStore store,
    CancellationToken cancellationToken) =>
    await store.CheckHealthAsync(cancellationToken)
        ? Results.Ok(new { status = "ready", identityStore = "ready" })
        : Results.Json(
            new { status = "not-ready", identityStore = "unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/openapi/v1.yaml", () => Results.File(
    Path.Combine(AppContext.BaseDirectory, "OpenAPI", "auth-v1.openapi.yaml"),
    "application/yaml"));

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
    IAuthStore store,
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
    IAuthStore store,
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
    IAuthStore store,
    IOptions<AuthOptions> options,
    string? search,
    int? limit,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    if (!HasMonitoringCredential(context, options.Value.MonitoringReadOnlyToken))
        return Results.Unauthorized();
    return Results.Ok(await store.ListPlayersAsync(
        search, Math.Clamp(limit ?? 500, 1, 2000), timeProvider.GetUtcNow(), cancellationToken));
});

app.MapGet("/internal/monitoring/players/{playerId}", async (
    string playerId,
    HttpContext context,
    IAuthStore store,
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

public partial class Program;
