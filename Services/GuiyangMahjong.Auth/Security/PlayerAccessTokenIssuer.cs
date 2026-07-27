using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Auth.Security;

public sealed class PlayerAccessTokenIssuer(IOptions<AuthOptions> options)
{
    private readonly byte[] signingKey = Encoding.UTF8.GetBytes(options.Value.TokenSigningKey);

    public string Issue(
        AuthIdentity identity,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new PlayerTokenPayload(
            identity.PlayerId,
            identity.DisplayName,
            identity.Provider,
            issuedAtUtc.ToUnixTimeMilliseconds(),
            expiresAtUtc.ToUnixTimeSeconds()));
        var encodedPayload = Base64UrlEncode(payload);
        var signature = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(encodedPayload));
        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
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
