using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.GameRouting;
using GuiyangMahjong.Lobby.Matchmaking;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Reconnection;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace GuiyangMahjong.Lobby.Tests;

/// <summary>
/// 阶段 4 LobbyControl 模块验收测试。
/// 覆盖规范状态机、RoomEpoch fencing、显式座位及匹配票据原子消费，不依赖外部基础设施。
/// </summary>
public sealed class LobbyControlStage4Tests
{
    [Fact]
    public void RoomStateMachine_ContainsAllCanonicalStates_AndRejectsIllegalSkip()
    {
        var canonical = new[]
        {
            RoomLifecycle.Created,
            RoomLifecycle.Waiting,
            RoomLifecycle.Ready,
            RoomLifecycle.Allocating,
            RoomLifecycle.Starting,
            RoomLifecycle.Playing,
            RoomLifecycle.Suspended,
            RoomLifecycle.Recovering,
            RoomLifecycle.Settling,
            RoomLifecycle.Finished,
            RoomLifecycle.Terminating,
            RoomLifecycle.Aborted,
            RoomLifecycle.Archived
        };

        Assert.Equal(13, canonical.Distinct().Count());
        Assert.False(RoomStateMachine.CanTransition(
            RoomLifecycle.Created,
            RoomLifecycle.Playing));
        Assert.True(RoomStateMachine.CanTransition(
            RoomLifecycle.Recovering,
            RoomLifecycle.Allocating));
        Assert.False(RoomStateMachine.CanTransition(
            RoomLifecycle.Archived,
            RoomLifecycle.Waiting));
    }

    [Fact]
    public void RoomLifecycleJson_KeepsLegacyTerminalWireValues_AndReadsCanonicalValues()
    {
        Assert.Equal(
            "\"Closed\"",
            JsonSerializer.Serialize(RoomLifecycle.Finished));
        Assert.Equal(
            "\"Failed\"",
            JsonSerializer.Serialize(RoomLifecycle.Aborted));
        Assert.Equal(
            RoomLifecycle.Finished,
            JsonSerializer.Deserialize<RoomLifecycle>("\"Finished\""));
        Assert.Equal(
            RoomLifecycle.Finished,
            JsonSerializer.Deserialize<RoomLifecycle>("\"Closed\""));
    }

    [Fact]
    public void Reallocation_IncrementsEpochAndStateVersion_ThenRejectsOldInstanceEpoch()
    {
        var room = NewRoom("410001", "owner-epoch");

        var recovering = GameRoutingPolicy.BeginReallocation(
            room,
            TimeProvider.System);

        Assert.Equal(room.RoomEpoch + 1, recovering.RoomEpoch);
        Assert.Equal(room.StateVersion + 1, recovering.StateVersion);
        Assert.Equal(RoomLifecycle.Recovering, recovering.Lifecycle);
        Assert.Null(recovering.Route);
        Assert.False(GameRoutingPolicy.AcceptsEpoch(
            recovering.RoomEpoch,
            room.RoomEpoch));
        Assert.True(GameRoutingPolicy.AcceptsEpoch(
            recovering.RoomEpoch,
            recovering.RoomEpoch));
    }

    [Fact]
    public async Task JoinRoom_AssignsStableUniqueSeat_AndDuplicateJoinDoesNotConsumeCapacity()
    {
        var store = new InMemoryLobbyStore();
        var room = NewRoom("410002", "owner-seat");
        Assert.Equal(
            CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);

        var first = await store.TryAddPlayerAsync(
            room.RoomCode,
            "member-seat",
            CancellationToken.None);
        var duplicate = await store.TryAddPlayerAsync(
            room.RoomCode,
            "member-seat",
            CancellationToken.None);

        Assert.Equal(AddPlayerStatus.Added, first.Status);
        Assert.Equal(AddPlayerStatus.AlreadyMember, duplicate.Status);
        Assert.NotNull(duplicate.Room);
        Assert.Equal(2, duplicate.Room.PlayerIds.Length);
        Assert.Equal(2, duplicate.Room.Seats.Length);
        Assert.Equal(
            duplicate.Room.Seats.Length,
            duplicate.Room.Seats.Select(seat => seat.SeatIndex).Distinct().Count());
    }

    [Fact]
    public async Task MatchmakingReservation_IsAtomicAcrossConcurrentGroups()
    {
        var store = new InMemoryMatchmakingTicketStore(TimeProvider.System);
        for (var index = 0; index < 4; index++)
        {
            await store.CreateAsync(
                $"player-{index}",
                "standard",
                TimeSpan.FromMinutes(2),
                CancellationToken.None);
        }
        var leftReservation = Guid.NewGuid();
        var rightReservation = Guid.NewGuid();

        var attempts = await Task.WhenAll(
            store.ReserveAsync(
                "standard",
                4,
                leftReservation,
                CancellationToken.None),
            store.ReserveAsync(
                "standard",
                4,
                rightReservation,
                CancellationToken.None));

        Assert.Single(attempts, result => result.Count == 4);
        Assert.Single(attempts, result => result.Count == 0);
        var winners = attempts.Single(result => result.Count == 4);
        Assert.Single(winners.Select(ticket => ticket.ReservationId).Distinct());
    }

