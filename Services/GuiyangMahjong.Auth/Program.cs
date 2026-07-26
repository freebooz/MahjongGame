using System.Threading.RateLimiting;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Options;
using GuiyangMahjong.Auth.Security;
using GuiyangMahjong.Auth.Services;
using GuiyangMahjong.Auth.Storage;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;

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
    AuthService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.LoginGuestAsync(request, cancellationToken)))
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

public partial class Program;
