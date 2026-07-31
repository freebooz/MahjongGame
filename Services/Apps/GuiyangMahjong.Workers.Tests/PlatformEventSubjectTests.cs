using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.Contracts.Events;
using Xunit;

namespace GuiyangMahjong.Workers.Tests;

/// <summary>锁定首批事件的公开 Subject，避免部署配置与代码映射静默漂移。</summary>
public sealed class PlatformEventSubjectTests
{
    [Fact]
    public void FirstBatchEvents_HaveExactVersionedSubjects()
    {
        var expected = new Dictionary<string, string>
        {
            [PlatformEventTypes.SessionCreated] = "identity.session.created.v1",
            [PlatformEventTypes.SessionRevoked] = "identity.session.revoked.v1",
            [PlatformEventTypes.RoomCreated] = "room.created.v1",
            [PlatformEventTypes.RoomStateChanged] = "room.state.changed.v1",
            [PlatformEventTypes.AllocationRequested] = "allocation.requested.v1",
            [PlatformEventTypes.GameServerAllocated] = "gameserver.allocated.v1",
            [PlatformEventTypes.GameServerReady] = "gameserver.ready.v1",
            [PlatformEventTypes.PlayerConnected] = "player.connected.v1",
            [PlatformEventTypes.PlayerDisconnected] = "player.disconnected.v1",
            [PlatformEventTypes.MatchStarted] = "match.started.v1",
            [PlatformEventTypes.MatchFinished] = "match.finished.v1",
            [PlatformEventTypes.SettlementCommitted] = "settlement.committed.v1",
            [PlatformEventTypes.RoomTerminated] = "room.terminated.v1"
        };

        foreach (var (eventType, subject) in expected)
        {
            Assert.Equal(subject, PlatformEventSubjects.Resolve(eventType, 1));
            Assert.True(PlatformEventSubjects.Matches(subject, eventType, 1));
        }
        Assert.Equal(expected.Values.Order(), PlatformEventSubjects.All.Order());
    }

    [Fact]
    public void UnknownEventAndFutureSchema_FailClosed()
    {
        Assert.Throws<InvalidDataException>(() =>
            PlatformEventSubjects.Resolve("unknown.event", 1));
        Assert.Throws<InvalidDataException>(() =>
            PlatformEventSubjects.Resolve(PlatformEventTypes.RoomCreated, 2));
    }
}
