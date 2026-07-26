extern alias lobby;

using System.Net;
using System.Net.Http.Json;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using LobbyDomain = lobby::GuiyangMahjong.Lobby.Domain;
using LobbyOptions = lobby::GuiyangMahjong.Lobby.Options;
using LobbySecurity = lobby::GuiyangMahjong.Lobby.Security;

namespace GuiyangMahjong.Auth.Tests;

public sealed class AuthApiTests(AuthWebApplicationFactory factory)
    : IClassFixture<AuthWebApplicationFactory>
{
    [Fact]
    public async Task Readiness_ChecksConfiguredIdentityStore()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task GuestLogin_IssuesTokenAcceptedByLobbyValidator()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/v1/auth/guest",
            new GuestLoginRequest("test-installation-00000001", "测试玩家"));
        var session = await response.Content.ReadFromJsonAsync<AuthSessionResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(session);
        var validator = new LobbySecurity.HmacPlayerTokenValidator(
            Microsoft.Extensions.Options.Options.Create(
                new LobbyOptions.LobbyOptions { TokenSigningKey = AuthWebApplicationFactory.SigningKey }),
            TimeProvider.System);
        var validation = validator.Validate(session.AccessToken);
        Assert.True(validation.IsValid);
        Assert.Equal(session.PlayerId, validation.Player?.PlayerId);
        Assert.Equal("Guest", validation.Player?.Provider);
    }

    [Fact]
    public async Task RefreshToken_IsRotatedAndCannotBeReused()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "test-installation-00000002");
        var firstRefresh = await client.PostAsJsonAsync(
            "/v1/auth/refresh", new RefreshSessionRequest(login.RefreshToken));
        var rotated = await firstRefresh.Content.ReadFromJsonAsync<AuthSessionResponse>();

        Assert.Equal(HttpStatusCode.OK, firstRefresh.StatusCode);
        Assert.NotNull(rotated);
        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        var replay = await client.PostAsJsonAsync(
            "/v1/auth/refresh", new RefreshSessionRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task SameInstallation_KeepsStableServerOwnedPlayerId()
    {
        using var client = factory.CreateClient();
        var first = await LoginAsync(client, "test-installation-stable-01");
        var second = await LoginAsync(client, "test-installation-stable-01");
        Assert.Equal(first.PlayerId, second.PlayerId);
    }

    [Fact]
    public async Task GuestLogin_WithoutName_ReturnsStableLocalPlayerName()
    {
        using var client = factory.CreateClient();
        var first = await LoginAsync(client, "test-installation-local-name-01");
        var second = await LoginAsync(client, "test-installation-local-name-01");

        Assert.Equal(first.DisplayName, second.DisplayName);
        Assert.Matches(
            "^(甲秀|黔灵|南明|青岩|花溪|筑城|云岩|观山|苗岭|黔中)"
            + "(乐|豪|灵|稳|闲|喜|巧|爽|福|旺)"
            + "(雀友|牌友|雀神|鸡客|听侠|杠花|满堂|好手|庄家|摸客)$",
            first.DisplayName);
        Assert.DoesNotMatch("[0-9]", first.DisplayName);
        Assert.InRange(first.DisplayName.Length, 2, 5);
        Assert.Equal(1_000, LocalPlayerNameGenerator.CandidateCount);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, "test-installation-logout-01");
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/v1/auth/logout", new LogoutRequest(login.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(
                "/v1/auth/refresh", new RefreshSessionRequest(login.RefreshToken))).StatusCode);
    }

    private static async Task<AuthSessionResponse> LoginAsync(HttpClient client, string installationId)
    {
        var response = await client.PostAsJsonAsync(
            "/v1/auth/guest", new GuestLoginRequest(installationId, null));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthSessionResponse>()
               ?? throw new InvalidDataException("Auth response was empty.");
    }
}

public sealed class AuthWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "test-only-auth-token-signing-key-which-is-long-enough";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:TokenSigningKey"] = SigningKey,
                ["Auth:GuestIdentityPepper"] = "test-only-guest-identity-pepper-which-is-long-enough",
                ["Auth:PersistenceMode"] = "InMemory"
            }));
    }
}
