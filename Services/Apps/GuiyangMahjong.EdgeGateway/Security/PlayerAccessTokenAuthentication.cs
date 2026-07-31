using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GuiyangMahjong.EdgeGateway.Options;
using GuiyangMahjong.EdgeGateway.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.EdgeGateway.Security;

/// <summary>网关认证方案和玩家/管理员授权策略的稳定名称。</summary>
public static class GatewaySecurityPolicies
{
    /// <summary>根据令牌段数选择当前兼容令牌或标准 JWT 的策略方案。</summary>
    public const string PlayerAccessScheme = "PlayerAccess";

    /// <summary>当前 Auth 两段式 HMAC Access Token 验证方案。</summary>
    public const string LegacyPlayerScheme = "LegacyPlayer";

    /// <summary>标准 JWT 验证方案。</summary>
    public const string JwtPlayerScheme = "JwtPlayer";

    /// <summary>要求任一有效玩家 Access Token。</summary>
    public const string PlayerPolicy = "Player";

    /// <summary>管理认证框架；本阶段没有任何玩家网关路由使用该策略。</summary>
    public const string ManagementPolicy = "Management";
}

/// <summary>
/// 验证当前 Auth 签发的两段式 Base64Url + HMAC-SHA256 玩家令牌。
/// 未验证载荷绝不进入 ClaimsPrincipal，原始令牌也不得写入日志或错误响应。
/// </summary>
public sealed class LegacyPlayerTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemes,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<EdgeGatewayOptions> gatewayOptions,
    TimeProvider timeProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(
        schemes,
        logger,
        encoder)
{
    private readonly byte[] signingKey = Encoding.UTF8.GetBytes(
        gatewayOptions.Value.PlayerTokens.LegacySigningKey);
    private readonly TimeProvider timeProvider = timeProvider;
    private readonly TimeSpan clockSkew = TimeSpan.FromSeconds(
        gatewayOptions.Value.PlayerTokens.ClockSkewSeconds);

    /// <summary>
    /// 验证 Authorization Bearer 的格式、签名、主体和时间窗口。
    /// 失败只返回稳定原因；下游服务仍会再次验证同一个 Access Token。
    /// </summary>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(
                "Bearer ",
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = authorization["Bearer ".Length..].Trim();
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 2
            || !TryDecode(parts[0], out var payload)
            || !TryDecode(parts[1], out var signature))
        {
            return Task.FromResult(
                AuthenticateResult.Fail("PLAYER_TOKEN_FORMAT_INVALID"));
        }

        var expected = HMACSHA256.HashData(
            signingKey,
            Encoding.ASCII.GetBytes(parts[0]));
        if (signature.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(
                signature,
                expected))
        {
            return Task.FromResult(
                AuthenticateResult.Fail("PLAYER_TOKEN_SIGNATURE_INVALID"));
        }

        try
        {
            var claims = JsonSerializer.Deserialize<LegacyPlayerTokenPayload>(
                payload);
            var now = timeProvider.GetUtcNow();
            if (claims is null
                || string.IsNullOrWhiteSpace(claims.Sub)
                || claims.Sub.Length > 80
                || string.IsNullOrWhiteSpace(claims.Name)
                || claims.Name.Length > 24
                || claims.Provider.Length > 32
                || claims.Exp <= now.Subtract(clockSkew).ToUnixTimeSeconds())
            {
                return Task.FromResult(
                    AuthenticateResult.Fail("PLAYER_TOKEN_CLAIMS_INVALID"));
            }

            var issuedAt = claims.Iat <= 0
                ? DateTimeOffset.UnixEpoch
                : DateTimeOffset.FromUnixTimeMilliseconds(claims.Iat);
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(claims.Exp);
            if (issuedAt >= expiresAt
                || issuedAt > now.Add(clockSkew))
            {
                return Task.FromResult(
                    AuthenticateResult.Fail("PLAYER_TOKEN_TIME_INVALID"));
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, claims.Sub),
                    new Claim("sub", claims.Sub),
                    new Claim(ClaimTypes.Name, claims.Name),
                    new Claim("provider", claims.Provider)
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(identity),
                    Scheme.Name)));
        }
        catch (JsonException)
        {
            return Task.FromResult(
                AuthenticateResult.Fail("PLAYER_TOKEN_PAYLOAD_INVALID"));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Task.FromResult(
                AuthenticateResult.Fail("PLAYER_TOKEN_TIME_INVALID"));
        }
    }

    /// <summary>把缺失或无效兼容令牌转换为网关统一 401，不暴露验证细节。</summary>
    protected override Task HandleChallengeAsync(
        AuthenticationProperties properties) =>
        GatewayErrorWriter.WriteAsync(
            Context,
            StatusCodes.Status401Unauthorized,
            "UNAUTHENTICATED",
            "未提供有效的玩家登录凭据。");

    /// <summary>认证成功但策略不满足时返回统一 403。</summary>
    protected override Task HandleForbiddenAsync(
        AuthenticationProperties properties) =>
        GatewayErrorWriter.WriteAsync(
            Context,
            StatusCodes.Status403Forbidden,
            "FORBIDDEN",
            "当前身份无权访问该资源。");

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(
                padded.Length + ((4 - padded.Length % 4) % 4),
                '=');
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private sealed record LegacyPlayerTokenPayload(
        string Sub,
        string Name,
        string Provider,
        long Iat,
        long Exp);
}
