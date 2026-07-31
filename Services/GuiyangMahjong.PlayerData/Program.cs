// PlayerData 服务入口：装配最小权限数据库、服务身份认证、资产事务和证据投影接口。
// 生产配置缺失必须启动失败，不得自动创建高权限账号或回退到无审计的存储实现。
using System.Text;
using System.Text.Json.Serialization;
using GuiyangMahjong.PlayerData.Api;
using GuiyangMahjong.PlayerData.Options;
using GuiyangMahjong.PlayerData.Services;
using GuiyangMahjong.PlayerData.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.PlayerData");

builder.Services
    .AddOptions<PlayerDataOptions>()
    .Bind(builder.Configuration.GetSection(
        PlayerDataOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options =>
        options.PersistenceMode is "InMemory" or "Postgres",
        "PlayerData:PersistenceMode must be InMemory or Postgres.")
    .Validate(options =>
        options.PersistenceMode != "Postgres"
        || !string.IsNullOrWhiteSpace(
            options.PostgresConnectionString),
        "PlayerData PostgreSQL connection string is required.")
    .Validate(options => !builder.Environment.IsProduction()
                         || !options.ApplyDatabaseMigrations,
        "Production PlayerData runtime must not execute database migrations.")
    .Validate(options =>
        !options.ProjectionEnabled
        || options.AdminEvidenceIngestionToken.Length >= 32,
        "Enabled evidence projection requires a dedicated Admin token.")
    .Validate(options =>
        !builder.Environment.IsProduction()
        || (options.PersistenceMode == "Postgres"
            && options.SourceIngestionToken.Length >= 32
            && options.AdminCommandToken.Length >= 32
            && options.ChatGatewayToken.Length >= 32
            && options.MonitoringToken.Length >= 32
            && options.CommunityLegacyChatToken.Length >= 32
            && options.GameDataLegacyReplayToken.Length >= 32
            && options.EconomySourceToken.Length >= 32
            && options.EconomyAdminToken.Length >= 32
            && options.EconomyMonitoringToken.Length >= 32
            && options.AdminEvidenceIngestionToken.Length >= 32),
        "Production PlayerData requires PostgreSQL and all dedicated credentials.")
    .Validate(options =>
        new[]
        {
            options.SourceIngestionToken,
            options.AdminCommandToken,
            options.ChatGatewayToken,
            options.MonitoringToken,
            options.CommunityLegacyChatToken,
            options.AdminEvidenceIngestionToken,
            options.GameDataLegacyReplayToken
            ,options.EconomySourceToken, options.EconomyAdminToken, options.EconomyMonitoringToken
        }
        .Where(value => !string.IsNullOrEmpty(value))
        .Distinct(StringComparer.Ordinal)
        .Count()
        ==
        new[]
        {
            options.SourceIngestionToken,
            options.AdminCommandToken,
            options.ChatGatewayToken,
            options.MonitoringToken,
            options.CommunityLegacyChatToken,
            options.AdminEvidenceIngestionToken,
            options.GameDataLegacyReplayToken
            ,options.EconomySourceToken, options.EconomyAdminToken, options.EconomyMonitoringToken
        }.Count(value => !string.IsNullOrEmpty(value)),
        "PlayerData credentials must all be distinct.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter()));
builder.Services.AddHttpClient(nameof(HttpLegacyCommunityChatClient),
    client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient(nameof(HttpLegacyAdminEvidenceClient),
    client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddHttpClient(
    nameof(ProjectionDispatcher),
    client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient(
    nameof(HttpLegacyReplayEvidenceClient),
    client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient(nameof(HttpLegacyEconomyClient), client => client.Timeout = TimeSpan.FromSeconds(8));
builder.Services.AddSingleton<IPlayerDataStore>(provider =>
{
    var options = provider
        .GetRequiredService<IOptions<PlayerDataOptions>>()
        .Value;
    return options.PersistenceMode == "Postgres"
        ? new PostgresPlayerDataStore(
            NpgsqlDataSource.Create(
                options.PostgresConnectionString))
        : new InMemoryPlayerDataStore();
});
builder.Services.AddSingleton<ILegacyCommunityChatClient, HttpLegacyCommunityChatClient>();
builder.Services.AddSingleton<ILegacyAdminEvidenceClient, HttpLegacyAdminEvidenceClient>();
builder.Services.AddSingleton<ILegacyReplayEvidenceClient, HttpLegacyReplayEvidenceClient>();
builder.Services.AddSingleton<ILegacyEconomyClient, HttpLegacyEconomyClient>();
builder.Services.AddSingleton<ProjectionDispatcher>();
builder.Services.AddHostedService<PlayerDataStoreInitializer>();
builder.Services.AddHostedService<ProjectionDispatcherService>();

var app = builder.Build();
app.UseMahjongObservability(
    "GuiyangMahjong.PlayerData",
    app.Environment.EnvironmentName);
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    try
    {
        await next(context);
    }
    catch (PlayerDataOperationException exception)
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
app.MapPlayerDataEndpoints();
app.Run();

/// <summary>
/// WebApplicationFactory 集成测试可发现的 PlayerData 程序入口标记。
/// 运行时依赖注册和中间件顺序由上方顶级语句定义，该 partial 类型不保存请求状态。
/// </summary>
public partial class Program;
