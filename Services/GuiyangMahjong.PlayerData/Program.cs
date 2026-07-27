using System.Text;
using System.Text.Json.Serialization;
using GuiyangMahjong.PlayerData.Api;
using GuiyangMahjong.PlayerData.Options;
using GuiyangMahjong.PlayerData.Services;
using GuiyangMahjong.PlayerData.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

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
            && options.AuthMonitoringToken.Length >= 32
            && options.ProjectionEnabled
            && options.AdminEvidenceIngestionToken.Length >= 32),
        "Production PlayerData requires PostgreSQL and all dedicated credentials.")
    .Validate(options =>
        new[]
        {
            options.SourceIngestionToken,
            options.AdminCommandToken,
            options.ChatGatewayToken,
            options.MonitoringToken,
            options.AuthMonitoringToken,
            options.AdminEvidenceIngestionToken
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
            options.AuthMonitoringToken,
            options.AdminEvidenceIngestionToken
        }.Count(value => !string.IsNullOrEmpty(value)),
        "PlayerData credentials must all be distinct.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter()));
builder.Services.AddHttpClient(
    nameof(HttpChatPolicyClient),
    client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient(
    nameof(ProjectionDispatcher),
    client => client.Timeout = TimeSpan.FromSeconds(5));
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
builder.Services.AddSingleton<IChatPolicyClient, HttpChatPolicyClient>();
builder.Services.AddSingleton<ProjectionDispatcher>();
builder.Services.AddHostedService<PlayerDataStoreInitializer>();
builder.Services.AddHostedService<ProjectionDispatcherService>();

var app = builder.Build();
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

public partial class Program;
