using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Tests;

public sealed class RoomDomainTests
{
    [Fact]
    public async Task ConcurrentCreation_ForSamePlayer_LeavesExactlyOneActiveRoom()
    {
        var store = new InMemoryLobbyStore();
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            start.Wait();
            return await store.TryCreateRoomAsync(
                NewRoom($"{index:D6}", "shared-player"), CancellationToken.None);
        })).ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.Status == CreateRoomStatus.Created);
        Assert.Equal(31, results.Count(result => result.Status == CreateRoomStatus.PlayerAlreadyActive));
        Assert.NotNull(await store.GetActiveRoomByPlayerAsync("shared-player", CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentJoin_ForTwoRooms_ReservesOnlyOneActiveMembership()
    {
        var store = new InMemoryLobbyStore();
        var left = NewRoom("100001", "owner-left");
        var right = NewRoom("100002", "owner-right");
        Assert.Equal(CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(left, CancellationToken.None)).Status);
        Assert.Equal(CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(right, CancellationToken.None)).Status);
        using var start = new ManualResetEventSlim(false);

        var attempts = new[] { left.RoomCode, right.RoomCode }.Select(code => Task.Run(async () =>
        {
            start.Wait();
            return await store.TryAddPlayerAsync(code, "joining-player", CancellationToken.None);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Status == AddPlayerStatus.Added);
        Assert.Single(results, result => result.Status == AddPlayerStatus.AlreadyInAnotherRoom);
        var active = await store.GetActiveRoomByPlayerAsync("joining-player", CancellationToken.None);
        Assert.NotNull(active);
        Assert.Single(new[] { left.RoomId, right.RoomId }, id => id == active.RoomId);
    }

    [Fact]
    public async Task ClosingRoom_ReleasesPlayerLease_AndRejectsStaleOverwrite()
    {
        var store = new InMemoryLobbyStore();
        var room = NewRoom("200001", "reusable-player");
        Assert.Equal(CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);
        var closed = room with
        {
            Lifecycle = RoomLifecycle.Failed,
            StateSequence = room.StateSequence + 1,
            UpdatedAtUtc = room.UpdatedAtUtc.AddSeconds(1)
        };

        Assert.True(await store.UpdateRoomAsync(closed, CancellationToken.None));
        Assert.False(await store.UpdateRoomAsync(closed, CancellationToken.None));
        Assert.Null(await store.GetActiveRoomByPlayerAsync("reusable-player", CancellationToken.None));
        Assert.Equal(CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(
                NewRoom("200002", "reusable-player"), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task WaitingRoom_ReclaimsExpiredSeats_BeforeCapacityCheck()
    {
        var store = new InMemoryLobbyStore();
        var room = NewRoom("200003", "owner");
        Assert.Equal(CreateRoomStatus.Created,
            (await store.TryCreateRoomAsync(room, CancellationToken.None)).Status);
        Assert.Equal(AddPlayerStatus.Added,
            (await store.TryAddPlayerAsync(room.RoomCode, "connected-player", CancellationToken.None)).Status);
        Assert.Equal(AddPlayerStatus.Added,
            (await store.TryAddPlayerAsync(room.RoomCode, "stale-player-1", CancellationToken.None)).Status);
        Assert.Equal(AddPlayerStatus.Added,
            (await store.TryAddPlayerAsync(room.RoomCode, "stale-player-2", CancellationToken.None)).Status);

        var observedAtUtc = room.UpdatedAtUtc.AddMinutes(5);
        await store.RefreshConnectedPlayersAsync(
            room.RoomId,
            ["owner", "connected-player"],
            observedAtUtc,
            CancellationToken.None);
        var reconciled = await store.ReconcileWaitingRoomMembersAsync(
            room.RoomCode,
            "joining-player",
            observedAtUtc.AddSeconds(-90),
            observedAtUtc,
            CancellationToken.None);
        var joined = await store.TryAddPlayerAsync(
            room.RoomCode, "joining-player", CancellationToken.None);

        Assert.NotNull(reconciled);
        Assert.Equal(["owner", "connected-player"], reconciled.PlayerIds);
        Assert.Equal(AddPlayerStatus.Added, joined.Status);
        Assert.NotNull(joined.Room);
        Assert.Equal(["owner", "connected-player", "joining-player"], joined.Room.PlayerIds);
    }

    [Fact]
    public async Task ConcurrentRoomCreation_AlwaysProducesUniqueRoomCodes()
    {
        var store = new InMemoryLobbyStore();
        var service = CreateService(store);
        var createTasks = Enumerable.Range(0, 200).Select(index => service.CreateRoomAsync(
            Guid.NewGuid().ToString(),
            new PlayerIdentity($"player-{index}", $"玩家{index}", "Guest"),
            NewCreateRequest(),
            CancellationToken.None));

        var rooms = await Task.WhenAll(createTasks);

        Assert.Equal(200, rooms.Select(room => room.RoomCode).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rooms, room => Assert.Matches("^[0-9]{6}$", room.RoomCode));
    }

    [Fact]
    public async Task SharedPersistentStore_RestoresRoomForNewServiceInstance()
    {
        var durableStoreContract = new InMemoryLobbyStore();
        var firstService = CreateService(durableStoreContract);
        var created = await firstService.CreateRoomAsync(
            Guid.NewGuid().ToString(),
            new PlayerIdentity("owner", "房主", "Guest"),
            NewCreateRequest(),
            CancellationToken.None);

        var restartedService = CreateService(durableStoreContract);
        var directory = await restartedService.ListRoomsAsync(CancellationToken.None);

        Assert.Contains(directory, room => room.RoomCode == created.RoomCode);
    }

    [Fact]
    public void StateMachine_RejectsSkippingFromAllocatingToPlaying()
    {
        Assert.False(RoomStateMachine.CanTransition(RoomLifecycle.Allocating, RoomLifecycle.Playing));
        Assert.True(RoomStateMachine.CanTransition(RoomLifecycle.Allocating, RoomLifecycle.Waiting));
    }

    private static LobbyService CreateService(ILobbyStore store)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LobbyOptions
        {
            TokenSigningKey = LobbyWebApplicationFactory.SigningKey,
            RoomCodeRetryLimit = 500,
            MaximumPlayersPerRoom = 4
        });
        return new LobbyService(
            store,
            new RoomPasswordService(options, TimeProvider.System),
            new NoOpEventPublisher(),
            new DisabledAllocatorClient(),
            new HmacJoinTicketIssuer(options, TimeProvider.System),
            options,
            TimeProvider.System,
            NullLogger<LobbyService>.Instance);
    }

    private static CreateRoomRequest NewCreateRequest() => new(
        4,
        true,
        true,
        false,
        null,
        new Dictionary<string, object?> { ["ruleId"] = "GuiyangMainstreamV1" });

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
            RuleSnapshot = new Dictionary<string, object?> { ["ruleId"] = "GuiyangMainstreamV1" },
            Lifecycle = RoomLifecycle.Allocating,
            PlayerIds = [ownerPlayerId],
            MatchId = Guid.NewGuid().ToString(),
            StateSequence = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private sealed class NoOpEventPublisher : ILobbyEventPublisher
    {
        public Task PublishAsync(string type, object data, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
