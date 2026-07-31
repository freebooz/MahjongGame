using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Security;

/// <summary>验证 Auth 签发的短期玩家访问令牌并提取最小身份的安全边界。</summary>
public interface IPlayerTokenValidator
{
    /// <summary>验证格式、HMAC、必需声明、签发/过期时间；失败返回原因而不抛出凭据内容。</summary>
    PlayerTokenValidationResult Validate(string token);
}

/// <summary>玩家令牌验证结果；失败时 Player 为空，IssuedAtUtc 不可用于会话撤销判断。</summary>
public sealed record PlayerTokenValidationResult(
    bool IsValid,
    PlayerIdentity? Player,
    DateTimeOffset IssuedAtUtc,
    string ChineseReason)
{
    /// <summary>构造已验证结果；player 与签发时间均来自通过签名校验的载荷。</summary>
    public static PlayerTokenValidationResult Success(
        PlayerIdentity player,
        DateTimeOffset issuedAtUtc) =>
        new(true, player, issuedAtUtc, string.Empty);

    /// <summary>构造脱敏失败结果，不保留原始令牌或解析出的未验证声明。</summary>
    public static PlayerTokenValidationResult Failure(string reason) =>
        new(false, null, DateTimeOffset.MinValue, reason);
}

/// <summary>
/// 使用服务端密钥验证短期玩家 Token。服务不提供公开签发端点，客户端无法自行产生有效签名。
/// HMAC 比较采用固定时间算法，任何未验证载荷都不能创建 PlayerIdentity。
/// </summary>
public sealed class HmacPlayerTokenValidator : IPlayerTokenValidator
{
    // 当前密钥排在首位，旧密钥仅在有限轮换窗口内参与固定时间签名比较。
    private readonly byte[][] validationKeys;
    private readonly TimeProvider timeProvider;

    /// <summary>取得 Auth/Lobby 共享签名密钥和可测试 UTC 时间源；密钥只驻留服务内存。</summary>
    public HmacPlayerTokenValidator(IOptions<LobbyOptions> options, TimeProvider timeProvider)
    {
        validationKeys = options.Value.PreviousTokenValidationKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Prepend(options.Value.TokenSigningKey)
            .Select(Encoding.UTF8.GetBytes)
            .ToArray();
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public PlayerTokenValidationResult Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
        {
            return PlayerTokenValidationResult.Failure("登录凭据无效");
        }

        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !TryDecode(parts[0], out var payloadBytes) || !TryDecode(parts[1], out var signature))
        {
            return PlayerTokenValidationResult.Failure("登录凭据格式无效");
        }

        var signedBytes = Encoding.ASCII.GetBytes(parts[0]);
        var signatureValid = false;
        foreach (var validationKey in validationKeys)
        {
            var expected = HMACSHA256.HashData(validationKey, signedBytes);
            signatureValid |= signature.Length == expected.Length
                              && CryptographicOperations.FixedTimeEquals(signature, expected);
        }
        if (!signatureValid)
        {
            return PlayerTokenValidationResult.Failure("登录凭据签名无效");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<PlayerTokenPayload>(payloadBytes);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Sub) || string.IsNullOrWhiteSpace(payload.Name))
            {
                return PlayerTokenValidationResult.Failure("登录身份不完整");
            }

            if (payload.Exp <= timeProvider.GetUtcNow().ToUnixTimeSeconds())
            {
                return PlayerTokenValidationResult.Failure("登录会话已过期，请重新登录");
            }

            var issuedAt = payload.Iat <= 0
                ? DateTimeOffset.UnixEpoch
                : DateTimeOffset.FromUnixTimeMilliseconds(payload.Iat);
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
            if (issuedAt >= expiresAt
                || issuedAt > timeProvider.GetUtcNow().AddMinutes(1))
            {
                return PlayerTokenValidationResult.Failure("登录凭据签发时间无效");
            }

            if (payload.Sub.Length > 80 || payload.Name.Length > 24 || payload.Provider.Length > 32)
            {
                return PlayerTokenValidationResult.Failure("登录身份字段超出限制");
            }

            // 新版 Auth 令牌携带 Sid/Epoch；旧令牌仍可在迁移窗口内解析，但只能签发带 legacy 标记的票据。
            return PlayerTokenValidationResult.Success(
                new PlayerIdentity(
                    payload.Sub,
                    payload.Name,
                    payload.Provider,
                    string.IsNullOrWhiteSpace(payload.Sid) ? "legacy-session" : payload.Sid,
                    payload.SessionEpoch,
                    payload.SecurityEpoch),
                issuedAt);
        }
        catch (JsonException)
        {
            return PlayerTokenValidationResult.Failure("登录凭据内容无效");
        }
    }

    /// <summary>供受信 Auth 适配器和自动化测试签发；不得暴露为客户端 HTTP API。</summary>
    public static string CreateSignedToken(
        string signingKey,
        PlayerIdentity player,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? issuedAtUtc = null)
    {
        var issuedAt = issuedAtUtc ?? expiresAtUtc.AddMinutes(-15);
        var payload = new PlayerTokenPayload(
            player.PlayerId,
            player.DisplayName,
            player.Provider,
            issuedAt.ToUnixTimeMilliseconds(),
            expiresAtUtc.ToUnixTimeSeconds(),
            player.SessionId,
            player.SessionEpoch,
            player.SecurityEpoch);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var encodedPayload = Base64UrlEncode(payloadBytes);
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(signingKey), Encoding.ASCII.GetBytes(encodedPayload));
        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record PlayerTokenPayload(
        string Sub,
        string Name,
        string Provider,
        long Iat,
        long Exp,
        string? Sid = null,
        long SessionEpoch = 0,
        long SecurityEpoch = 0);
}
