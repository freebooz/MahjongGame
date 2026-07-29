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

    [Fact]
    public async Task EnterpriseIdentityRejectsTokenOlderThanRevocationSla()
    {
        var middleware = new AdminAuthenticationMiddleware(
            _ => Task.CompletedTask,
            CreateOptions());
        var context = CreateContext(
            new Claim("sub", "departed-operator"),
            new Claim("roles", "player.viewer"),
            new Claim("amr", "mfa"));
        // 覆盖默认短时签发时间，验证离职或角色回收不会被长会话绕过。
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "departed-operator"),
            new Claim("roles", "player.viewer"),
            new Claim("amr", "mfa"),
            new Claim(
                "iat",
                DateTimeOffset.UtcNow.AddMinutes(-11)
                    .ToUnixTimeSeconds().ToString()),
            new Claim("jti", Guid.NewGuid().ToString("N"))
        ], "oidc"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(
        params Claim[] claims)
    {
        // 测试令牌模拟企业 IdP 的短时 iat/jti，分别验证 MFA 和角色映射而不绕过会话门禁。
        var enterpriseClaims = claims.Concat(
        [
            new Claim(
                "iat",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
            new Claim("jti", Guid.NewGuid().ToString("N"))
        ]);
        var context = new DefaultHttpContext();
        context.Request.Path = "/admin/v1/players";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity(enterpriseClaims, "oidc"));
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
