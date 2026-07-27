using System.Security.Cryptography;
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

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/admin/v1"))
        {
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
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                requestId = context.TraceIdentifier,
                code = "ADMIN_UNAUTHORIZED",
                message = "A valid read-only administrator credential is required."
            });
            return;
        }

        AdminPrincipalContext.Set(context, principal);
        await next(context);
    }
}
