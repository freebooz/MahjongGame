using System.Net.Http.Headers;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class LobbyWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "test-only-guiyang-lobby-signing-key-which-is-long-enough";
    public const string MonitoringToken = "test-only-lobby-monitoring-token-which-is-long-enough";
    public const string ManagementToken = "test-only-lobby-management-token-which-is-long-enough";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lobby:TokenSigningKey"] = SigningKey,
                ["Lobby:JoinTicketSigningKey"] = "test-only-join-ticket-signing-key-which-is-long-enough",
                ["Lobby:InternalServiceToken"] = "test-only-internal-service-token-which-is-long-enough",
                ["Lobby:MonitoringReadOnlyToken"] = MonitoringToken,
                ["Lobby:ManagementCommandToken"] = ManagementToken,
                ["Lobby:Allocator:Enabled"] = "false",
                ["Lobby:Persistence:Mode"] = "InMemory",
                ["Lobby:PasswordFailureLimit"] = "5",
                ["Lobby:PasswordFailureWindowSeconds"] = "300"
            });
        });
    }

    public HttpClient CreateAuthenticatedClient(
        string playerId,
        string displayName = "自动化玩家",
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? issuedAtUtc = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var token = HmacPlayerTokenValidator.CreateSignedToken(
            SigningKey,
            new PlayerIdentity(playerId, displayName, "Guest"),
            expiresAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(10),
            issuedAtUtc);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static void AddRequestHeaders(HttpRequestMessage request, string? idempotencyKey = null)
    {
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
    }
}

public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset current = utcNow;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan duration) => current += duration;
}
