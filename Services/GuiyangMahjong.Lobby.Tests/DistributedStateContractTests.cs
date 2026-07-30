// 验证 Lobby 多副本共享状态契约，包括在线状态、幂等结果、事件传播和访问撤销水位。
using System.Text.Json;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Services;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class DistributedStateContractTests
{
    [Fact]
    public async Task IdempotencyStore_CoalescesConcurrentExecution()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-20T00:00:00Z"));
        var store = new InMemoryIdempotencyStore(
            Microsoft.Extensions.Options.Options.Create(new LobbyOptions()), time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<IdempotentHttpResponse> Execute()
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
            return new IdempotentHttpResponse(202, JsonSerializer.SerializeToElement(new { roomCode = "123456" }));
        }

        var first = store.ExecuteAsync("same-key", Execute, CancellationToken.None);
        await entered.Task;
        var second = store.ExecuteAsync("same-key", Execute, CancellationToken.None);
        release.TrySetResult();
        var responses = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(responses[0].Body.GetRawText(), responses[1].Body.GetRawText());
    }

    [Fact]
    public async Task PresenceStore_ExpiresInactivePlayers()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-07-20T00:00:00Z"));
        var presence = new InMemoryOnlinePresenceService(
            Microsoft.Extensions.Options.Options.Create(new LobbyOptions { PresenceTimeoutSeconds = 90 }),
            time);
        await presence.TouchAsync("player-a", CancellationToken.None);
        await presence.TouchAsync("player-b", CancellationToken.None);
        Assert.Equal(2, await presence.GetOnlineCountAsync(CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(91));
        await presence.TouchAsync("player-b", CancellationToken.None);

        Assert.Equal(1, await presence.GetOnlineCountAsync(CancellationToken.None));
    }
}
