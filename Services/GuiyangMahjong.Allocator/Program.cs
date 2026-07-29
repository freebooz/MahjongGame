using System.Text;
using System.Net;
using GuiyangMahjong.Allocator.Api;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Security;
using GuiyangMahjong.Allocator.Services;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.AddMahjongObservability("GuiyangMahjong.Allocator");

builder.Services
    .AddOptions<AllocatorOptions>()
    .Bind(builder.Configuration.GetSection(AllocatorOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.PortEnd >= options.PortStart, "Allocator port range is invalid.")
    .Validate(options => options.Backend == AllocatorBackendMode.Agones
                         || !IPAddress.TryParse(options.AdvertisedIp, out var address)
                         || (!IPAddress.Any.Equals(address) && !IPAddress.IPv6Any.Equals(address)),
        "Allocator AdvertisedIp must not be an unspecified address.")
    .Validate(options => options.Backend == AllocatorBackendMode.Agones
                         || !string.IsNullOrWhiteSpace(options.GameServerExecutablePath),
        "Allocator GameServerExecutablePath is required for LocalProcess mode.")
    .Validate(options => options.Backend != AllocatorBackendMode.Agones
                         || (!string.IsNullOrWhiteSpace(options.Agones.Namespace)
                             && !string.IsNullOrWhiteSpace(options.Agones.FleetName)
                             && Uri.TryCreate(options.Agones.ApiServer, UriKind.Absolute, out _)),
        "Allocator Agones namespace, fleet, and API server are required in Agones mode.")
    .Validate(options => string.IsNullOrEmpty(options.MonitoringReadOnlyToken)
                         || options.MonitoringReadOnlyToken.Length >= 32,
        "Allocator:MonitoringReadOnlyToken must be empty or contain at least 32 characters.")
    .Validate(options => string.IsNullOrEmpty(options.ManagementCommandToken)
                         || options.ManagementCommandToken.Length >= 32,
        "Allocator:ManagementCommandToken must be empty or contain at least 32 characters.")
    .Validate(options => string.IsNullOrEmpty(options.ManagementCommandToken)
                         || (!string.Equals(
                                 options.ManagementCommandToken,
                                 options.MonitoringReadOnlyToken,
                                 StringComparison.Ordinal)
                             && !string.Equals(
                                 options.ManagementCommandToken,
                                 options.ServiceToken,
                                 StringComparison.Ordinal)),
        "Allocator management credential must be dedicated.")
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
        "Allocator topology registration requires a dedicated 32+ character credential.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PortLeasePool>();
builder.Services.AddSingleton<InstanceCredentialService>();
builder.Services.AddSingleton<GameServerProcessLauncher>();
builder.Services.AddSingleton<IGameServerProcessLauncher>(provider =>
    provider.GetRequiredService<GameServerProcessLauncher>());
builder.Services.AddSingleton<IAgonesAllocationClient>(provider =>
    provider.GetRequiredService<IOptions<AllocatorOptions>>().Value.Backend == AllocatorBackendMode.Agones
        ? ActivatorUtilities.CreateInstance<KubernetesAgonesAllocationClient>(provider)
        : new DisabledAgonesAllocationClient());
builder.Services.AddSingleton<IInstanceFailureNotifier, LobbyInstanceFailureNotifier>();
builder.Services.AddSingleton<IAllocatorStateStore, JsonAllocatorStateStore>();
builder.Services.AddSingleton<MatchResultOutboxRecovery>();
builder.Services.AddSingleton<GameServerInstanceManager>();
builder.Services.AddHostedService<AllocatorStateInitializer>();
builder.Services.AddHostedService<InstanceMonitorService>();
builder.Services.AddHostedService<MatchResultOutboxRecoveryService>();
builder.Services.AddHostedService<AllocatorTopologyRegistrationService>();

var app = builder.Build();
app.UseMahjongObservability(
    "GuiyangMahjong.Allocator",
    app.Environment.EnvironmentName);
app.UseMiddleware<AllocatorExceptionMiddleware>();
app.UseMiddleware<AllocatorServiceAuthenticationMiddleware>();
app.MapAllocatorEndpoints();
app.Run();

public partial class Program;
