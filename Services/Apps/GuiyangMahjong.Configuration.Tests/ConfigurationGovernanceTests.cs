using GuiyangMahjong.Configuration.Domain;
using GuiyangMahjong.Configuration.Infrastructure;
using GuiyangMahjong.Configuration.Options;
using GuiyangMahjong.Configuration.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GuiyangMahjong.Configuration.Tests;

/// <summary>覆盖阶段 11 的 Schema、安全、不变版本、稳定灰度、双人审批、签名、幂等与回滚门禁。</summary>
public sealed class ConfigurationGovernanceTests
{
    /// <summary>同一主体和实验必须稳定落入同一分桶，禁用实验不得意外进入 Canary。</summary>
    [Fact]
    public void StableBucket_IsDeterministic_AndDisabledRuleIsolated()
    {
        var subject = new RolloutSubject("player-1", null, "official", "1.2.0", "Windows", "cn-south", false);
        var rule = SamplePayload().Rollouts.Single();
        var results = Enumerable.Range(0, 50).Select(_ => StableRolloutEvaluator.IsCanary(rule, subject)).Distinct().ToArray();
        Assert.Single(results);
        Assert.False(StableRolloutEvaluator.IsCanary(rule with { Enabled = false, PercentageBasisPoints = 10_000 }, subject));
    }

    /// <summary>错误 Schema 与疑似敏感字典键必须在创建不可变版本前被拒绝。</summary>
    [Fact]
    public void Schema_RejectsInvalidVersionAndSensitiveFeatureKey()
    {
        var payload = SamplePayload() with
        {
            Client = SamplePayload().Client with { MinimumVersion = "not-semver" },
            FeatureFlags = new Dictionary<string, bool> { ["databasePassword"] = true }
        };
        var result = ConfigurationPolicy.Validate(payload);
        Assert.Contains("CLIENT_MINIMUM_VERSION_INVALID", result.Errors);
        Assert.Contains("SENSITIVE_CONFIGURATION_FORBIDDEN", result.Errors);
    }

    /// <summary>已发布 Build 和 RuleSet 名称不能原地绑定新摘要，必须创建新版本标识。</summary>
    [Fact]
    public async Task ExistingBuildAndRuleSet_CannotBeOverwritten()
    {
        var service = CreateService();
        var first = await PublishAsync(service, SamplePayload(), "draft-key-0001", "publish-key-0001");
        var changed = SamplePayload() with
        {
            FleetRoutes = [SamplePayload().FleetRoutes[0] with { ServerImageDigest = $"sha256:{new string('b', 64)}" }]
        };
        var draft = await service.CreateDraftAsync(new(PlatformConfigurationService.PlatformConfigKey, 1, changed, "release", "ticket-2"),
            "operator-1", "trace-0002", "draft-key-0002", default);
        var error = await Assert.ThrowsAsync<ConfigurationOperationException>(() =>
            service.ValidateDraftAsync(draft.DraftId, "operator-1", default));
        Assert.Equal("CONFIG_VALIDATION_FAILED", error.Code);
        Assert.True(service.Verify(first));
    }

    /// <summary>高风险发布必须由异人审批，发布后相同幂等键重试返回首次结果且不会生成新版本。</summary>
    [Fact]
    public async Task Publish_RequiresTwoPeople_AndIsIdempotent()
    {
        var service = CreateService();
        var draft = await service.CreateDraftAsync(new(PlatformConfigurationService.PlatformConfigKey, 1, SamplePayload(), "release", "ticket-1"),
            "operator-1", "trace-0001", "draft-key-0001", default);
        await service.ValidateDraftAsync(draft.DraftId, "operator-1", default);
        var denied = await Assert.ThrowsAsync<ConfigurationOperationException>(() => service.PublishAsync(draft.DraftId,
            new("operator-1", "operator-1", "approval-1", "release", "ticket-1", "trace-0001", "publish-key-0001"), default));
        Assert.Equal("CONFIG_TWO_PERSON_APPROVAL_REQUIRED", denied.Code);
        var command = new PublishConfigurationCommand("operator-1", "approver-2", "approval-1", "release", "ticket-1", "trace-0001", "publish-key-0001");
        var first = await service.PublishAsync(draft.DraftId, command, default);
        var repeated = await service.PublishAsync(draft.DraftId, command, default);
        Assert.Equal(first.VersionId, repeated.VersionId);
        Assert.Single(await service.ListVersionsAsync(PlatformConfigurationService.PlatformConfigKey, default));
    }

