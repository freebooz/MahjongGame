// 玩家访问令牌签发器：生成并验证 Lobby/游戏服使用的短期签名令牌及关键声明。
// 密钥必须由生产身份注入，算法和受众固定；过期、用途或账号版本不匹配时必须拒绝。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Auth.Security;

/// <summary>
/// 签发玩家短期访问令牌。
/// 实例持有服务端 HMAC 密钥，不负责刷新令牌；调用方必须在签发前完成账号控制检查，
/// 令牌格式属于跨 Auth/Lobby/游戏服契约。
/// </summary>
public sealed class PlayerAccessTokenIssuer(IOptions<AuthOptions> options)
{
    // HMAC 密钥来自启动配置并仅驻留服务内存，禁止输出到日志、异常或健康响应。
    private readonly byte[] signingKey = Encoding.UTF8.GetBytes(options.Value.TokenSigningKey);

    /// <summary>
    /// 为已认证身份签发指定 UTC 时间窗口的 HMAC 令牌。
    /// 调用方保证 expiresAtUtc 晚于 issuedAtUtc 且不超过策略上限；
    /// 返回值是敏感凭据，只能放入认证响应。
    /// </summary>
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

    /// <summary>
    /// 为实际登录会话签发带 Epoch 快照的兼容令牌。外层仍保持既有两段式 HMAC 格式，
    /// 旧消费者会忽略新增字段；新消费者可使用会话标识和 Epoch 执行撤销检查。
    /// </summary>
    public string Issue(
        AuthIdentity identity,
        RefreshSession session,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new PlayerSessionTokenPayload(
            identity.PlayerId,
            identity.DisplayName,
            identity.Provider,
            issuedAtUtc.ToUnixTimeMilliseconds(),
            expiresAtUtc.ToUnixTimeSeconds(),
            session.SessionId,
            session.SessionEpoch,
            session.SecurityEpoch));
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

    /// <summary>
    /// v1 兼容扩展载荷；新增字段采用可向后兼容的 JSON 属性，
    /// 不改变既有 Sub、Name、Provider、Iat 和 Exp 的含义及单位。
    /// </summary>
    private sealed record PlayerSessionTokenPayload(
        string Sub,
        string Name,
        string Provider,
        long Iat,
        long Exp,
        string Sid,
        long SessionEpoch,
        long SecurityEpoch);
}
