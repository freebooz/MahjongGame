using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Options;
using GuiyangMahjong.Allocator.Services;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Tests;

/// <summary>验证 Fleet 路由的签名、稳定选择、暂停分配和 LKG 保护边界。</summary>
public sealed class AgonesFleetConfigurationTests
{
    private const string SigningKey = "allocator-fleet-signing-key-for-tests-0001";

    [Fact]
    public void Resolve_WithoutDynamicVersion_UsesStaticSafeBaseline()
    {
        var state = CreateState();
        Assert.Equal("stable-fleet", state.Resolve(Spec()));
    }

    [Fact]
    public async Task SignedRoute_SelectsCanaryAndStopFlagBlocksOnlyNewAllocation()
    {
        var state = CreateState();
        Assert.True(await state.TryApplyAsync(CreateEnvelope(1, "canary-fleet", false), CancellationToken.None));
        Assert.Equal("canary-fleet", state.Resolve(Spec()));

        Assert.True(await state.TryApplyAsync(CreateEnvelope(2, "canary-fleet", true), CancellationToken.None));
        var error = Assert.Throws<AllocatorOperationException>(() => state.Resolve(Spec()));
        Assert.Equal(503, error.StatusCode);
    }

    [Fact]
    public async Task InvalidSignature_DoesNotReplaceLastKnownGoodRoute()
    {
        var state = CreateState();
        Assert.True(await state.TryApplyAsync(CreateEnvelope(1, "stable-v2", false), CancellationToken.None));
        var invalid = CreateEnvelope(2, "bad-fleet", false) with { Signature = new string('0', 64) };
        Assert.False(await state.TryApplyAsync(invalid, CancellationToken.None));
        Assert.Equal("stable-v2", state.Resolve(Spec()));
    }

    private static AgonesFleetConfigurationState CreateState()
    {
        var path = Path.Combine(Path.GetTempPath(), "mahjong-allocator-tests", Guid.NewGuid().ToString("N"), "lkg.json");
        return new AgonesFleetConfigurationState(Microsoft.Extensions.Options.Options.Create(new AllocatorOptions
        {
            Agones = new AgonesAllocatorOptions
            {
                FleetName = "stable-fleet",
                DynamicFleetConfiguration = new AgonesFleetConfigurationOptions
                {
                    SigningKey = SigningKey,
                    LastKnownGoodPath = path
                }
            }
        }));
    }

    private static AgonesAllocationSpec Spec() => new(
        "room-1", "match-1", "server-1", "credential", "http://lobby", "build-v2",
        RuleSetVersion: "rules-v2", ProtocolVersion: "2", Region: "cn-southwest");

    private static AgonesFleetConfigurationEnvelope CreateEnvelope(long version, string fleet, bool stopped)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            fleetRoutes = new[]
            {
                new AgonesFleetRoute("route-v2", fleet, "build-v2", $"sha256:{new string('a', 64)}",
                    "rules-v2", new string('b', 64), "2", "cn-southwest", "cell-a", "canary", "exp-v2", stopped)
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
        var publishedAt = DateTimeOffset.Parse("2026-07-31T00:00:00Z");
        var material = $"platform.runtime\n{version}\n1\n{hash}\n{publishedAt:O}\n";
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SigningKey), Encoding.UTF8.GetBytes(material)));
        return new(version, 1, payload, hash, signature, publishedAt, null, "platform.runtime");
    }
}
