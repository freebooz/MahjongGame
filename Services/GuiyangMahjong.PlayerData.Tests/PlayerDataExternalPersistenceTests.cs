// PostgreSQL 外部集成测试：验证资产流水、奖励、补偿和证据投影的原子性与幂等性。
// 仅在显式隔离数据库中运行，测试结束不得遗留可被生产身份访问的数据。
using System.Text.Json;
using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Storage;
using Npgsql;

namespace GuiyangMahjong.PlayerData.Tests;

public sealed class PlayerDataExternalPersistenceFactAttribute : FactAttribute
{
    public PlayerDataExternalPersistenceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("PLAYER_DATA_TEST_POSTGRES")))
        {
            Skip =
                "Set PLAYER_DATA_TEST_POSTGRES to run PlayerData external persistence tests.";
        }
    }
}

public sealed class PlayerDataExternalPersistenceTests
{
    [PlayerDataExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_RewardWalletAndProjectionAreAtomicAndIdempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var playerId = $"player-data-{Guid.NewGuid():N}";
        var reward = new RewardClaimRequest(
            Guid.NewGuid().ToString(),
            $"reward-{Guid.NewGuid():N}",
            playerId,
            "coin",
            5000,
            now.AddMinutes(-1),
            $"reward-source-{Guid.NewGuid():N}",
            $"trace-{Guid.NewGuid():N}");
        await using var storeA = CreateStore();
        await using var storeB = CreateStore();
        await storeA.InitializeAsync(CancellationToken.None);

        var rewardResults = await Task.WhenAll(
            storeA.RecordRewardClaimAsync(
                reward,
                now,
                CancellationToken.None),
            storeB.RecordRewardClaimAsync(
                reward,
                now,
                CancellationToken.None));

        Assert.Single(rewardResults, item => !item.Duplicate);
        Assert.Single(rewardResults, item => item.Duplicate);
        var balance = Assert.Single(
            await storeB.ListBalancesAsync(
                playerId,
                CancellationToken.None));
        Assert.Equal("COIN", balance.AssetCode);
        Assert.Equal(5000, balance.Balance);

        var walletRequest = new AdminWalletOperationRequest(
            "GrantCompensation",
            playerId,
            Guid.NewGuid().ToString(),
            "coin",
            800,
            null,
            "compensation-operator",
            "player-approver",
            "Approved service interruption compensation",
            $"TICKET-{Guid.NewGuid():N}",
            $"trace-{Guid.NewGuid():N}",
            now);
        var commandId = Guid.NewGuid().ToString();
        var walletResults = await Task.WhenAll(
            storeA.ApplyWalletOperationAsync(
                commandId,
                walletRequest,
                now.AddSeconds(1),
                CancellationToken.None),
            storeB.ApplyWalletOperationAsync(
                commandId,
                walletRequest,
                now.AddSeconds(1),
                CancellationToken.None));

        Assert.Single(walletResults, item => !item.Duplicate);
        Assert.Single(walletResults, item => item.Duplicate);
        Assert.All(walletResults, item =>
            Assert.Equal(5800, item.BalanceAfter));
        balance = Assert.Single(
            await storeA.ListBalancesAsync(
                playerId,
                CancellationToken.None));
        Assert.Equal(5800, balance.Balance);
        Assert.Equal(2, balance.Version);

        var claimed = await storeA.ClaimProjectionsAsync(
            "projection-worker-a",
            20,
            now.AddSeconds(2),
            now.AddMinutes(1),
            CancellationToken.None);
        Assert.Equal(3, claimed.Count);
        Assert.Empty(await storeB.ClaimProjectionsAsync(
            "projection-worker-b",
            20,
            now.AddSeconds(3),
            now.AddMinutes(1),
            CancellationToken.None));
        foreach (var projection in claimed)
        {
            await storeA.CompleteProjectionAsync(
                projection.EventId,
                "projection-worker-a",
                CancellationToken.None);
        }
        Assert.True(await storeB.CheckHealthAsync(CancellationToken.None));
    }

    [PlayerDataExternalPersistenceFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task PostgreSql_SourceEvidenceRejectsConflictingReuse()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new RecordEvidenceRequest(
            Guid.NewGuid().ToString(),
            $"player-payment-{Guid.NewGuid():N}",
            PlayerEvidenceType.PaymentOrder,
            now,
            $"payment-{Guid.NewGuid():N}",
            JsonSerializer.SerializeToElement(new
            {
                orderReference = $"masked-{Guid.NewGuid():N}",
                amountMinor = 1200,
                currency = "CNY",
                status = "Paid"
            }),
            PlayerEvidenceSensitivity.Financial);
        await using var store = CreateStore();
        await store.InitializeAsync(CancellationToken.None);

        Assert.False((await store.RecordEvidenceAsync(
            request,
            now,
            CancellationToken.None)).Duplicate);
        Assert.True((await store.RecordEvidenceAsync(
            request,
            now,
            CancellationToken.None)).Duplicate);
        await Assert.ThrowsAsync<PlayerDataOperationException>(
            () => store.RecordEvidenceAsync(
                request with
                {
                    PlayerId = $"different-{Guid.NewGuid():N}"
                },
                now,
                CancellationToken.None));
    }

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("PLAYER_DATA_TEST_POSTGRES")!;

    private static PostgresPlayerDataStore CreateStore() =>
        new(NpgsqlDataSource.Create(ConnectionString));
}
