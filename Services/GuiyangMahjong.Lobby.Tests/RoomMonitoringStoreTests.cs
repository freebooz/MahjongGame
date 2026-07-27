using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Storage;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class RoomMonitoringStoreTests
{
    [Fact]
    public async Task RuntimeSnapshotAndTimelineRoundTripInOrder()
    {
        var store = new InMemoryRoomMonitoringStore();
        var runtime = new RoomRuntimeTelemetry(
            "room-1",
            "instance-1",
            DateTimeOffset.Parse("2026-07-27T08:00:00Z"),
            DateTimeOffset.Parse("2026-07-27T07:59:00Z"),
            "Playing",
            2,
            1,
            16.6,
            60.2,
            42,
            256 * 1024 * 1024,
            null,
            null,
            null,
            "build-1",
            [new PlayerRuntimeTelemetry("player-1", 0, "Connected", 25, null, false)]);
        await store.SetRuntimeAsync(runtime, CancellationToken.None);
        await store.AppendEventAsync(
            runtime.RoomId,
            NewEvent("event-1", runtime.ObservedAtUtc),
            CancellationToken.None);
        await store.AppendEventAsync(
            runtime.RoomId,
            NewEvent("event-2", runtime.ObservedAtUtc.AddSeconds(1)),
            CancellationToken.None);

        Assert.Equal(runtime, await store.GetRuntimeAsync(runtime.RoomId, CancellationToken.None));
        var events = await store.ListEventsAsync(runtime.RoomId, 20, CancellationToken.None);
        Assert.Equal(["event-1", "event-2"], events.Select(item => item.EventId));
    }

    private static RoomTimelineEvent NewEvent(string eventId, DateTimeOffset occurredAtUtc) =>
        new(
            eventId,
            "PlayerConnectionChanged",
            occurredAtUtc,
            1,
            Guid.NewGuid().ToString(),
            new Dictionary<string, object?>());
}
