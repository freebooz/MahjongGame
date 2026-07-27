using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Security;

public sealed class AdminAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<AdminOptions> options)
{
    private readonly byte[] expected =
        Encoding.UTF8.GetBytes(options.Value.ReadOnlyAccessToken);
    private readonly (byte[] Token, AdminPrincipal Principal)[] principals =
        options.Value.Principals.Select(item => (
            Encoding.UTF8.GetBytes(item.AccessToken),
            new AdminPrincipal(
                item.OperatorId,
                item.Roles.ToHashSet(StringComparer.Ordinal))))
            .ToArray();
    private readonly EnterpriseIdentityOptions enterprise =
        options.Value.EnterpriseIdentity;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/admin/v1"))
        {
            await next(context);
            return;
        }

        if (enterprise.Enabled)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                await RejectAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "ADMIN_ENTERPRISE_IDENTITY_REQUIRED",
                    "A valid enterprise identity token is required.");
                return;
            }
            var operatorId = context.User.FindFirstValue(
                enterprise.OperatorIdClaim)?.Trim();
            var roles = context.User.Claims
                .Where(claim => claim.Type == enterprise.RoleClaim
                    || claim.Type == ClaimTypes.Role)
                .SelectMany(claim => claim.Value.Split(
                    [' ', ','],
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries))
                .Where(AdminRoles.Known.Contains)
                .ToHashSet(StringComparer.Ordinal);
            var mfaSatisfied = !enterprise.RequireMfa
                || context.User.Claims.Any(claim =>
                    claim.Type == enterprise.AuthenticationMethodClaim
                    && claim.Value.Split(
                            [' ', ','],
                            StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries)
                        .Contains(
                            enterprise.MfaValue,
                            StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(operatorId)
                || operatorId.Length > 128
                || roles.Count == 0)
            {
                await RejectAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "ADMIN_ENTERPRISE_CLAIMS_INVALID",
                    "The enterprise identity has no valid operator id or administrator role.");
                return;
            }
            if (!mfaSatisfied)
            {
                await RejectAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "ADMIN_MFA_REQUIRED",
                    "A recent multi-factor authentication is required.");
                return;
            }
            AdminPrincipalContext.Set(
                context,
                new AdminPrincipal(operatorId, roles));
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(authorization[7..].Trim())
            : [];
        AdminPrincipal? principal = null;
        foreach (var candidate in principals)
        {
            if (supplied.Length == candidate.Token.Length
                && CryptographicOperations.FixedTimeEquals(supplied, candidate.Token))
            {
                principal = candidate.Principal;
            }
        }
        if (principal is null
            && supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            principal = new AdminPrincipal(
                "readonly-operator",
                new HashSet<string>(
                    [AdminRoles.RoomViewer, AdminRoles.PlayerViewer],
                    StringComparer.Ordinal));
        }
        CryptographicOperations.ZeroMemory(supplied);
        if (principal is null)
        {
            await RejectAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "ADMIN_UNAUTHORIZED",
                "A valid read-only administrator credential is required.");
            return;
        }

        AdminPrincipalContext.Set(context, principal);
        await next(context);
    }

    private static async Task RejectAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new
        {
            requestId = context.TraceIdentifier,
            code,
            message
        });
    }
}
