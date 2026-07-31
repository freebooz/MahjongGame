using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Auth;
using GuiyangMahjong.Auth.Devices;
using GuiyangMahjong.Auth.Options;
using GuiyangMahjong.Auth.Security;
using GuiyangMahjong.Auth.Sessions;
using GuiyangMahjong.Auth.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Auth.Services;

/// <summary>
/// Auth 应用服务，协调游客身份、刷新令牌、访问令牌和登录审计。
/// 明文刷新令牌只在生成和响应期间存在，存储层仅接收哈希；
/// 每次登录/刷新均重新执行账号冻结与封禁策略。
/// </summary>
public sealed partial class AuthService(
    IIdentityRepository identities,
    ISessionRepository sessions,
    IDeviceAuditWriter devices,
    PlayerAccessTokenIssuer accessTokenIssuer,
    LocalPlayerNameGenerator playerNameGenerator,
    IOptions<AuthOptions> options,
    IOptions<SessionPolicyOptions> sessionPolicyOptions,
    TimeProvider timeProvider)
{
    // 启动时验证并冻结的令牌 TTL、密钥和身份策略；请求处理中不动态接受客户端覆盖。
    private readonly AuthOptions options = options.Value;
    // 会话并发策略在启动阶段完成校验并冻结，防止单个请求通过 Header 或正文改变安全边界。
    private readonly (SessionPolicyMode Mode, int MaximumActiveSessions) sessionPolicy =
        sessionPolicyOptions.Value.ToPolicy();

    /// <summary>
    /// 保留阶段 3 之前的进程内构造入口，供既有集成测试和嵌入式调用方平滑升级。
    /// 未显式提供策略时采用与生产默认值一致的有限多设备模式。
    /// </summary>
    public AuthService(
        IAuthStore store,
        PlayerAccessTokenIssuer accessTokenIssuer,
        LocalPlayerNameGenerator playerNameGenerator,
        IOptions<AuthOptions> options,
        TimeProvider timeProvider)
        : this(
            store,
            store,
            store,
            accessTokenIssuer,
            playerNameGenerator,
            options,
            Microsoft.Extensions.Options.Options.Create(new SessionPolicyOptions()),
            timeProvider)
    {
    }

    /// <summary>兼容无网络观察值的内部登录入口；生产 HTTP 入口应使用带脱敏观察值的重载。</summary>
    public async Task<AuthSessionResponse> LoginGuestAsync(
        GuestLoginRequest request,
        CancellationToken cancellationToken) =>
        await LoginGuestAsync(
            request, new LoginObservation("Unknown", "Unknown"), cancellationToken);

    /// <summary>
    /// 校验安装标识和展示名，取得稳定游客身份，创建刷新会话并记录脱敏登录事件。
    /// 冻结或封禁状态不会签发任何令牌；返回值为敏感认证响应。
    /// </summary>
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
        var deviceId = $"device-{installationHash[..20]}";
        var identity = await identities.GetOrCreateGuestAsync(
            installationHash,
            new AuthIdentity(playerId, displayName, "Guest", now, now),
            cancellationToken);
        var refresh = CreateRefreshSession(identity, deviceId, now);
        var creation = await sessions.CreateRefreshSessionAsync(
            refresh.Session,
            now,
            cancellationToken);
        if (creation != SessionCreationStatus.Created)
        {
            await devices.RecordLoginAsync(
                new AuthLoginEvent(
                    Guid.NewGuid().ToString(),
                    identity.PlayerId,
                    deviceId,
                    NormalizeObservation(observation.MaskedIp, 64, "Unknown"),
                    NormalizeObservation(observation.ClientSummary, 160, "Unknown"),
                    creation.ToString(),
                    now),
                cancellationToken);
            throw Restricted(creation.ToString());
        }
        await devices.RecordLoginAsync(
            new AuthLoginEvent(
                Guid.NewGuid().ToString(),
                identity.PlayerId,
                deviceId,
                NormalizeObservation(observation.MaskedIp, 64, "Unknown"),
                NormalizeObservation(observation.ClientSummary, 160, "Unknown"),
                "Success",
                now),
            cancellationToken);
        return CreateResponse(identity, refresh, now);
    }

    /// <summary>
    /// 单次消费刷新令牌并签发后继会话和访问令牌。
    /// 格式错误、过期、撤销、重放、冻结或封禁统一失败，旧令牌不能继续使用。
    /// </summary>
    public async Task<AuthSessionResponse> RefreshAsync(
        RefreshSessionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryParseRefreshToken(request.RefreshToken, out var sessionId, out var tokenHash))
            throw InvalidRefresh();
        var now = timeProvider.GetUtcNow();
        // 轮换事务会从当前会话继承玩家、设备、Token Family 和 Epoch；
        // 此处只生成一次性新秘密，避免应用层先读取再更新产生竞态。
        var replacement = CreateRefreshSession(
            new AuthIdentity(string.Empty, string.Empty, string.Empty, now, now),
            string.Empty,
            now);
        var rotation = await sessions.RotateRefreshSessionAsync(
            sessionId,
            tokenHash,
            replacement.Session,
            now,
            cancellationToken);
        if (rotation.Status is RefreshRotationStatus.Frozen or RefreshRotationStatus.Banned)
            throw Restricted(rotation.Status.ToString());
        if (rotation.Status != RefreshRotationStatus.Rotated
            || rotation.Identity is null
            || rotation.ReplacementSession is null)
            throw InvalidRefresh();

        return CreateResponse(
            rotation.Identity,
            replacement with { Session = rotation.ReplacementSession },
            now);
    }

    /// <summary>幂等撤销有效刷新令牌；格式损坏或已撤销时无副作用，不泄漏会话是否存在。</summary>
    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseRefreshToken(request.RefreshToken, out var sessionId, out var tokenHash)) return;
        await sessions.RevokeRefreshSessionAsync(
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
            accessTokenIssuer.Issue(identity, refresh.Session, now, accessExpiry),
            accessExpiry,
            refresh.Plaintext,
            refresh.Session.ExpiresAtUtc);
    }

    private IssuedRefreshToken CreateRefreshSession(
        AuthIdentity identity,
        string deviceId,
        DateTimeOffset now)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var secret = RandomNumberGenerator.GetBytes(32);
        var plaintext = $"{sessionId}.{Base64UrlEncode(secret)}";
        return new IssuedRefreshToken(
            plaintext,
            new RefreshSession(
                sessionId,
                identity.PlayerId,
                SHA256.HashData(secret),
                now.AddDays(options.RefreshTokenDays),
                now,
                null,
                Guid.NewGuid().ToString("N"),
                null,
                deviceId,
                identity.SessionEpoch,
                identity.SecurityEpoch,
                null,
                null,
                null,
                sessionPolicy.Mode.ToString(),
                sessionPolicy.MaximumActiveSessions));
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

    private static AuthOperationException Restricted(string status) =>
        new(
            $"ACCOUNT_{status.ToUpperInvariant()}",
            status == "Banned"
                ? "Account is permanently banned."
                : "Account is temporarily frozen.",
            StatusCodes.Status403Forbidden);

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

/// <summary>可安全映射为 Auth API 错误码和 HTTP 状态的领域异常。</summary>
public sealed class AuthOperationException(string code, string message, int statusCode) : Exception(message)
{
    /// <summary>稳定机器错误码，客户端只能据此选择重新登录等受控行为。</summary>
    public string Code { get; } = code;

    /// <summary>由统一异常边界返回的 HTTP 状态码。</summary>
    public int StatusCode { get; } = statusCode;
}
