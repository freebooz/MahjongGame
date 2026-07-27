using System.Text;
using System.Text.Json.Serialization;
using GuiyangMahjong.Admin.Api;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services
    .AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection(AdminOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.ReadOnlyAccessToken.Length >= 32,
        "Admin:ReadOnlyAccessToken must contain at least 32 characters.")
    .Validate(options => options.Principals.All(principal =>
            principal.AccessToken.Length >= 32
            && principal.Roles.All(AdminRoles.Known.Contains)),
        "Every Admin principal must use a 32+ character token and known roles.")
    .Validate(options => options.Principals
            .Select(principal => principal.OperatorId)
            .Distinct(StringComparer.Ordinal)
            .Count() == options.Principals.Length,
        "Admin principal OperatorId values must be unique.")
    .Validate(options => options.Principals
            .Select(principal => principal.AccessToken)
            .Distinct(StringComparer.Ordinal)
            .Count() == options.Principals.Length,
        "Admin principal access tokens must be unique.")
    .Validate(options => options.Principals.All(principal =>
            !string.Equals(
                principal.AccessToken,
                options.ReadOnlyAccessToken,
                StringComparison.Ordinal)),
        "Admin principal tokens must differ from the legacy read-only token.")
    .Validate(options => !options.Management.Enabled
        || options.Principals.Any(operatorPrincipal =>
            operatorPrincipal.Roles.Any(role => role is
                AdminRoles.RoomOperator
                or AdminRoles.PlayerOperator
                or AdminRoles.SanctionOperator
                or AdminRoles.RiskAnalyst
                or AdminRoles.SupportOperator
                or AdminRoles.InfrastructureOperator
                or AdminRoles.CompensationOperator)
            && options.Principals.Any(approverPrincipal =>
                approverPrincipal.OperatorId != operatorPrincipal.OperatorId
                && approverPrincipal.Roles.Any(role => role is
                    AdminRoles.RoomApprover or AdminRoles.PlayerApprover))),
        "Enabled management requires distinct operator and approver principals.")
    .Validate(options => options.Management.PersistenceMode is "InMemory" or "Postgres",
        "Admin management PersistenceMode must be InMemory or Postgres.")
    .Validate(options => options.Management.PersistenceMode != "Postgres"
        || !string.IsNullOrWhiteSpace(options.Management.PostgresConnectionString),
        "Admin management PostgreSQL connection string is required in Postgres mode.")
    .Validate(options => !builder.Environment.IsProduction()
        || !options.Management.Enabled
        || options.Management.PersistenceMode == "Postgres",
        "Production management requires PostgreSQL persistence.")
    .Validate(options => !options.Management.ExecutionEnabled
        || options.Management.Enabled,
        "Admin command execution requires management to be enabled.")
    .Validate(options => !options.Management.ExecutionEnabled
        || (options.Management.AuthCommandToken.Length >= 32
            && options.Management.LobbyCommandToken.Length >= 32
            && options.Allocators
                .Where(source => source.Enabled)
                .All(source => source.ManagementCommandToken.Length >= 32)),
        "Admin command execution requires dedicated Auth, Lobby, and Allocator command credentials.")
    .Validate(options =>
        (string.IsNullOrEmpty(options.Management.AuthCommandToken)
            || !string.Equals(
                options.Management.AuthCommandToken,
                options.Auth.MonitoringToken,
                StringComparison.Ordinal))
        && (string.IsNullOrEmpty(options.Management.LobbyCommandToken)
            || !string.Equals(
                options.Management.LobbyCommandToken,
                options.Lobby.MonitoringToken,
                StringComparison.Ordinal))
        && (string.IsNullOrEmpty(options.Management.AuthCommandToken)
            || string.IsNullOrEmpty(options.Management.LobbyCommandToken)
            || !string.Equals(
                options.Management.AuthCommandToken,
                options.Management.LobbyCommandToken,
                StringComparison.Ordinal)),
        "Admin management credentials must differ from monitoring credentials.")
    .Validate(options => options.Allocators.All(source =>
            string.IsNullOrEmpty(source.ManagementCommandToken)
            || !string.Equals(
                source.ManagementCommandToken,
                source.MonitoringToken,
                StringComparison.Ordinal)),
        "Allocator management credentials must differ from monitoring credentials.")
    .Validate(options => !builder.Environment.IsProduction()
        || !options.Management.ExecutionEnabled,
        "Production command execution is blocked until real command adapters are configured.")
    .Validate(options => !options.Auth.Enabled || options.Auth.MonitoringToken.Length >= 32,
        "Admin:Auth:MonitoringToken must contain at least 32 characters when enabled.")
    .Validate(options => !options.Lobby.Enabled || options.Lobby.MonitoringToken.Length >= 32,
        "Admin:Lobby:MonitoringToken must contain at least 32 characters when enabled.")
    .Validate(options => options.Allocators.All(source =>
            !source.Enabled || source.MonitoringToken.Length >= 32),
        "Every Admin:Allocators monitoring token must contain at least 32 characters.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ILobbyMonitoringClient, HttpLobbyMonitoringClient>();
builder.Services.AddSingleton<IAllocatorMonitoringClient, HttpAllocatorMonitoringClient>();
builder.Services.AddSingleton<IPlayerDirectoryClient, HttpPlayerDirectoryClient>();
builder.Services.AddSingleton<MonitoringAggregationService>();
builder.Services.AddSingleton<PlayerMonitoringService>();
builder.Services.AddSingleton<IAdminActionStore>(provider =>
{
    var management = provider.GetRequiredService<IOptions<AdminOptions>>().Value.Management;
    return management.PersistenceMode == "Postgres"
        ? new PostgresAdminActionStore(
            NpgsqlDataSource.Create(management.PostgresConnectionString))
        : new InMemoryAdminActionStore();
});
builder.Services.AddHostedService<AdminActionStoreInitializer>();
builder.Services.AddSingleton<AdminActionWorkflow>();
builder.Services.AddSingleton<IAdminCommandExecutor, HttpAdminCommandExecutor>();
builder.Services.AddSingleton<AdminCommandDispatcher>();
builder.Services.AddHostedService<AdminCommandDispatcherService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    try
    {
        await next(context);
    }
    catch (AdminOperationException exception)
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
app.UseMiddleware<AdminAuthenticationMiddleware>();
app.MapAdminEndpoints();
app.Run();

public partial class Program;
