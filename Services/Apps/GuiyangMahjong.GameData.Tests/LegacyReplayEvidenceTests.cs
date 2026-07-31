using System.Text.Json;
using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Infrastructure;

namespace GuiyangMahjong.GameData.Tests;

/// <summary>阶段8.2旧回放索引测试；确保幂等写不影响结算、战绩或排行榜。</summary>
public sealed class LegacyReplayEvidenceTests
{
    [Fact]
    public async Task RecordLegacyReplay_IsIdempotentAndRejectsChangedPayload()
    {
        var store = new InMemoryGameDataStore();
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { replayId = "replay-1" }));

        Assert.False((await store.RecordLegacyReplayAsync(request, default)).Duplicate);
        Assert.True((await store.RecordLegacyReplayAsync(request, default)).Duplicate);
        await Assert.ThrowsAsync<GuiyangMahjong.GameData.Settlement.GameDataException>(() =>
            store.RecordLegacyReplayAsync(
                request with { Data = JsonSerializer.SerializeToElement(new { replayId = "changed" }) }, default));
        Assert.Empty(await store.GetLeaderboardAsync(10, default));
    }

    private static LegacyReplayEvidenceRequest CreateRequest(JsonElement data) => new(
        Guid.NewGuid().ToString(), "player-replay-test", "Replay",
        new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero),
        $"replay:{Guid.NewGuid():N}", data, "Restricted");
}
