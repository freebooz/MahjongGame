using System.Text.Json;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Security;

namespace GuiyangMahjong.Lobby.Tests;

/// <summary>
/// 验证 Lobby 令牌消费者能够读取独立 v1 契约的确定性测试向量。
/// 与 Auth 生产者测试组合后形成黑盒兼容门禁，两个测试项目无需互相引用程序集。
/// </summary>
public sealed class PlayerAccessTokenContractTests
{
    /// <summary>
    /// 在契约指定时间验证固定令牌，并核对玩家身份字段，避免只验证签名而遗漏载荷语义。
    /// </summary>
    [Fact]
    public async Task LobbyValidator_AcceptsCanonicalV1Token()
    {
        using var contract = await LoadContractAsync();
        var vector = contract.RootElement.GetProperty("testVector");
        var payload = vector.GetProperty("payload");
        var validator = new HmacPlayerTokenValidator(
            Microsoft.Extensions.Options.Options.Create(new LobbyOptions
            {
                TokenSigningKey = vector.GetProperty("signingKey").GetString()
                    ?? throw new InvalidDataException("Contract signing key is missing.")
            }),
            new FixedTimeProvider(
                vector.GetProperty("validationTimeUtc").GetDateTimeOffset()));

        var result = validator.Validate(
            vector.GetProperty("token").GetString()
            ?? throw new InvalidDataException("Contract token is missing."));

        Assert.True(result.IsValid, result.ChineseReason);
        Assert.Equal(payload.GetProperty("Sub").GetString(), result.Player?.PlayerId);
        Assert.Equal(payload.GetProperty("Name").GetString(), result.Player?.DisplayName);
        Assert.Equal(payload.GetProperty("Provider").GetString(), result.Player?.Provider);
        Assert.Equal(
            vector.GetProperty("issuedAtUtc").GetDateTimeOffset(),
            result.IssuedAtUtc);
    }

    /// <summary>
    /// 从测试输出目录读取契约，确保消费者测试和部署仓库使用同一事实来源。
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
