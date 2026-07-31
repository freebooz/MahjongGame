using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using GuiyangMahjong.Admin.Api;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using GuiyangMahjong.Admin.TrustSafety;
using GuiyangMahjong.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Npgsql;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.Admin");
var enterpriseIdentity = builder.Configuration
    .GetSection($"{AdminOptions.SectionName}:EnterpriseIdentity")
    .Get<EnterpriseIdentityOptions>()
    ?? new EnterpriseIdentityOptions();

builder.Services
    .AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection(AdminOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.EnterpriseIdentity.Enabled
        || options.ReadOnlyAccessToken.Length >= 32,
        "Local Admin mode requires a 32+ character read-only token.")
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
    .Validate(options => !options.EnterpriseIdentity.Enabled
        || options.EnterpriseIdentity.MaxTokenAgeMinutes
            <= options.EnterpriseIdentity.RevocationSlaMinutes,
        "Enterprise token age must not exceed the identity revocation SLA.")
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
    .Validate(options => !builder.Environment.IsProduction()
        || !options.Management.ApplyDatabaseMigrations,
        "Production Admin runtime must not execute database migrations.")
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
        || (options.EnterpriseIdentity.Enabled
            && options.EnterpriseIdentity.RequireMfa
            && options.EnterpriseIdentity.RequireHttpsMetadata
            && options.EnterpriseIdentity.MaxTokenAgeMinutes <= 10
            && options.EnterpriseIdentity.RevocationSlaMinutes <= 10
            && options.Principals.Length == 0
            && string.IsNullOrEmpty(options.ReadOnlyAccessToken)
            && options.WebSecurity.RequireHttps),
        "Production Admin requires HTTPS enterprise OIDC, MFA, <=10 minute sessions, and no local shared tokens.")
    .Validate(options => options.WebSecurity.SessionLifetimeMinutes
            <= options.EnterpriseIdentity.RevocationSlaMinutes,
        "Administrator browser session lifetime must not exceed the enterprise identity revocation SLA.")
    .Validate(options => !builder.Environment.IsProduction()
            || !options.WebSecurity.BrowserSessionEnabled
            || (options.WebSecurity.RequireHttps
                && options.WebSecurity.SessionCookieName.StartsWith("__Host-", StringComparison.Ordinal)
                && options.WebSecurity.BindDevice
                && options.WebSecurity.BindIpNetwork
                && options.Management.PersistenceMode == "Postgres"),
        "Production browser sessions require __Host- secure cookies, device/IP binding, and PostgreSQL persistence.")
    .Validate(options => !builder.Environment.IsProduction()
        || !options.AuditArchive.Enabled
        || (!string.IsNullOrWhiteSpace(
                options.AuditArchive.PostgresConnectionString)
            && !string.Equals(
                options.AuditArchive.PostgresConnectionString,
                options.Management.PostgresConnectionString,
                StringComparison.Ordinal)),
        "Production audit archive requires a dedicated PostgreSQL identity.")
    .Validate(options => !options.AuditArchive.AnchorEnabled
        || (options.AuditArchive.Enabled
            && options.AuditArchive.AppendToken.Length >= 32
            && Uri.TryCreate(
                options.AuditArchive.AnchorUrl,
                UriKind.Absolute,
                out var anchorUri)
            && (!builder.Environment.IsProduction()
                || anchorUri.Scheme == Uri.UriSchemeHttps)),
        "Audit chain anchoring requires an enabled archive and an HTTPS production anchor URL.")
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
    .Validate(options => !options.CentralLogs.Enabled
            || options.CentralLogs.QueryToken.Length >= 32,
        "Enabled central log queries require a dedicated 32+ character read-only token.")
    .Validate(options => !options.ChatArchive.Enabled
            || options.ChatArchive.QueryToken.Length >= 32,
        "Enabled chat archive queries require a dedicated 32+ character read-only token.")
    .Validate(options => !options.ChatArchive.Enabled
            || !string.Equals(
                options.ChatArchive.QueryToken,
                options.CentralLogs.QueryToken,
                StringComparison.Ordinal),
        "Chat archive and central log gateways must use different credentials.")
    .Validate(options => !options.ReplayArchive.Enabled
            || (options.ReplayArchive.ReadToken.Length >= 32
                && options.ReplayArchive.SigningKey.Length >= 32),
        "Enabled replay access requires dedicated 32+ character read and signing credentials.")
    .Validate(options => !options.TopologyDiscovery.Enabled
            || (options.TopologyDiscovery.RegistrationToken.Length >= 32
                && options.TopologyDiscovery.LobbyMonitoringToken.Length >= 32
                && options.TopologyDiscovery.AllocatorMonitoringToken.Length >= 32),
        "Enabled topology discovery requires dedicated 32+ character registration and monitoring credentials.")
    .Validate(options => !builder.Environment.IsProduction()
            || options.Abac.Enabled,
        "Production Admin requires ABAC governance policies.")
    .Validate(options => !options.Abac.Enabled
            || options.EnterpriseIdentity.Enabled
            || options.Principals.All(principal =>
                principal.Regions.Length > 0
                && !string.IsNullOrWhiteSpace(principal.ShiftId)),
        "ABAC-enabled local principals require explicit region and shift attributes.")
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
        // 管理端不缓存原始 Bearer Token，避免令牌被后续组件或日志意外持久化。
        options.SaveToken = false;
    });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            code = "ADMIN_RATE_LIMITED",
            message = "请求频率超过当前管理操作类别的安全上限。",
            traceId = context.HttpContext.TraceIdentifier
        }, cancellationToken);
    };
    // 按操作者和请求类别隔离额度，防止批量导出或证据查询挤占常规监控刷新。
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        context =>
        {
            if (!context.Request.Path.StartsWithSegments("/admin/v1")
                && !context.Request.Path.StartsWithSegments("/admin/operations/v1")
                && !context.Request.Path.StartsWithSegments("/admin/bff/v1"))
            {
                return RateLimitPartition.GetNoLimiter("non-admin");
            }

            var settings = context.RequestServices
                .GetRequiredService<IOptions<AdminOptions>>().Value.WebSecurity;
            var category = ClassifyAdminRequest(context.Request);
            var operatorKey = context.User.FindFirst(
                    enterpriseIdentity.OperatorIdClaim)?.Value
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown";
            var (limit, window) = category switch
            {
                "export" => (
                    settings.ExportRequestsPerTenMinutes,
                    TimeSpan.FromMinutes(10)),
                "evidence" => (
                    settings.EvidenceRequestsPerMinute,
                    TimeSpan.FromMinutes(1)),
                "search" => (
                    settings.SearchRequestsPerMinute,
                    TimeSpan.FromMinutes(1)),
                _ => (
                    settings.ReadRequestsPerMinute,
                    TimeSpan.FromMinutes(1))
            };
            return RateLimitPartition.GetFixedWindowLimiter(
                $"{operatorKey}:{category}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = window,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
        });
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // 只接受明确列出的代理地址，防止客户端伪造 X-Forwarded-Proto 绕过 HTTPS 门禁。
    foreach (var address in builder.Configuration.GetSection(
                 $"{AdminOptions.SectionName}:WebSecurity:TrustedProxyAddresses")
             .Get<string[]>() ?? [])
    {
        if (System.Net.IPAddress.TryParse(address, out var parsed))
            options.KnownProxies.Add(parsed);
    }
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ConfigurationManagementClient>();
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
builder.Services.AddSingleton<ICentralLogQueryClient, LokiCentralLogQueryClient>();
builder.Services.AddSingleton<IChatArchiveQueryClient, HttpChatArchiveQueryClient>();
builder.Services.AddSingleton<IReplayArchiveClient, HttpReplayArchiveClient>();
builder.Services.AddSingleton<TopologyRegistry>();
builder.Services.AddSingleton<AdminAbacPolicyService>();
builder.Services.AddHostedService<AuditChainAnchorService>();
builder.Services.AddSingleton<IPlayerDirectoryClient, HttpPlayerDirectoryClient>();
builder.Services.AddSingleton<MonitoringSourceReliabilityService>();
builder.Services.AddSingleton<MonitoringAggregationService>();
builder.Services.AddSingleton<PlayerMonitoringService>();
builder.Services.AddSingleton<AdminDataRedactionService>();
builder.Services.AddSingleton<TrustSafetyReadModelService>();
builder.Services.AddSingleton<AdminRealtimeEventHub>();
builder.Services.AddHostedService<AdminRealtimeSnapshotPublisher>();
builder.Services.AddSingleton<IAdminBrowserSessionStore>(provider =>
{
    var settings = provider.GetRequiredService<IOptions<AdminOptions>>().Value.Management;
    return settings.PersistenceMode == "Postgres"
        ? new PostgresAdminBrowserSessionStore(NpgsqlDataSource.Create(settings.PostgresConnectionString))
        : new InMemoryAdminBrowserSessionStore();
});
builder.Services.AddSingleton<AdminBrowserSessionService>();
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
                string.IsNullOrWhiteSpace(
                    provider.GetRequiredService<IOptions<AdminOptions>>()
                        .Value.AuditArchive.PostgresConnectionString)
                    ? management.PostgresConnectionString
                    : provider.GetRequiredService<IOptions<AdminOptions>>()
                        .Value.AuditArchive.PostgresConnectionString))
        : new InMemoryAuditArchiveOutboxStore();
});
builder.Services.AddHostedService<AdminActionStoreInitializer>();
// 本地允许迁移时先由动作存储应用完整 Schema，再验证浏览器会话表；生产两者都只做就绪校验。
builder.Services.AddHostedService<AdminBrowserSessionStoreInitializer>();
builder.Services.AddSingleton<AdminActionWorkflow>();
builder.Services.AddSingleton<IAdminCommandExecutor, HttpAdminCommandExecutor>();
builder.Services.AddSingleton<AdminCommandDispatcher>();
builder.Services.AddHostedService<AdminCommandDispatcherService>();
builder.Services.AddSingleton<AuditArchiveDispatcher>();
builder.Services.AddHostedService<AuditArchiveDispatcherService>();

