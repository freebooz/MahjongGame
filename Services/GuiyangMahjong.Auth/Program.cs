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

public partial class Program;
