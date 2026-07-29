using GuiyangMahjong.Lobby.Api;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.Lobby");

builder.Services
    .AddOptions<LobbyOptions>()
    .Bind(builder.Configuration.GetSection(LobbyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.JoinTicketSigningKey.Length >= 32,
        "Lobby:JoinTicketSigningKey must contain at least 32 characters.")
    .Validate(options => options.InternalServiceToken.Length >= 32,
        "Lobby:InternalServiceToken must contain at least 32 characters.")
    .Validate(options => string.IsNullOrEmpty(options.MonitoringReadOnlyToken)
                         || options.MonitoringReadOnlyToken.Length >= 32,
        "Lobby:MonitoringReadOnlyToken must be empty or contain at least 32 characters.")
    .Validate(options => string.IsNullOrEmpty(options.ManagementCommandToken)
                         || options.ManagementCommandToken.Length >= 32,
        "Lobby:ManagementCommandToken must be empty or contain at least 32 characters.")
    .Validate(options => string.IsNullOrEmpty(options.ManagementCommandToken)
                         || (!string.Equals(
                                 options.ManagementCommandToken,
                                 options.MonitoringReadOnlyToken,
                                 StringComparison.Ordinal)
                             && !string.Equals(
                                 options.ManagementCommandToken,
                                 options.InternalServiceToken,
                                 StringComparison.Ordinal)),
        "Lobby management credential must be dedicated.")
    .Validate(options => !options.Allocator.Enabled || options.Allocator.ServiceToken.Length >= 32,
        "Lobby:Allocator:ServiceToken must contain at least 32 characters when enabled.")
    .Validate(options => !options.TopologyRegistration.Enabled
            || (options.TopologyRegistration.RegistrationToken.Length >= 32
                && !string.Equals(
                    options.TopologyRegistration.RegistrationToken,
                    options.MonitoringReadOnlyToken,
                    StringComparison.Ordinal)
                && !string.Equals(
                    options.TopologyRegistration.RegistrationToken,
                    options.ManagementCommandToken,
                    StringComparison.Ordinal)),
        "Lobby topology registration requires a dedicated 32+ character credential.")
    .Validate(options => options.TokenSigningKey.Length >= 32, "Lobby:TokenSigningKey 至少需要 32 个字符")
    .Validate(options =>
        !builder.Environment.IsProduction()
        || !options.TokenSigningKey.StartsWith("development-only", StringComparison.OrdinalIgnoreCase),
        "生产环境禁止使用 development-only 签名密钥")
    .Validate(options =>
        !options.Persistence.Mode.Equals("RedisPostgres", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(options.Persistence.RedisConnectionString)
            && !string.IsNullOrWhiteSpace(options.Persistence.PostgresConnectionString)),
        "RedisPostgres 模式必须配置 Redis 与 PostgreSQL 连接字符串")
    .Validate(options => !builder.Environment.IsProduction()
                         || !options.Persistence.ApplyDatabaseMigrations,
        "Production Lobby runtime must not execute database migrations.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPlayerTokenValidator, HmacPlayerTokenValidator>();
builder.Services.AddSingleton<IRoomPasswordService, RoomPasswordService>();
builder.Services.AddSingleton<IJoinTicketIssuer, HmacJoinTicketIssuer>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IAllocatorClient>(provider =>
    provider.GetRequiredService<IOptions<LobbyOptions>>().Value.Allocator.Enabled
        ? ActivatorUtilities.CreateInstance<HttpAllocatorClient>(provider)
        : new DisabledAllocatorClient());
builder.Services.AddSingleton<LobbyPersistenceConnections>();
builder.Services.AddSingleton<IOnlinePresenceService>(provider =>
    provider.GetRequiredService<IOptions<LobbyOptions>>().Value.Persistence.Mode
        .Equals("RedisPostgres", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<RedisOnlinePresenceService>(provider)
        : ActivatorUtilities.CreateInstance<InMemoryOnlinePresenceService>(provider));
builder.Services.AddSingleton<IPlayerAccessRevocationStore>(provider =>
    provider.GetRequiredService<IOptions<LobbyOptions>>().Value.Persistence.Mode
        .Equals("RedisPostgres", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<RedisPlayerAccessRevocationStore>(provider)
        : ActivatorUtilities.CreateInstance<InMemoryPlayerAccessRevocationStore>(provider));
builder.Services.AddSingleton<IIdempotencyStore>(provider =>
    provider.GetRequiredService<IOptions<LobbyOptions>>().Value.Persistence.Mode
        .Equals("RedisPostgres", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<RedisIdempotencyStore>(provider)
        : ActivatorUtilities.CreateInstance<InMemoryIdempotencyStore>(provider));
builder.Services.AddSingleton<LobbyEventHub>();
builder.Services.AddSingleton<ILobbyEventPublisher>(provider => provider.GetRequiredService<LobbyEventHub>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<LobbyEventHub>());
builder.Services.AddSingleton<LobbyService>();
builder.Services.AddSingleton<ILobbyStore>(provider =>
{
    var lobbyOptions = provider.GetRequiredService<IOptions<LobbyOptions>>();
    var persistence = lobbyOptions.Value.Persistence;
    if (!persistence.Mode.Equals("RedisPostgres", StringComparison.OrdinalIgnoreCase))
    {
        return new InMemoryLobbyStore();
    }

    return ActivatorUtilities.CreateInstance<RedisPostgresLobbyStore>(provider);
});
builder.Services.AddSingleton<IRoomMonitoringStore>(provider =>
    provider.GetRequiredService<IOptions<LobbyOptions>>().Value.Persistence.Mode
        .Equals("RedisPostgres", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<RedisRoomMonitoringStore>(provider)
        : new InMemoryRoomMonitoringStore());
builder.Services.AddSingleton<IPlayerHistoryStore>(provider =>
    provider.GetRequiredService<IOptions<LobbyOptions>>().Value.Persistence.Mode
        .Equals("RedisPostgres", StringComparison.OrdinalIgnoreCase)
        ? ActivatorUtilities.CreateInstance<PostgresPlayerHistoryStore>(provider)
        : new InMemoryPlayerHistoryStore());
builder.Services.AddHostedService<LobbyStoreInitializer>();
builder.Services.AddHostedService<LobbyTopologyRegistrationService>();

var app = builder.Build();
app.UseMahjongObservability(
    "GuiyangMahjong.Lobby",
    app.Environment.EnvironmentName);

if (app.Services.GetRequiredService<IOptions<LobbyOptions>>().Value.EnableHttpsRedirection)
    app.UseHttpsRedirection();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<LobbyExceptionMiddleware>();
app.UseMiddleware<PlayerAuthenticationMiddleware>();
app.MapLobbyEndpoints();

app.Run();

public partial class Program;
