extern alias lobby;

using System.Net;
using System.Net.Http.Headers;
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

    [Fact]
    public async Task PlayerMonitoring_RequiresDedicatedReadOnlyCredential()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/internal/monitoring/players");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlayerMonitoring_ReturnsMaskedLoginAndNeverReturnsCredentials()
    {
        const string installationId = "monitoring-installation-0001";
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MahjongClient/1.2");
        var login = await LoginAsync(client, installationId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthWebApplicationFactory.MonitoringToken);

        var players = await client.GetFromJsonAsync<PlayerDirectoryItem[]>(
            $"/internal/monitoring/players?search={Uri.EscapeDataString(login.PlayerId)}");
        Assert.NotNull(players);
        var player = Assert.Single(players);
        Assert.StartsWith("device-", player.CurrentDeviceId, StringComparison.Ordinal);
        Assert.DoesNotContain(installationId, player.CurrentDeviceId, StringComparison.Ordinal);

        using var response = await client.GetAsync(
            $"/internal/monitoring/players/{Uri.EscapeDataString(login.PlayerId)}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var detail = await response.Content.ReadFromJsonAsync<PlayerDirectoryDetail>();
        Assert.NotNull(detail);
        Assert.NotEmpty(detail.Sessions);
        Assert.All(detail.Sessions, session =>
            Assert.EndsWith("…", session.SessionReference, StringComparison.Ordinal));
        Assert.Contains(detail.LoginHistory,
            item => item.ClientSummary == "MahjongClient/1.2");
        Assert.DoesNotContain(installationId, body, StringComparison.Ordinal);
        Assert.DoesNotContain(login.AccessToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(login.RefreshToken, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminSessionRevocationRequiresDedicatedCredentialAndIsIdempotent()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, $"admin-revoke-{Guid.NewGuid():N}");
        var path =
            $"/internal/admin/players/{Uri.EscapeDataString(login.PlayerId)}/sessions/revoke";
        var body = new AdminRevokePlayerSessionsRequest(
            "Security investigation forced logout",
            Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow);
        using var unauthorized = await client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AuthWebApplicationFactory.ManagementToken);
        var commandId = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", commandId);
        using var first = await client.PostAsJsonAsync(path, body);
        var firstResult =
            await first.Content.ReadFromJsonAsync<AdminRevokePlayerSessionsResult>();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(firstResult);
        Assert.True(firstResult.PlayerFound);
        Assert.Equal(1, firstResult.RevokedSessionCount);
        Assert.False(firstResult.Duplicate);

        using var duplicate = await client.PostAsJsonAsync(path, body);
        var duplicateResult =
            await duplicate.Content.ReadFromJsonAsync<AdminRevokePlayerSessionsResult>();
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.NotNull(duplicateResult);
        Assert.True(duplicateResult.Duplicate);
        Assert.Equal(firstResult.RevokedSessionCount, duplicateResult.RevokedSessionCount);

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Authorization = null;
        using var refresh = await client.PostAsJsonAsync(
            "/v1/auth/refresh",
            new RefreshSessionRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task TemporaryFreezeIsVersionedIdempotentAndBlocksLoginAndRefresh()
    {
        using var client = factory.CreateClient();
        var installationId = $"admin-freeze-{Guid.NewGuid():N}";
        var login = await LoginAsync(client, installationId);
        var path =
            $"/internal/admin/players/{Uri.EscapeDataString(login.PlayerId)}/controls";
        var effectiveAtUtc = DateTimeOffset.UtcNow;
        var body = new AdminUpdatePlayerControlRequest(
            nameof(AdminPlayerControlAction.TemporaryFreezePlayer),
            0,
            "Confirmed account takeover investigation",
            Guid.NewGuid().ToString(),
            "SEC-FREEZE-001",
            "sanction-operator",
            "player-approver",
            effectiveAtUtc,
            effectiveAtUtc.AddHours(24),
            null);
        using var unauthorized = await client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AuthWebApplicationFactory.ManagementToken);
        var commandId = Guid.NewGuid().ToString();
        client.DefaultRequestHeaders.Add("Idempotency-Key", commandId);
        using var applied = await client.PostAsJsonAsync(path, body);
        var result =
            await applied.Content.ReadFromJsonAsync<AdminUpdatePlayerControlResult>();
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.NotNull(result);
        Assert.Equal("Frozen", result.AfterState.AccountStatus);
        Assert.Equal(1, result.AfterState.Version);
        Assert.Equal(1, result.RevokedSessionCount);
        Assert.False(result.Duplicate);

        using var duplicate = await client.PostAsJsonAsync(path, body);
        var duplicateResult =
            await duplicate.Content.ReadFromJsonAsync<AdminUpdatePlayerControlResult>();
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.NotNull(duplicateResult);
        Assert.True(duplicateResult.Duplicate);
        Assert.Equal(result.RevokedSessionCount, duplicateResult.RevokedSessionCount);

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Authorization = null;
        using var refresh = await client.PostAsJsonAsync(
            "/v1/auth/refresh",
            new RefreshSessionRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        using var relogin = await client.PostAsJsonAsync(
            "/v1/auth/guest",
            new GuestLoginRequest(installationId, null));
        Assert.Equal(HttpStatusCode.Forbidden, relogin.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AuthWebApplicationFactory.MonitoringToken);
        var detail = await client.GetFromJsonAsync<PlayerDirectoryDetail>(
            $"/internal/monitoring/players/{Uri.EscapeDataString(login.PlayerId)}");
        Assert.NotNull(detail);
        Assert.Equal("Frozen", detail.Player.AccountStatus);
        Assert.Equal(1, detail.Player.ControlVersion);
        Assert.NotNull(detail.Player.FrozenUntilUtc);
        Assert.Single(detail.ControlHistory);
        Assert.Equal(commandId, detail.ControlHistory[0].CommandId);
    }

    [Fact]
    public async Task BanMuteAndRiskControlsRequireCurrentVersionAndRecordHistory()
    {
        using var client = factory.CreateClient();
        var login = await LoginAsync(client, $"admin-controls-{Guid.NewGuid():N}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AuthWebApplicationFactory.ManagementToken);
        var path =
            $"/internal/admin/players/{Uri.EscapeDataString(login.PlayerId)}/controls";
        var version = 0L;

        async Task<AdminUpdatePlayerControlResult> ApplyAsync(
            AdminPlayerControlAction action,
            DateTimeOffset? expiresAtUtc = null,
            string? riskLabel = null)
        {
            var effectiveAtUtc = DateTimeOffset.UtcNow;
            var request = new AdminUpdatePlayerControlRequest(
                action.ToString(),
                version,
                $"Approved security control for {action}",
                Guid.NewGuid().ToString(),
                $"SEC-{action}",
                action == AdminPlayerControlAction.MarkRiskAccount
                    ? "risk-analyst"
                    : "sanction-operator",
                "player-approver",
                effectiveAtUtc,
                expiresAtUtc,
                riskLabel);
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add(
                "Idempotency-Key",
                Guid.NewGuid().ToString());
            using var response = await client.PostAsJsonAsync(path, request);
            response.EnsureSuccessStatusCode();
            var result = await response.Content
                .ReadFromJsonAsync<AdminUpdatePlayerControlResult>();
            Assert.NotNull(result);
            version = result.AfterState.Version;
            return result;
        }

        var banned = await ApplyAsync(
            AdminPlayerControlAction.PermanentBanPlayer);
        Assert.Equal("Banned", banned.AfterState.AccountStatus);

        var staleRequest = new AdminUpdatePlayerControlRequest(
            nameof(AdminPlayerControlAction.LiftPlayerBan),
            0,
            "Attempt using a stale player control version",
            Guid.NewGuid().ToString(),
            "SEC-STALE",
            "sanction-operator",
            "player-approver",
            DateTimeOffset.UtcNow,
            null,
            null);
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var stale = await client.PostAsJsonAsync(path, staleRequest);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var lifted = await ApplyAsync(AdminPlayerControlAction.LiftPlayerBan);
        Assert.Equal("Active", lifted.AfterState.AccountStatus);
        var mutedAt = DateTimeOffset.UtcNow;
        var muted = await ApplyAsync(
            AdminPlayerControlAction.MutePlayer,
            mutedAt.AddHours(24));
        Assert.NotNull(muted.AfterState.MutedUntilUtc);
        var riskAt = DateTimeOffset.UtcNow;
        var risk = await ApplyAsync(
            AdminPlayerControlAction.MarkRiskAccount,
            riskAt.AddDays(30),
            "manual-review");
        Assert.Contains("manual-review", risk.AfterState.RiskLabels);
        var unmuted = await ApplyAsync(AdminPlayerControlAction.UnmutePlayer);
        Assert.Null(unmuted.AfterState.MutedUntilUtc);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AuthWebApplicationFactory.MonitoringToken);
        var detail = await client.GetFromJsonAsync<PlayerDirectoryDetail>(
            $"/internal/monitoring/players/{Uri.EscapeDataString(login.PlayerId)}");
        Assert.NotNull(detail);
        Assert.Equal(5, detail.Player.ControlVersion);
        Assert.Equal(5, detail.ControlHistory.Length);
        Assert.Contains("manual-review", detail.Player.RiskLabels);
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
    public const string MonitoringToken = "test-only-auth-monitoring-token-that-is-long-enough";
    public const string ManagementToken = "test-only-auth-management-token-that-is-long-enough";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:TokenSigningKey"] = SigningKey,
                ["Auth:GuestIdentityPepper"] = "test-only-guest-identity-pepper-which-is-long-enough",
                ["Auth:MonitoringReadOnlyToken"] = MonitoringToken,
                ["Auth:ManagementCommandToken"] = ManagementToken,
                ["Auth:PersistenceMode"] = "InMemory"
            }));
    }
}
