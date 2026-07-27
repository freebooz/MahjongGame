using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Options;
using GuiyangMahjong.Auth.Security;
using GuiyangMahjong.Auth.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Auth.Services;

public sealed partial class AuthService(
    IAuthStore store,
    PlayerAccessTokenIssuer accessTokenIssuer,
    LocalPlayerNameGenerator playerNameGenerator,
    IOptions<AuthOptions> options,
    TimeProvider timeProvider)
{
    private readonly AuthOptions options = options.Value;

    public async Task<AuthSessionResponse> LoginGuestAsync(
        GuestLoginRequest request,
        CancellationToken cancellationToken) =>
        await LoginGuestAsync(
            request, new LoginObservation("Unknown", "Unknown"), cancellationToken);

    public async Task<AuthSessionResponse> LoginGuestAsync(
        GuestLoginRequest request,
        LoginObservation observation,
        CancellationToken cancellationToken)
    {
        var installationId = request.InstallationId?.Trim() ?? string.Empty;
        if (!InstallationIdPattern().IsMatch(installationId))
            throw new AuthOperationException("INVALID_REQUEST", "设备安装标识格式无效", 400);

        var now = timeProvider.GetUtcNow();
        var installationHashBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.GuestIdentityPepper),
            Encoding.UTF8.GetBytes(installationId));
        var installationHash = Convert.ToHexStringLower(installationHashBytes);
        var playerId = $"guest-{Base64UrlEncode(installationHashBytes.AsSpan(0, 18))}";
        var displayName = NormalizeDisplayName(request.DisplayName, playerNameGenerator);
        var identity = await store.GetOrCreateGuestAsync(
            installationHash,
            new AuthIdentity(playerId, displayName, "Guest", now, now),
            cancellationToken);
        var refresh = CreateRefreshSession(identity.PlayerId, now);
        await store.CreateRefreshSessionAsync(refresh.Session, cancellationToken);
        await store.RecordLoginAsync(
            new AuthLoginEvent(
                Guid.NewGuid().ToString(),
                identity.PlayerId,
                $"device-{installationHash[..20]}",
                NormalizeObservation(observation.MaskedIp, 64, "Unknown"),
                NormalizeObservation(observation.ClientSummary, 160, "Unknown"),
                "Success",
                now),
            cancellationToken);
        return CreateResponse(identity, refresh, now);
    }

    public async Task<AuthSessionResponse> RefreshAsync(
        RefreshSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseRefreshToken(request.RefreshToken, out var sessionId, out var tokenHash))
            throw InvalidRefresh();
        var now = timeProvider.GetUtcNow();
        var replacement = CreateRefreshSession(string.Empty, now);
        var rotation = await store.RotateRefreshSessionAsync(
            sessionId,
            tokenHash,
            replacement.Session,
            now,
            cancellationToken);
        if (rotation.Status != RefreshRotationStatus.Rotated || rotation.Identity is null)
            throw InvalidRefresh();

        return CreateResponse(rotation.Identity, replacement, now);
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseRefreshToken(request.RefreshToken, out var sessionId, out var tokenHash)) return;
        await store.RevokeRefreshSessionAsync(
            sessionId, tokenHash, timeProvider.GetUtcNow(), cancellationToken);
    }

    private AuthSessionResponse CreateResponse(
        AuthIdentity identity,
        IssuedRefreshToken refresh,
        DateTimeOffset now)
    {
        var accessExpiry = now.AddMinutes(options.AccessTokenMinutes);
        return new AuthSessionResponse(
            identity.PlayerId,
            identity.DisplayName,
            identity.Provider,
            accessTokenIssuer.Issue(identity, now, accessExpiry),
            accessExpiry,
            refresh.Plaintext,
            refresh.Session.ExpiresAtUtc);
    }

    private IssuedRefreshToken CreateRefreshSession(string playerId, DateTimeOffset now)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var secret = RandomNumberGenerator.GetBytes(32);
        var plaintext = $"{sessionId}.{Base64UrlEncode(secret)}";
        return new IssuedRefreshToken(
            plaintext,
            new RefreshSession(
                sessionId,
                playerId,
                SHA256.HashData(secret),
                now.AddDays(options.RefreshTokenDays),
                now,
                null));
    }

    private static bool TryParseRefreshToken(string? token, out string sessionId, out byte[] hash)
    {
        sessionId = string.Empty;
        hash = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return false;
        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length != 32 || !Guid.TryParseExact(parts[0], "N", out _))
            return false;
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var secret = Convert.FromBase64String(padded);
            if (secret.Length != 32) return false;
            sessionId = parts[0];
            hash = SHA256.HashData(secret);
            CryptographicOperations.ZeroMemory(secret);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static string NormalizeDisplayName(
        string? supplied,
        LocalPlayerNameGenerator playerNameGenerator)
    {
        var value = supplied?.Trim() ?? string.Empty;
        if (value.Length == 0) return playerNameGenerator.Generate();
        if (value.Length is < 2 or > 24 || value.Any(char.IsControl))
            throw new AuthOperationException("INVALID_REQUEST", "昵称长度必须为 2 到 24 个字符", 400);
        return value;
    }

    private static AuthOperationException InvalidRefresh() =>
        new("SESSION_EXPIRED", "刷新凭据无效、已过期或已被使用", 401);

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeObservation(
        string? value, int maximumLength, string fallback)
    {
        var normalized = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray())
            .Trim();
        return normalized.Length == 0 ? fallback : normalized;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{16,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallationIdPattern();

    private sealed record IssuedRefreshToken(string Plaintext, RefreshSession Session);
}

public sealed class AuthOperationException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