    [Fact]
    public async Task MatchmakingTicket_CanOnlyBeConsumedByReservation_AndDuplicateIsAcknowledged()
    {
        var store = new InMemoryMatchmakingTicketStore(TimeProvider.System);
        var created = await store.CreateAsync(
            "player-consume",
            "standard",
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var reservationId = Guid.NewGuid();
        var reserved = await store.ReserveAsync(
            "standard",
            1,
            reservationId,
            CancellationToken.None);

        var wrong = await store.ConsumeAsync(
            created.TicketId,
            Guid.NewGuid(),
            CancellationToken.None);
        var accepted = await store.ConsumeAsync(
            created.TicketId,
            reservationId,
            CancellationToken.None);
        var duplicate = await store.ConsumeAsync(
            created.TicketId,
            reservationId,
            CancellationToken.None);

        Assert.Single(reserved);
        Assert.False(wrong.Accepted);
        Assert.True(accepted.Accepted);
        Assert.False(accepted.Duplicate);
        Assert.True(duplicate.Accepted);
        Assert.True(duplicate.Duplicate);
    }

    [Fact]
    public async Task Reconnection_RejectsRecoveringRoomAfterConfiguredWindow()
    {
        var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
        var time = new FixedTimeProvider(now);
        var store = new InMemoryLobbyStore();
        var room = NewRoom("410003", "owner-reconnect") with
        {
            Lifecycle = RoomLifecycle.Recovering,
            EmptySinceUtc = now.AddMinutes(-3),
            UpdatedAtUtc = now.AddMinutes(-3),
            Route = null
        };
        Assert.Equal(
            CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);
        var options = Microsoft.Extensions.Options.Options.Create(
            new LobbyOptions
            {
                TokenSigningKey =
                    LobbyWebApplicationFactory.SigningKey,
                JoinTicketSigningKey =
                    "test-only-join-ticket-signing-key-which-is-long-enough",
                Matchmaking = new MatchmakingOptions
                {
                    ReconnectionWindowSeconds = 120
                }
            });
        var service = new ReconnectionService(
            store,
            new HmacJoinTicketIssuer(options, time),
            options,
            time,
            NullLogger<ReconnectionService>.Instance);

        var exception = await Assert.ThrowsAsync<LobbyOperationException>(
            () => service.GetRouteAsync(
                "reconnect-timeout",
                new PlayerIdentity(
                    room.OwnerPlayerId,
                    "Owner",
                    "Guest"),
                new ReconnectRouteRequest(
                    room.RoomId,
                    room.MatchId),
                CancellationToken.None));

        Assert.Equal(LobbyErrorCode.Timeout, exception.ErrorCode);
    }

    [Fact]
    public async Task Stage4Schema_DefinesAuthoritativeVersionEpochSeatsAndMatchingConstraints()
    {
        var schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "Schemas",
            "Lobby",
            "schema.sql");
        var schema = await File.ReadAllTextAsync(schemaPath);

        Assert.Contains("state_version BIGINT", schema, StringComparison.Ordinal);
        Assert.Contains("room_epoch BIGINT", schema, StringComparison.Ordinal);
        Assert.Contains("room.room_members", schema, StringComparison.Ordinal);
        Assert.Contains("room.room_state_history", schema, StringComparison.Ordinal);
        Assert.Contains(
            "matchmaking.matchmaking_tickets",
            schema,
            StringComparison.Ordinal);
        Assert.Contains(
            "ux_matchmaking_active_player_queue",
            schema,
            StringComparison.Ordinal);
    }

    /// <summary>创建带当前路由的最小权威房间，用于验证 Epoch 和座位规则。</summary>
    private static LobbyRoom NewRoom(string roomCode, string ownerPlayerId)
    {
        var now = DateTimeOffset.UtcNow;
        return new LobbyRoom
        {
            RoomId = Guid.NewGuid().ToString(),
            RoomCode = roomCode,
            OwnerPlayerId = ownerPlayerId,
            RoundCount = 4,
            PublicRoom = true,
            AutoStart = true,
            MaximumPlayers = 4,
            RuleSnapshot = new Dictionary<string, object?>
            {
                ["ruleId"] = "GuiyangMainstreamV1",
                ["ruleVersion"] = "1"
            },
            Lifecycle = RoomLifecycle.Allocating,
            PlayerIds = [ownerPlayerId],
            MatchId = Guid.NewGuid().ToString(),
            StateSequence = 3,
            RoomEpoch = 1,
            RuleSetVersion = "1",
            BuildVersion = "test-build",
            PendingServerInstanceId = "server-old",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }
}