var app = builder.Build();
app.UseMahjongObservability(
    "GuiyangMahjong.Admin",
    app.Environment.EnvironmentName);
var webSecurity = app.Services.GetRequiredService<IOptions<AdminOptions>>()
    .Value.WebSecurity;
app.UseForwardedHeaders();
if (webSecurity.RequireHttps)
{
    // 生产反向代理必须转发原始 HTTPS 协议；HSTS 只在受控 HTTPS 入口启用。
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseDefaultFiles();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; " +
        "img-src 'self' data:; connect-src 'self' wss:; object-src 'none'; " +
        "base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["Permissions-Policy"] =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    if (context.Request.Path.StartsWithSegments("/admin/v1")
        || context.Request.Path.StartsWithSegments("/admin/operations/v1")
        || context.Request.Path.StartsWithSegments("/admin/bff/v1"))
        context.Response.Headers.CacheControl = "no-store";
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
    catch (MonitoringFreshDataRequiredException exception)
    {
        // 高危管理操作读取到缓存或降级状态时统一返回冲突，强制操作员刷新并等待权威来源恢复。
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "ADMIN_FRESH_STATE_REQUIRED",
            message = exception.Message,
            traceId = context.TraceIdentifier
        }, cancellationToken: context.RequestAborted);
    }
    catch (MonitoringSourceUnavailableException exception)
    {
        // 区分“对象不存在”和“主数据来源不可用”，避免故障期间产生误判或错误处置。
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "ADMIN_MONITORING_SOURCE_UNAVAILABLE",
            message = exception.Message,
            source = exception.SourceName,
            traceId = context.TraceIdentifier
        }, cancellationToken: context.RequestAborted);
    }
    catch (CentralLogQueryUnavailableException exception)
    {
        // 日志平台故障时拒绝生成不完整导出，避免审批人员误以为快照就是完整证据。
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "ADMIN_CENTRAL_LOG_UNAVAILABLE",
            message = exception.Message,
            traceId = context.TraceIdentifier
        }, cancellationToken: context.RequestAborted);
    }
    catch (ChatArchiveUnavailableException exception)
    {
        // 合规归档不可用时默认拒绝，不以空结果掩盖证据来源故障。
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "ADMIN_CHAT_ARCHIVE_UNAVAILABLE",
            message = exception.Message,
            traceId = context.TraceIdentifier
        }, cancellationToken: context.RequestAborted);
    }
    catch (ReplayArchiveUnavailableException exception)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "ADMIN_REPLAY_ARCHIVE_UNAVAILABLE",
            message = exception.Message,
            traceId = context.TraceIdentifier
        }, cancellationToken: context.RequestAborted);
    }
    catch (InvalidMonitoringCursorException exception)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "INVALID_CURSOR",
            message = exception.Message,
            traceId = context.TraceIdentifier
        }, cancellationToken: context.RequestAborted);
    }
});
// 安全响应头必须包裹静态 Angular 入口，确保 index.html 与 API 使用同一套严格 CSP。
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<AdminAuthenticationMiddleware>();
app.MapAdminBffEndpoints();
app.MapAdminEndpoints();
app.MapTrustSafetyEndpoints();
app.MapPlayerEvidenceEndpoints();
app.Run();

// 将管理请求分为独立限流桶。导出和敏感证据优先匹配，搜索其次，
// 避免低成本读取掩盖高风险批量行为。
static string ClassifyAdminRequest(HttpRequest request)
{
    var path = request.Path.Value ?? string.Empty;
    if (path.Contains("/export", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/logs", StringComparison.OrdinalIgnoreCase))
        return "export";
    if (path.Contains("/evidence", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/chat", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/replays", StringComparison.OrdinalIgnoreCase))
        return "evidence";
    if (request.Query.ContainsKey("search")
        || request.Query.ContainsKey("ticketId")
        || request.Query.ContainsKey("caseId"))
        return "search";
    return "read";
}

/// <summary>
/// WebApplicationFactory 集成测试可发现的 Admin 程序入口标记。
/// 运行时注册和安全中间件顺序由上方顶级语句定义，该 partial 类型不保存管理状态。
/// </summary>
public partial class Program;
