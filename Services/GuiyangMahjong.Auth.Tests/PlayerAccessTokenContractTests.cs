using System.Text.Json;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Options;
using GuiyangMahjong.Auth.Security;

namespace GuiyangMahjong.Auth.Tests;

/// <summary>
/// 验证 Auth 令牌生产者严格匹配独立 v1 契约测试向量。
/// 测试只引用 Auth 本身和仓库契约文件，不依赖 Lobby 生产程序集。
/// </summary>
public sealed class PlayerAccessTokenContractTests
{
    /// <summary>
    /// 使用固定身份、时间和测试密钥签发令牌，逐字比较确定性向量，
    /// 从而同时约束 JSON 大小写、时间单位、Base64Url 和 HMAC 算法。
    /// </summary>
    [Fact]
    public async Task AuthIssuer_MatchesCanonicalV1Token()
    {
        using var contract = await LoadContractAsync();
        var vector = contract.RootElement.GetProperty("testVector");
        var payload = vector.GetProperty("payload");
        var issuedAtUtc = vector.GetProperty("issuedAtUtc").GetDateTimeOffset();
        var expiresAtUtc = vector.GetProperty("expiresAtUtc").GetDateTimeOffset();
        var issuer = new PlayerAccessTokenIssuer(
            Microsoft.Extensions.Options.Options.Create(new AuthOptions
            {
                TokenSigningKey = vector.GetProperty("signingKey").GetString()
                    ?? throw new InvalidDataException("Contract signing key is missing.")
            }));
        var identity = new AuthIdentity(
            payload.GetProperty("Sub").GetString()!,
            payload.GetProperty("Name").GetString()!,
            payload.GetProperty("Provider").GetString()!,
            issuedAtUtc,
            issuedAtUtc);

        var token = issuer.Issue(identity, issuedAtUtc, expiresAtUtc);

        Assert.Equal(vector.GetProperty("token").GetString(), token);
    }

    /// <summary>
    /// 从测试输出目录读取契约，缺失文件代表项目复制规则或构建上下文已损坏。
    /// </summary>
    private static async Task<JsonDocument> LoadContractAsync()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "player-access-token-v1.contract.json");
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream);
    }
}
