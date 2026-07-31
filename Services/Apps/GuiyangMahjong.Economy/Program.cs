using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GuiyangMahjong.Economy.Domain;
using GuiyangMahjong.Economy.Options;
using GuiyangMahjong.Economy.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.AddMahjongObservability("GuiyangMahjong.Economy");
builder.Services.AddOptions<EconomyOptions>().Bind(builder.Configuration.GetSection(EconomyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(value => value.PersistenceMode is "InMemory" or "Postgres", "Economy:PersistenceMode 必须为 InMemory 或 Postgres。")
    .Validate(value => value.PersistenceMode != "Postgres" || !string.IsNullOrWhiteSpace(value.PostgresConnectionString), "Postgres 模式必须提供连接字符串。")
    .Validate(value => !builder.Environment.IsProduction() || !value.ApplyDatabaseMigrations, "生产运行身份禁止执行 DDL。")
    .Validate(value => !builder.Environment.IsProduction() || new[] { value.SourceIngestionToken, value.AdminCommandToken, value.MonitoringToken }.All(token => token.Length >= 32), "生产工作负载凭据不得为空或过短。")
    .Validate(value => new[] { value.SourceIngestionToken, value.AdminCommandToken, value.MonitoringToken }.Where(token => token.Length > 0).Distinct(StringComparer.Ordinal).Count()
        == new[] { value.SourceIngestionToken, value.AdminCommandToken, value.MonitoringToken }.Count(token => token.Length > 0), "Economy 工作负载凭据必须相互隔离。")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEconomyStore>(provider =>
{
    var value = provider.GetRequiredService<IOptions<EconomyOptions>>().Value;
    return value.PersistenceMode == "Postgres"
        ? new PostgresEconomyStore(NpgsqlDataSource.Create(value.PostgresConnectionString), value.ApplyDatabaseMigrations)
        : new InMemoryEconomyStore();
});
builder.Services.AddHostedService<EconomyInitializer>();

var app = builder.Build();
app.UseMahjongObservability("GuiyangMahjong.Economy", app.Environment.EnvironmentName);
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    try { await next(context); }
    catch (EconomyOperationException exception)
    {
        context.Response.StatusCode = exception.StatusCode;
        await context.Response.WriteAsJsonAsync(new { code = exception.Code, message = exception.Message,
            traceId = context.TraceIdentifier }, cancellationToken: context.RequestAborted);
    }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/startup", () => Results.Ok(new { status = "started" }));
app.MapGet("/health/ready", async (IEconomyStore store, CancellationToken token) =>
    await store.CheckHealthAsync(token) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

app.MapPost("/internal/sources/reward-claims", async (HttpContext context, RewardClaimRequest request,
    IOptions<EconomyOptions> options, IEconomyStore store, TimeProvider clock, CancellationToken token) =>
{
    EconomyValidation.RequireBearer(context, options.Value.SourceIngestionToken);
    var key = EconomyValidation.RequireIdempotencyKey(context);
    EconomyValidation.ValidateReward(request, clock.GetUtcNow());
    if (key != Guid.Parse(request.EventId)) throw EconomyValidation.Invalid("Idempotency-Key must match eventId.");
    var result = await store.ClaimRewardAsync(request, clock.GetUtcNow(), token);
    return result.Duplicate ? Results.Ok(result) : Results.Json(result, statusCode: 201);
}).DisableAntiforgery().WithRequestTimeout(TimeSpan.FromSeconds(10));

app.MapPost("/internal/admin/wallet-operations", async (HttpContext context, AdminWalletOperationRequest request,
    IOptions<EconomyOptions> options, IEconomyStore store, TimeProvider clock, CancellationToken token) =>
{
    EconomyValidation.RequireBearer(context, options.Value.AdminCommandToken);
    var key = EconomyValidation.RequireIdempotencyKey(context);
    EconomyValidation.ValidateWallet(request, clock.GetUtcNow());
    return Results.Ok(await store.ApplyWalletOperationAsync(key, request, clock.GetUtcNow(), token));
}).DisableAntiforgery().WithRequestTimeout(TimeSpan.FromSeconds(10));

app.MapGet("/internal/monitoring/players/{playerId}/balances", async (string playerId, HttpContext context,
    IOptions<EconomyOptions> options, IEconomyStore store, CancellationToken token) =>
{
    EconomyValidation.RequireBearer(context, options.Value.MonitoringToken);
    EconomyValidation.Identifier(playerId, "playerId");
    return Results.Ok(await store.ListBalancesAsync(playerId, token));
});
app.Run();

/// <summary>测试宿主可发现的 Economy 应用入口。</summary>
public partial class Program;

/// <summary>启动时初始化权威存储；生产环境只探测，不执行迁移。</summary>
public sealed class EconomyInitializer(IEconomyStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Economy 边界的无状态安全校验，确保失败发生在事务开始前。</summary>
internal static partial class EconomyValidation
{
    public static void RequireBearer(HttpContext context, string expected)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetBytes(header[7..].Trim()) : [];
        var wanted = Encoding.UTF8.GetBytes(expected);
        var valid = expected.Length >= 32 && supplied.Length == wanted.Length && CryptographicOperations.FixedTimeEquals(supplied, wanted);
        CryptographicOperations.ZeroMemory(supplied); CryptographicOperations.ZeroMemory(wanted);
        if (!valid) throw new EconomyOperationException("ECONOMY_UNAUTHORIZED", "A valid dedicated credential is required.", 401);
    }
    public static Guid RequireIdempotencyKey(HttpContext context) => Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var key) ? key : throw Invalid("Idempotency-Key must be a UUID.");
    public static void ValidateReward(RewardClaimRequest value, DateTimeOffset now)
    {
        if (!Guid.TryParse(value.EventId, out _)) throw Invalid("eventId must be a UUID.");
        Identifier(value.RewardGrantId, "rewardGrantId"); Identifier(value.PlayerId, "playerId");
        Identifier(value.AssetCode, "assetCode", 2, 32); Identifier(value.SourceReference, "sourceReference");
        Identifier(value.TraceId, "traceId", 8, 64);
        if (value.Amount is < 1 or > 1_000_000_000 || value.OccurredAtUtc < now.AddYears(-5) || value.OccurredAtUtc > now.AddMinutes(5)) throw Invalid("Reward payload is outside accepted limits.");
    }
    public static void ValidateWallet(AdminWalletOperationRequest value, DateTimeOffset now)
    {
        Identifier(value.PlayerId, "playerId"); Identifier(value.RequestedBy, "requestedBy"); Identifier(value.ApprovedBy, "approvedBy"); Identifier(value.TicketId, "ticketId"); Identifier(value.TraceId, "traceId", 8, 64);
        if (!Guid.TryParse(value.CaseId, out _) || value.RequestedBy == value.ApprovedBy || value.Reason.Trim().Length is < 10 or > 1000 || value.ApprovedAtUtc < now.AddDays(-7) || value.ApprovedAtUtc > now.AddMinutes(1)) throw Invalid("Approval payload is invalid.");
        if (value.OperationType == "GrantCompensation") { Identifier(value.AssetCode, "assetCode", 2, 32); if (value.Amount is < 1 or > 1_000_000_000 || value.RewardGrantId is not null) throw Invalid("Compensation payload is invalid."); }
        else if (value.OperationType == "RevokeReward") { Identifier(value.RewardGrantId, "rewardGrantId"); if (value.AssetCode is not null || value.Amount is not null) throw Invalid("Reward reversal payload is invalid."); }
        else throw Invalid("operationType is invalid.");
    }
    public static void Identifier(string? value, string name, int min = 3, int max = 128) { if (value is null || value.Length < min || value.Length > max || !Safe().IsMatch(value)) throw Invalid($"{name} contains invalid characters or length."); }
    public static EconomyOperationException Invalid(string message) => new("ECONOMY_INVALID_REQUEST", message, 400);
    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)] private static partial Regex Safe();
}
