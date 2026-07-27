using System.Security.Claims;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Tests;

public sealed class AdminEnterpriseAuthenticationTests
{
    [Fact]
    public async Task EnterpriseIdentityRequiresMfaAndMapsOnlyKnownRoles()
    {
        AdminPrincipal? resolved = null;
        var middleware = new AdminAuthenticationMiddleware(
            context =>
            {
                resolved = AdminPrincipalContext.Get(context);
                return Task.CompletedTask;
            },
            CreateOptions());
        var context = CreateContext(
            new Claim("sub", "enterprise-operator"),
            new Claim("roles", "player.viewer player.operator unknown.role"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Null(resolved);

        context = CreateContext(
            new Claim("sub", "enterprise-operator"),
            new Claim("roles", "player.viewer player.operator unknown.role"),
            new Claim("amr", "pwd mfa"));
        await middleware.InvokeAsync(context);

        Assert.NotNull(resolved);
        Assert.Equal("enterprise-operator", resolved.OperatorId);
        Assert.Equal(
            new[] { AdminRoles.PlayerOperator, AdminRoles.PlayerViewer },
            resolved.Roles.Order());
        Assert.DoesNotContain("unknown.role", resolved.Roles);
    }

    private static DefaultHttpContext CreateContext(
        params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/v1/players";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity(claims, "oidc"));
        return context;
    }

    private static IOptions<AdminOptions> CreateOptions() =>
        Microsoft.Extensions.Options.Options.Create(new AdminOptions
        {
            ReadOnlyAccessToken =
                "legacy-read-token-that-is-at-least-32-characters",
            EnterpriseIdentity = new EnterpriseIdentityOptions
            {
                Enabled = true,
                Authority = "https://identity.example.invalid",
                Audience = "mahjong-admin",
                RequireMfa = true
            }
        });
}
