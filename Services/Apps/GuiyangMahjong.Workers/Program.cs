using System.Text;
using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.BuildingBlocks.Persistence;
using GuiyangMahjong.Observability;
using GuiyangMahjong.Workers.Maintenance;
using GuiyangMahjong.Workers.Messaging;
using GuiyangMahjong.Workers.Options;
using GuiyangMahjong.Workers.Outbox;
using GuiyangMahjong.Workers.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.Workers");
builder.Services.AddOptions<WorkersOptions>()
    .Bind(builder.Configuration.GetSection(WorkersOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options =>
        Uri.TryCreate(options.NatsUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "nats" or "tls",
        "Workers:NatsUrl 必须使用 nats:// 或 tls://。")
    .Validate(options => string.IsNullOrWhiteSpace(options.NatsUsername)
                         || !string.IsNullOrWhiteSpace(options.NatsPassword),
        "配置 NATS 用户名时必须通过 Secret 同时提供密码。")
    .Validate(options =>
        !string.IsNullOrWhiteSpace(options.PostgresConnectionString),
        "Workers 必须配置自有 PostgreSQL 连接。")
    .Validate(options => options.OutboxSources.All(source =>
        !string.IsNullOrWhiteSpace(source.Name)
        && !string.IsNullOrWhiteSpace(source.ConnectionString)
        && IsSafeSchema(source.Schema)),
        "Outbox Source 必须提供名称、连接和安全 Schema。")
    .Validate(options => ValidateMaintenance(options.SessionCleanup)
                         && ValidateMaintenance(options.RoomCleanup),
        "维护任务启用时必须配置 HTTPS/集群内 HTTP 端点和 32+ 字符专用凭据。")
    .Validate(options => !builder.Environment.IsProduction()
                         || (!options.ApplyDatabaseMigrations
                             && (options.StreamReplicas == 3
                                 || options.AllowSingleNodeStream)
                             && options.OutboxSources.Count > 0),
        "生产 Workers 必须关闭运行时 DDL、使用三副本 Stream（本地 Compose 可显式放宽）并配置至少一个 Outbox Source。")
    .ValidateOnStart();

var configured = builder.Configuration
    .GetSection(WorkersOptions.SectionName)
    .Get<WorkersOptions>() ?? new WorkersOptions();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("maintenance", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton(_ =>
    NpgsqlDataSource.Create(configured.PostgresConnectionString));
builder.Services.AddSingleton<WorkerStorage>();
builder.Services.AddSingleton<OutboxSourceRegistry>();
builder.Services.AddSingleton<JetStreamRuntime>();
builder.Services.AddSingleton<IEventPublisher>(_ =>
    new NatsJetStreamEventPublisher(
        configured.NatsUrl,
        $"workers-outbox-{Environment.MachineName}",
        configured.NatsUsername,
        configured.NatsPassword));

// 初始化顺序先保证本地测试 Schema，再后台重试 NATS；业务事务不依赖 NATS 启动成功。
builder.Services.AddHostedService<WorkerSchemaInitializer>();
builder.Services.AddHostedService<JetStreamBootstrapWorker>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<ProjectionConsumersWorker>();
builder.Services.AddHostedService<MessageBacklogMonitorWorker>();
builder.Services.AddHostedService<MessagingRetentionWorker>();
builder.Services.AddHostedService<OwnershipMaintenanceWorker>();

var app = builder.Build();
app.UseMahjongObservability("GuiyangMahjong.Workers", app.Environment.EnvironmentName);
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/startup", (JetStreamRuntime runtime) =>
    runtime.Initialized
        ? Results.Ok(new { status = "started" })
        : Results.Json(
            new { status = "starting" },
            statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/health/ready", async (
    JetStreamRuntime runtime,
    WorkerStorage storage,
    CancellationToken cancellationToken) =>
{
    var databaseReady = await storage.CheckHealthAsync(cancellationToken);
    var natsReady = await runtime.CheckHealthAsync(cancellationToken);
    return databaseReady && natsReady
        ? Results.Ok(new { status = "ready" })
        : Results.Json(
            new { status = "not-ready", databaseReady, natsReady },
            statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.Run();

static bool IsSafeSchema(string schema)
{
    try
    {
        _ = new PersistenceTableNames(schema);
        return true;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static bool ValidateMaintenance(MaintenanceOptions options) =>
    !options.Enabled
    || (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && options.Token.Length >= 32);

/// <summary>WebApplicationFactory 和架构测试入口；生产行为全部通过显式依赖注册。</summary>
public partial class Program;
