using System.Text;
using System.Text.Json.Serialization;
using GuiyangMahjong.Admin.Api;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var enterpriseIdentity = builder.Configuration
    .GetSection($"{AdminOptions.SectionName}:EnterpriseIdentity")
    .Get<EnterpriseIdentityOptions>()
    ?? new EnterpriseIdentityOptions();

builder.Services
    .AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection(AdminOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.ReadOnlyAccessToken.Length >= 32,
        "Admin:ReadOnlyAccessToken must contain at least 32 characters.")
    .Validate(options => string.IsNullOrEmpty(options.EvidenceIngestionToken)
        || options.EvidenceIngestionToken.Length >= 32,
        "Admin:EvidenceIngestionToken must be empty or contain at least 32 characters.")
    .Validate(options => !builder.Environment.IsProduction()
        || options.EvidenceIngestionToken.Length >= 32,
        "Production player evidence ingestion requires a dedicated 32+ character token.")
    .Validate(options => options.Principals.All(principal =>
            principal.AccessToken.Length >= 32
            && principal.Roles.All(AdminRoles.Known.Contains)),
        "Every Admin principal must use a 32+ character token and known roles.")
    .Validate(options => !options.EnterpriseIdentity.Enabled
        || (Uri.TryCreate(
                options.EnterpriseIdentity.Authority,
                UriKind.Absolute,
                out var authority)
            && (!options.EnterpriseIdentity.RequireHttpsMetadata
                || authority.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(
                options.EnterpriseIdentity.Audience)),
        "Enterprise identity requires a valid authority and audience.")
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
    .Validate(options => string.IsNullOrEmpty(options.EvidenceIngestionToken)
        || (!string.Equals(
                options.EvidenceIngestionToken,
                options.ReadOnlyAccessToken,
                StringComparison.Ordinal)
            && options.Principals.All(principal => !string.Equals(
                principal.AccessToken,
                options.EvidenceIngestionToken,
                StringComparison.Ordinal))
            && !string.Equals(
                options.EvidenceIngestionToken,
                options.Auth.MonitoringToken,
                StringComparison.Ordinal)
            && !string.Equals(
                options.EvidenceIngestionToken,
                options.Lobby.MonitoringToken,
                StringComparison.Ordinal)
            && options.Allocators.All(source => !string.Equals(
                source.MonitoringToken,
                options.EvidenceIngestionToken,
                StringComparison.Ordinal))),
        "Player evidence ingestion credentials must differ from administrator and monitoring credentials.")
    .Validate(options => !options.Management.Enabled
        || options.EnterpriseIdentity.Enabled
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
            && options.Wallet.Enabled
            && options.Wallet.CommandToken.Length >= 32
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
    .Validate(options => string.IsNullOrEmpty(options.Wallet.CommandToken)
        || (!string.Equals(
                options.Wallet.CommandToken,
                options.ReadOnlyAccessToken,
                StringComparison.Ordinal)
            && !string.Equals(
                options.Wallet.CommandToken,
                options.EvidenceIngestionToken,
                StringComparison.Ordinal)
            && !string.Equals(
                options.Wallet.CommandToken,
                options.Auth.MonitoringToken,
                StringComparison.Ordinal)
            && !string.Equals(
                options.Wallet.CommandToken,
                options.Lobby.MonitoringToken,
                StringComparison.Ordinal)),
        "Wallet command credential must differ from Admin and monitoring credentials.")
    .Validate(options => string.IsNullOrEmpty(
            options.AuditArchive.AppendToken)
        || (!string.Equals(
                options.AuditArchive.AppendToken,
                options.ReadOnlyAccessToken,
                StringComparison.Ordinal)
            && !string.Equals(
                options.AuditArchive.AppendToken,
                options.EvidenceIngestionToken,
                StringComparison.Ordinal)
            && !string.Equals(
                options.AuditArchive.AppendToken,
                options.Wallet.CommandToken,
                StringComparison.Ordinal)
            && options.Principals.All(principal => !string.Equals(
                principal.AccessToken,
                options.AuditArchive.AppendToken,
                StringComparison.Ordinal))),
        "Audit archive credential must be dedicated.")
    .Validate(options => !builder.Environment.IsProduction()
        || !options.Management.Enabled
        || options.EnterpriseIdentity.Enabled,
        "Production management requires enterprise OIDC identity.")
    .Validate(options => !builder.Environment.IsProduction()
        || !options.Management.ExecutionEnabled
        || (options.AuditArchive.Enabled
            && Uri.TryCreate(
                options.AuditArchive.AppendUrl,
                UriKind.Absolute,
                out var archiveUri)
            && archiveUri.Scheme == Uri.UriSchemeHttps
            && options.AuditArchive.AppendToken.Length >= 32),
        "Production command execution requires HTTPS immutable audit archival.")
    .Validate(options => !options.Auth.Enabled || options.Auth.MonitoringToken.Length >= 32,
        "Admin:Auth:MonitoringToken must contain at least 32 characters when enabled.")
    .Validate(options => !options.Lobby.Enabled || options.Lobby.MonitoringToken.Length >= 32,
        "Admin:Lobby:MonitoringToken must contain at least 32 characters when enabled.")
    .Validate(options => options.Allocators.All(source =>
            !source.Enabled || source.MonitoringToken.Length >= 32),
        "Every Admin:Allocators monitoring token must contain at least 32 characters.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = enterpriseIdentity.Authority;
        options.Audience = enterpriseIdentity.Audience;
        options.RequireHttpsMetadata =
            enterpriseIdentity.RequireHttpsMetadata;
        options.MapInboundClaims = false;
    });
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient();
builder.Services.AddHttpClient(
    nameof(AuditArchiveDispatcher),
    (provider, client) =>
    {
        var options = provider
            .GetRequiredService<IOptions<AdminOptions>>()
            .Value.AuditArchive;
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    });
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
builder.Services.AddSingleton<IAdminCaseStore>(provider =>
{
    var management = provider.GetRequiredService<IOptions<AdminOptions>>().Value.Management;
    return management.PersistenceMode == "Postgres"
        ? new PostgresAdminCaseStore(
            NpgsqlDataSource.Create(management.PostgresConnectionString))
        : new InMemoryAdminCaseStore();
});
builder.Services.AddSingleton<IPlayerAssetOperationStore>(provider =>
{
    var management = provider.GetRequiredService<IOptions<AdminOptions>>().Value.Management;
    return management.PersistenceMode == "Postgres"
        ? new PostgresPlayerAssetOperationStore(
            NpgsqlDataSource.Create(management.PostgresConnectionString))
        : new InMemoryPlayerAssetOperationStore();
});
builder.Services.AddSingleton<IPlayerEvidenceStore>(provider =>
{
    var management = provider.GetRequiredService<IOptions<AdminOptions>>().Value.Management;
    return management.PersistenceMode == "Postgres"
        ? new PostgresPlayerEvidenceStore(
            NpgsqlDataSource.Create(management.PostgresConnectionString))
        : new InMemoryPlayerEvidenceStore();
});
builder.Services.AddSingleton<IAuditArchiveOutboxStore>(provider =>
{
    var management = provider.GetRequiredService<IOptions<AdminOptions>>()
        .Value.Management;
    return management.PersistenceMode == "Postgres"
        ? new PostgresAuditArchiveOutboxStore(
            NpgsqlDataSource.Create(
                management.PostgresConnectionString))
        : new InMemoryAuditArchiveOutboxStore();
});
builder.Services.AddHostedService<AdminActionStoreInitializer>();
builder.Services.AddSingleton<AdminActionWorkflow>();
builder.Services.AddSingleton<IAdminCommandExecutor, HttpAdminCommandExecutor>();
builder.Services.AddSingleton<AdminCommandDispatcher>();
builder.Services.AddHostedService<AdminCommandDispatcherService>();
builder.Services.AddSingleton<AuditArchiveDispatcher>();
builder.Services.AddHostedService<AuditArchiveDispatcherService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
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
app.MapPlayerEvidenceEndpoints();
app.Run();

public partial class Program;
