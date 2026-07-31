using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.EdgeGateway.Configuration;
using GuiyangMahjong.EdgeGateway.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace GuiyangMahjong.EdgeGateway.Tests;

/// <summary>验证 EdgeGateway 的签名配置、原子切换和 Last Known Good 故障回退。</summary>
public sealed class GatewayDynamicConfigurationTests : IDisposable
{
    private readonly string lkgPath = Path.Combine(Path.GetTempPath(), $"mahjong-edge-lkg-{Guid.NewGuid():N}.json");
    private const string SigningKey = "edge-test-configuration-signing-key-000001";

    /// <summary>有效版本可落盘恢复；坏签名不会清空或覆盖最后有效配置。</summary>
    [Fact]
    public async Task InvalidSignature_PreservesLastKnownGood()
    {
        var options = CreateOptions();
        var state = new GatewayConfigurationState(options);
        var valid = CreateEnvelope(7);
        Assert.True(await state.TryApplyAsync(valid, default));
        Assert.False(await state.TryApplyAsync(CreateEnvelope(8) with { Signature = new string('0', 64) }, default));
        Assert.Equal(7, state.Snapshot().ConfigVersion);

        // 新进程即使配置中心不可用，也能从持久卷恢复已验真的 LKG。
        var restored = new GatewayConfigurationState(options);
        await restored.RestoreAsync(default);
        Assert.Equal(7, restored.Snapshot().ConfigVersion);
        Assert.Equal("2.0.0", restored.Snapshot().Contract.MinimumClientVersion);
    }

    private IOptions<EdgeGatewayOptions> CreateOptions() => Microsoft.Extensions.Options.Options.Create(new EdgeGatewayOptions
    {
        ClientContract = new ClientContractOptions { MinimumClientVersion = "1.0.0", RecommendedClientVersion = "1.0.0" },
        DynamicConfiguration = new DynamicConfigurationOptions { SigningKey = SigningKey, LastKnownGoodPath = lkgPath }
    });

    private static GatewayConfigurationEnvelope CreateEnvelope(long version)
    {
        using var document = JsonDocument.Parse("""{"client":{"minimumVersion":"2.0.0","recommendedVersion":"2.1.0","blockedVersions":["1.5.0"],"supportedProtocolVersions":["2"]},"apiProtocolVersion":2}""");
        var payload = document.RootElement.Clone();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
        var publishedAt = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        var material = $"platform.runtime\n{version}\n1\n{hash}\n{publishedAt:O}\n";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), Encoding.UTF8.GetBytes(material)));
        return new(version, 1, payload, hash, signature, publishedAt, null, "platform.runtime");
    }

    /// <summary>只删除本测试创建的精确临时文件，不触碰工作区或用户目录中的其他数据。</summary>
    public void Dispose()
    {
        if (File.Exists(lkgPath)) File.Delete(lkgPath);
    }
}
