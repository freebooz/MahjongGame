using System.Security.Cryptography;
using GuiyangMahjong.Auth.Domain;
using GuiyangMahjong.Auth.Players;
using GuiyangMahjong.Auth.Storage;

namespace GuiyangMahjong.Auth.Tests;

/// <summary>
/// 验证阶段 3 拆分后的 Sessions、Devices 和 Players 模块在兼容存储适配器上的核心行为。
/// 测试不启动 HTTP 服务，也不接触外部数据库或任何生产凭证。
/// </summary>
public sealed class IdentityModuleTests
{
    /// <summary>单端策略必须在新会话创建事务内撤销旧会话，只保留一个活跃会话。</summary>
    [Fact]
    public async Task SingleDevicePolicy_RevokesPreviousSession()
    {
        var store = new InMemoryAuthStore();
        var now = DateTimeOffset.Parse("2026-07-31T00:00:00Z");
        var identity = await CreateIdentityAsync(store, "single-device-player", now);
        var first = CreateSession(identity, "device-a", now, "MultiDevice", 4);
        var second = CreateSession(identity, "device-b", now.AddSeconds(1), "SingleDevice", 1);

        Assert.Equal(
            SessionCreationStatus.Created,
            await store.CreateRefreshSessionAsync(first, now, CancellationToken.None));
        Assert.Equal(
            SessionCreationStatus.Created,
            await store.CreateRefreshSessionAsync(second, now.AddSeconds(1), CancellationToken.None));
        var detail = await store.GetPlayerDetailAsync(
            identity.PlayerId,
            now.AddSeconds(2),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(1, detail.Player.ActiveSessionCount);
    }

    /// <summary>多端策略达到上限时按创建顺序淘汰最早会话，不能无界增长。</summary>
    [Fact]
    public async Task MultiDevicePolicy_EnforcesConfiguredMaximum()
    {
        var store = new InMemoryAuthStore();
        var now = DateTimeOffset.Parse("2026-07-31T01:00:00Z");
        var identity = await CreateIdentityAsync(store, "multi-device-player", now);
        for (var index = 0; index < 3; index++)
        {
            var session = CreateSession(
                identity,
                $"device-{index}",
                now.AddSeconds(index),
                "MultiDevice",
                2);
            Assert.Equal(
                SessionCreationStatus.Created,
                await store.CreateRefreshSessionAsync(
                    session,
                    now.AddSeconds(index),
                    CancellationToken.None));
        }

        var detail = await store.GetPlayerDetailAsync(
            identity.PlayerId,
            now.AddMinutes(1),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Player.ActiveSessionCount);
    }

    /// <summary>
    /// 登录事件应生成设备摘要和切换历史所需的稳定引用；
    /// 玩家档案读取只能返回非凭证字段。
    /// </summary>
    [Fact]
    public async Task DeviceSwitchAndPlayerProfile_AreMaintainedWithoutCredentials()
    {
        var store = new InMemoryAuthStore();
        var now = DateTimeOffset.Parse("2026-07-31T02:00:00Z");
        var identity = await CreateIdentityAsync(store, "profile-player", now);
        await store.RecordLoginAsync(
            new AuthLoginEvent(
                Guid.NewGuid().ToString(),
                identity.PlayerId,
                "device-a",
                "10.0.0.*",
                "Windows",
                "Success",
                now),
            CancellationToken.None);
        await store.RecordLoginAsync(
            new AuthLoginEvent(
                Guid.NewGuid().ToString(),
                identity.PlayerId,
                "device-b",
                "10.0.1.*",
                "Android",
                "Success",
                now.AddMinutes(1)),
            CancellationToken.None);

        var profile = await ((IPlayerProfileReader)store).GetProfileAsync(
            identity.PlayerId,
            CancellationToken.None);
        var detail = await store.GetPlayerDetailAsync(
            identity.PlayerId,
            now.AddMinutes(2),
            CancellationToken.None);
        Assert.NotNull(profile);
        Assert.Equal(identity.DisplayName, profile.DisplayName);
        Assert.NotNull(detail);
        Assert.Equal(["device-b", "device-a"], detail.KnownDeviceIds);
    }

    /// <summary>建立稳定游客身份，安装摘要仅用于测试内存索引，不代表真实设备秘密。</summary>
    private static Task<AuthIdentity> CreateIdentityAsync(
        InMemoryAuthStore store,
        string playerId,
        DateTimeOffset now) =>
        store.GetOrCreateGuestAsync(
            Convert.ToHexStringLower(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(playerId))),
            new AuthIdentity(playerId, "模块测试玩家", "Guest", now, now),
            CancellationToken.None);

    /// <summary>创建只含哈希的测试会话；原始 Refresh Token 不进入存储或断言输出。</summary>
    private static RefreshSession CreateSession(
        AuthIdentity identity,
        string deviceId,
        DateTimeOffset now,
        string mode,
        int maximumActiveSessions) =>
        new(
            Guid.NewGuid().ToString("N"),
            identity.PlayerId,
            SHA256.HashData(Guid.NewGuid().ToByteArray()),
            now.AddDays(30),
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
            mode,
            maximumActiveSessions);
}