    /// <summary>回滚复制历史正本生成更高版本，不更新或删除目标历史版本。</summary>
    [Fact]
    public async Task Rollback_CreatesNewSignedVersion()
    {
        var service = CreateService();
        var first = await PublishAsync(service, SamplePayload(), "draft-key-0001", "publish-key-0001");
        var secondPayload = SamplePayload() with { FeatureFlags = new Dictionary<string, bool> { ["newLobby"] = true } };
        _ = await PublishAsync(service, secondPayload, "draft-key-0002", "publish-key-0002", "ticket-2");
        var rollback = await service.RollbackAsync(PlatformConfigurationService.PlatformConfigKey,
            new(1, "operator-1", "approver-2", "approval-3", "rollback", "ticket-3", "trace-0003", "rollback-key-0003"), default);
        Assert.Equal(3, rollback.Version);
        Assert.Equal(first.PayloadHash, rollback.PayloadHash);
        Assert.Equal(1, rollback.RollbackOfVersion);
        Assert.True(service.Verify(rollback));
    }

    /// <summary>客户端兼容策略必须同时区分最低版本、阻断列表和协议列表。</summary>
    [Fact]
    public async Task ClientView_ReportsBlockedAndSupportedProtocols()
    {
        var service = CreateService();
        var published = await PublishAsync(service, SamplePayload(), "draft-key-0001", "publish-key-0001");
        var view = service.EvaluateClient(published,
            new("player-1", null, "official", "0.9.0", "Windows", "cn-south", false));
        Assert.True(view.Blocked);
        Assert.Contains("2", view.SupportedProtocolVersions);
        Assert.Equal(1, view.ConfigVersion);
    }

    private static PlatformConfigurationService CreateService() => new(
        new InMemoryConfigurationStore(),
        Microsoft.Extensions.Options.Options.Create(new ConfigurationOptions
        {
            SigningKey = "test-signing-key-with-at-least-32-characters",
            AdminCommandToken = "test-admin-token-with-at-least-32-characters",
            ServiceReadToken = "test-reader-token-with-at-least-32-characters"
        }),
        TimeProvider.System,
        NullLogger<PlatformConfigurationService>.Instance);

    private static async Task<PublishedConfiguration> PublishAsync(
        PlatformConfigurationService service, PlatformConfigurationPayload payload,
        string draftKey, string publishKey, string ticket = "ticket-1")
    {
        var draft = await service.CreateDraftAsync(new(PlatformConfigurationService.PlatformConfigKey, 1, payload, "release", ticket),
            "operator-1", $"trace-{draftKey}", draftKey, default);
        await service.ValidateDraftAsync(draft.DraftId, "operator-1", default);
        return await service.PublishAsync(draft.DraftId,
            new("operator-1", "approver-2", $"approval-{publishKey}", "release", ticket, $"trace-{publishKey}", publishKey), default);
    }

    private static PlatformConfigurationPayload SamplePayload() => new(
        new("1.0.0", "1.2.0", ["0.9.0"], ["1", "2"]),
        2,
        new Dictionary<string, bool> { ["newLobby"] = false },
        [new("lobby-canary-v1", 1_000, [], [], ["official"], [], ["Windows"], ["cn-south"], true, true)],
        [new("stable-cn-south", "guiyang-mahjong", "server-1.0.0", $"sha256:{new string('a', 64)}",
            "rules-1.0.0", new string('c', 64), "2", "cn-south", "cell-a", "stable", null, false)],
        [new("friend-room-v1", "rules-1.0.0", 8, 4, true)],
        "risk-1.0.0");
}
