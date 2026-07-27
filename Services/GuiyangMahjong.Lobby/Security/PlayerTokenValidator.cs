using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Security;

public interface IPlayerTokenValidator
{
    PlayerTokenValidationResult Validate(string token);
}

public sealed record PlayerTokenValidationResult(
    bool IsValid,
    PlayerIdentity? Player,
    DateTimeOffset IssuedAtUtc,
    string ChineseReason)
{
    public static PlayerTokenValidationResult Success(
        PlayerIdentity player,
        DateTimeOffset issuedAtUtc) =>
        new(true, player, issuedAtUtc, string.Empty);
    public static PlayerTokenValidationResult Failure(string reason) =>
        new(false, null, DateTimeOffset.MinValue, reason);
}

/// <summary>
/// 使用服务端密钥验证短期玩家 Token。服务不提供公开签发端点，客户端无法自行产生有效签名。
/// </summary>
public sealed class HmacPlayerTokenValidator : IPlayerTokenValidator
{
    private readonly byte[] signingKey;
    private readonly TimeProvider timeProvider;

    public HmacPlayerTokenValidator(IOptions<LobbyOptions> options, TimeProvider timeProvider)
    {
        signingKey = Encoding.UTF8.GetBytes(options.Value.TokenSigningKey);
        this.timeProvider = timeProvider;
    }

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

        var expected = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(parts[0]));
        if (signature.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(signature, expected))
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

            return PlayerTokenValidationResult.Success(
                new PlayerIdentity(payload.Sub, payload.Name, payload.Provider),
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
            expiresAtUtc.ToUnixTimeSeconds());
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
        long Exp);
}
