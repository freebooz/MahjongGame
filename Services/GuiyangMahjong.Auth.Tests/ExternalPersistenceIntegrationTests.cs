using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Options;
using GuiyangMahjong.Auth.Security;
using GuiyangMahjong.Auth.Services;
using GuiyangMahjong.Auth.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GuiyangMahjong.Auth.Tests;

public sealed class AuthExternalPersistenceFactAttribute : FactAttribute
{
    public AuthExternalPersistenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTH_TEST_POSTGRES")))
            Skip = "Set AUTH_TEST_POSTGRES to run Auth external persistence tests.";
    }
}

public sealed class ExternalPersistenceIntegrationTests
{
    [AuthExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_RefreshRotationIsSingleUseAcrossAuthInstances()
    {
        var options = CreateOptions();
        await using var storeA = CreateStore();
        await using var storeB = CreateStore();
        await storeA.InitializeAsync(CancellationToken.None);
        var serviceA = CreateService(storeA, options);
        var serviceB = CreateService(storeB, options);
        var login = await serviceA.LoginGuestAsync(
            new GuestLoginRequest($"external-auth-{Guid.NewGuid():N}", "外部认证玩家"),
            CancellationToken.None);

        async Task<AuthSessionResponse?> AttemptRefresh(AuthService service)
        {
            try
            {
                return await service.RefreshAsync(
                    new RefreshSessionRequest(login.RefreshToken),
                    CancellationToken.None);
            }
            catch (AuthOperationException exception)
            {
                Assert.Equal("SESSION_EXPIRED", exception.Code);
                Assert.Equal(401, exception.StatusCode);
                return null;
            }
        }

        var attempts = await Task.WhenAll(
            AttemptRefresh(serviceA),
            AttemptRefresh(serviceB));
        var rotated = Assert.Single(attempts, result => result is not null)!;
        Assert.Single(attempts, result => result is null);
        await Assert.ThrowsAsync<AuthOperationException>(() => serviceA.RefreshAsync(
            new RefreshSessionRequest(login.RefreshToken), CancellationToken.None));

        await serviceB.LogoutAsync(
            new LogoutRequest(rotated.RefreshToken), CancellationToken.None);
        await Assert.ThrowsAsync<AuthOperationException>(() => serviceA.RefreshAsync(
            new RefreshSessionRequest(rotated.RefreshToken), CancellationToken.None));

        await using var inspector = NpgsqlDataSource.Create(ConnectionString);
        await using var command = inspector.CreateCommand(
            """
            SELECT COUNT(*), COUNT(*) FILTER (WHERE revoked_at_utc IS NOT NULL)
            FROM auth_refresh_sessions WHERE player_id=$1
            """);
        command.Parameters.AddWithValue(login.PlayerId);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
    }

    [AuthExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_IdentityIsStableAndHealthyAcrossAuthInstances()
    {
        var options = CreateOptions();
        await using var storeA = CreateStore();
        await using var storeB = CreateStore();
        await storeA.InitializeAsync(CancellationToken.None);
        var serviceA = CreateService(storeA, options);
        var serviceB = CreateService(storeB, options);
        var installationId = $"external-auth-stable-{Guid.NewGuid():N}";

        var first = await serviceA.LoginGuestAsync(
            new GuestLoginRequest(installationId, "稳定认证玩家"), CancellationToken.None);
        var second = await serviceB.LoginGuestAsync(
            new GuestLoginRequest(installationId, "不同昵称不会改身份"), CancellationToken.None);

        Assert.Equal(first.PlayerId, second.PlayerId);
        Assert.Equal(first.DisplayName, second.DisplayName);
        Assert.True(await storeA.CheckHealthAsync(CancellationToken.None));
        Assert.True(await storeB.CheckHealthAsync(CancellationToken.None));
    }

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("AUTH_TEST_POSTGRES")!;

    private static IOptions<AuthOptions> CreateOptions() =>
        Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            TokenSigningKey = "external-test-auth-signing-key-which-is-long-enough",
            GuestIdentityPepper = "external-test-auth-identity-pepper-which-is-long-enough",
            PersistenceMode = "Postgres",
            PostgresConnectionString = ConnectionString
        });

    private static PostgresAuthStore CreateStore() =>
        new(NpgsqlDataSource.Create(ConnectionString));

    private static AuthService CreateService(
        IAuthStore store,
        IOptions<AuthOptions> options) =>
        new(
            store,
            new PlayerAccessTokenIssuer(options),
            new LocalPlayerNameGenerator(),
            options,
            TimeProvider.System);
}
