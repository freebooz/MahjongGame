using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

public sealed class LobbyService
{
    private readonly ILobbyStore store;
    private readonly IRoomPasswordService passwordService;
    private readonly ILobbyEventPublisher events;
    private readonly IAllocatorClient allocator;
    private readonly IJoinTicketIssuer joinTicketIssuer;
    private readonly IRoomMonitoringStore monitoringStore;
    private readonly LobbyOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LobbyService> logger;

    public LobbyService(
        ILobbyStore store,
        IRoomPasswordService passwordService,
        ILobbyEventPublisher events,
        IAllocatorClient allocator,
        IJoinTicketIssuer joinTicketIssuer,
        IRoomMonitoringStore monitoringStore,
        IOptions<LobbyOptions> options,
        TimeProvider timeProvider,
        ILogger<LobbyService> logger)
    {
        this.store = store;
        this.passwordService = passwordService;
        this.events = events;
        this.allocator = allocator;
        this.joinTicketIssuer = joinTicketIssuer;
        this.monitoringStore = monitoringStore;
        this.options = options.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<RoomOperation> CreateRoomAsync(
        string requestId,
        PlayerIdentity player,
        CreateRoomRequest request,
        CancellationToken cancellationToken)
    {
        ValidateCreateRequest(request);
        if (await store.GetActiveRoomByPlayerAsync(player.PlayerId, cancellationToken) is not null)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.RequestInProgress,
                "玩家已有未关闭的牌桌",
                StatusCodes.Status409Conflict);
        }
        var protectedPassword = request.PasswordProtected
            ? passwordService.Protect(request.Password!)
            : null;

        for (var attempt = 0; attempt < options.RoomCodeRetryLimit; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            var room = new LobbyRoom
            {
                RoomId = Guid.NewGuid().ToString(),
                RoomCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"),
                OwnerPlayerId = player.PlayerId,
                RoundCount = request.RoundCount,
                PublicRoom = request.PublicRoom,
                AutoStart = request.AutoStart,
                MaximumPlayers = options.MaximumPlayersPerRoom,
                RuleSnapshot = CloneRuleSnapshot(request.RuleSnapshot),
                Lifecycle = RoomLifecycle.Allocating,
                PlayerIds = [player.PlayerId],
                Password = protectedPassword,
                MatchId = Guid.NewGuid().ToString(),
                StateSequence = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            var createResult = await store.TryCreateRoomAsync(room, cancellationToken);
            if (createResult.Status == CreateRoomStatus.RoomCodeConflict) continue;
            if (createResult.Status == CreateRoomStatus.PlayerAlreadyActive)
            {
                throw new LobbyOperationException(
                    LobbyErrorCode.RequestInProgress,
                    "玩家已有未关闭的牌桌",
                    StatusCodes.Status409Conflict);
            }

            logger.LogInformation(
                "房间创建请求已接受 RequestId={RequestId} RoomId={RoomId} PlayerId={PlayerId} PasswordProtected={PasswordProtected}",
                requestId, room.RoomId, player.PlayerId, room.Password is not null);
            await events.PublishAsync(LobbyEventTypes.RoomUpdated, ToDirectoryItem(room), cancellationToken);
            if (allocator.Enabled)
            {
                try
                {
                    var allocation = await allocator.AllocateAsync(
                        requestId,
                        room.RoomId,
                        room.MatchId,
                        cancellationToken);
                    room = room with
                    {
                        PendingServerInstanceId = allocation.ServerInstanceId,
                        StateSequence = room.StateSequence + 1,
                        UpdatedAtUtc = timeProvider.GetUtcNow()
                    };
                    if (!await store.UpdateRoomAsync(room, cancellationToken))
                    {
                        throw new HttpRequestException("Allocated room binding could not be persisted.");
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    room = RoomStateMachine.Transition(room, RoomLifecycle.Failed, timeProvider);
                    await store.UpdateRoomAsync(room, cancellationToken);
                    await events.PublishAsync(LobbyEventTypes.RoomClosed, ToDirectoryItem(room), cancellationToken);
                    throw new LobbyOperationException(
                        LobbyErrorCode.ServerUnavailable,
                        "GameServer allocator is temporarily unavailable.",
                        StatusCodes.Status503ServiceUnavailable,
                        1000);
                }
            }
            return new RoomOperation(requestId, room.RoomId, room.RoomCode, room.Lifecycle);
        }

        throw new LobbyOperationException(
            LobbyErrorCode.InternalError,
            "暂时无法生成唯一房间号，请稍后重试",
            StatusCodes.Status503ServiceUnavailable,
            1000);
    }

    public async Task<object> JoinRoomAsync(
        string requestId,
        PlayerIdentity player,
        string roomCode,
        JoinRoomRequest request,
        CancellationToken cancellationToken)
    {
        if (roomCode.Length != 6 || !roomCode.All(char.IsAsciiDigit))
        {
            throw Invalid("房间号必须为 6 位数字");
        }
        if (request.ClientProtocolVersion != options.ProtocolVersion)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.VersionMismatch, "客户端协议版本不兼容", StatusCodes.Status409Conflict);
        }

        var room = await store.GetRoomByCodeAsync(roomCode, cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound, "房间不存在", StatusCodes.Status404NotFound);
        var now = timeProvider.GetUtcNow();
        room = await store.ReconcileWaitingRoomMembersAsync(
                roomCode,
                player.PlayerId,
                now.AddSeconds(-options.PlayerReservationTimeoutSeconds),
                now,
                cancellationToken)
            ?? room;
        var activeRoom = await store.GetActiveRoomByPlayerAsync(player.PlayerId, cancellationToken);
        if (activeRoom is not null && activeRoom.RoomId != room.RoomId)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.RequestInProgress,
                "玩家已在其他未关闭牌桌中",
                StatusCodes.Status409Conflict);
        }

        var passwordResult = passwordService.Verify(player.PlayerId, room.RoomId, room.Password, request.Password);
        switch (passwordResult.Status)
        {
            case PasswordVerificationStatus.Required:
                throw new LobbyOperationException(
                    LobbyErrorCode.PasswordRequired, "请输入房间密码", StatusCodes.Status400BadRequest);
            case PasswordVerificationStatus.Wrong:
                logger.LogWarning(
                    "房间密码验证失败 RequestId={RequestId} RoomId={RoomId} PlayerId={PlayerId}",
                    requestId, room.RoomId, player.PlayerId);
                throw new LobbyOperationException(
                    LobbyErrorCode.WrongPassword, "房间密码错误", StatusCodes.Status403Forbidden);
            case PasswordVerificationStatus.RateLimited:
                logger.LogWarning(
                    "房间密码尝试被限流 RequestId={RequestId} RoomId={RoomId} PlayerId={PlayerId}",
                    requestId, room.RoomId, player.PlayerId);
                throw new LobbyOperationException(
                    LobbyErrorCode.RateLimited,
                    "密码尝试次数过多，请稍后重试",
                    StatusCodes.Status429TooManyRequests,
                    passwordResult.RetryAfterMilliseconds);
        }

        var added = await store.TryAddPlayerAsync(roomCode, player.PlayerId, cancellationToken);
        room = added.Room ?? room;
        switch (added.Status)
        {
            case AddPlayerStatus.RoomNotFound:
                throw new LobbyOperationException(
                    LobbyErrorCode.RoomNotFound, "房间不存在", StatusCodes.Status404NotFound);
            case AddPlayerStatus.RoomClosed:
                throw new LobbyOperationException(
                    LobbyErrorCode.RoomClosed, "房间已关闭或牌局已经开始", StatusCodes.Status409Conflict);
            case AddPlayerStatus.RoomFull:
                throw new LobbyOperationException(
                    LobbyErrorCode.RoomFull, "房间人数已满", StatusCodes.Status409Conflict);
            case AddPlayerStatus.AdmissionProhibited:
                throw new LobbyOperationException(
                    LobbyErrorCode.RoomClosed,
                    "房间已禁止新玩家加入",
                    StatusCodes.Status409Conflict);
            case AddPlayerStatus.AlreadyInAnotherRoom:
                throw new LobbyOperationException(
                    LobbyErrorCode.RequestInProgress,
                    "玩家已在其他未关闭牌桌中",
                    StatusCodes.Status409Conflict);
        }

        logger.LogInformation(
            "玩家加入房间 RequestId={RequestId} RoomId={RoomId} PlayerId={PlayerId}",
            requestId, room.RoomId, player.PlayerId);
        await events.PublishAsync(LobbyEventTypes.RoomUpdated, ToDirectoryItem(room), cancellationToken);
        return room.Route is not null
            ? GetAuthorizedRoute(requestId, player, room)
            : new RoomOperation(requestId, room.RoomId, room.RoomCode, room.Lifecycle);
    }

    public async Task<RoomOperation> CloseOwnedRoomAsync(
        string requestId,
        PlayerIdentity player,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var room = await store.GetActiveRoomByPlayerAsync(player.PlayerId, cancellationToken)
                ?? throw new LobbyOperationException(
                    LobbyErrorCode.RoomNotFound,
                    "当前玩家没有可关闭的活动房间",
                    StatusCodes.Status404NotFound);
            if (!string.Equals(room.OwnerPlayerId, player.PlayerId, StringComparison.Ordinal))
            {
                throw new LobbyOperationException(
                    LobbyErrorCode.InvalidRequest,
                    "只有房主可以关闭并释放房间",
                    StatusCodes.Status403Forbidden);
            }

            var serverInstanceId = room.Route?.ServerInstanceId ?? room.PendingServerInstanceId;
            var terminalLifecycle = room.Lifecycle == RoomLifecycle.Playing
                ? RoomLifecycle.Failed
                : RoomLifecycle.Closed;
            if (!RoomStateMachine.CanTransition(room.Lifecycle, terminalLifecycle))
            {
                throw new LobbyOperationException(
                    LobbyErrorCode.InvalidRequest,
                    "当前房间状态不允许房主关闭",
                    StatusCodes.Status409Conflict);
            }

            var closedRoom = RoomStateMachine.Transition(room, terminalLifecycle, timeProvider) with
            {
                Route = null,
                PendingServerInstanceId = null,
                LastServerInstanceId = serverInstanceId ?? room.LastServerInstanceId
            };
            if (!await store.UpdateRoomAsync(closedRoom, cancellationToken))
            {
                continue;
            }

            await events.PublishAsync(LobbyEventTypes.RoomClosed, ToDirectoryItem(closedRoom), cancellationToken);
            logger.LogInformation(
                "Owner closed room RequestId={RequestId} RoomId={RoomId} PlayerId={PlayerId}",
                requestId,
                closedRoom.RoomId,
                player.PlayerId);
            return new RoomOperation(
                requestId,
                closedRoom.RoomId,
                closedRoom.RoomCode,
                closedRoom.Lifecycle,
                0);
        }

        throw new LobbyOperationException(
            LobbyErrorCode.RequestInProgress,
            "房间状态正在变化，请稍后重试",
            StatusCodes.Status409Conflict,
            250);
    }

    public async Task ReleaseClosedRoomServerAsync(
        string requestId,
        string roomId,
        CancellationToken cancellationToken)
    {
        if (!allocator.Enabled) return;
        var room = await store.GetRoomByIdAsync(roomId, cancellationToken);
        var serverInstanceId = room?.LastServerInstanceId;
        if (room is null
            || room.Lifecycle is not RoomLifecycle.Closed and not RoomLifecycle.Failed
            || string.IsNullOrEmpty(serverInstanceId))
        {
            return;
        }

        try
        {
            await allocator.DrainAsync(requestId, serverInstanceId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                exception,
                "Owner closed room but allocator drain is pending RequestId={RequestId} RoomId={RoomId} InstanceId={InstanceId}",
                requestId,
                roomId,
                serverInstanceId);
        }
    }

    public async Task<GameServerRoute> GetRouteAsync(
        string requestId,
        PlayerIdentity player,
        string roomCode,
        CancellationToken cancellationToken)
    {
        var room = await store.GetRoomByCodeAsync(roomCode, cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound, "房间不存在", StatusCodes.Status404NotFound);
        return GetAuthorizedRoute(requestId, player, room);
    }

    public async Task<GameServerRoute> GetReconnectRouteAsync(
        string requestId,
        PlayerIdentity player,
        ReconnectRouteRequest request,
        CancellationToken cancellationToken)
    {
        var room = await store.GetActiveRoomByPlayerAsync(player.PlayerId, cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound, "原房间不存在", StatusCodes.Status404NotFound);
        if ((!string.IsNullOrWhiteSpace(request.RoomId)
                && !string.Equals(room.RoomId, request.RoomId, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(request.MatchId)
                && !string.Equals(room.MatchId, request.MatchId, StringComparison.Ordinal)))
            logger.LogInformation(
                "重连提示已过期，使用大厅权威映射 RequestId={RequestId} PlayerId={PlayerId} RoomId={RoomId}",
                requestId, player.PlayerId, room.RoomId);
        return GetAuthorizedRoute(requestId, player, room);
    }

    public async Task<IReadOnlyList<RoomDirectoryItem>> ListRoomsAsync(CancellationToken cancellationToken) =>
        (await store.ListPublicRoomsAsync(cancellationToken)).Select(ToDirectoryItem).ToArray();

    public async Task<GameServerRegistrationAck> RegisterGameServerAsync(
        string requestId,
        GameServerRegistration registration,
        CancellationToken cancellationToken)
    {
        if (!allocator.Enabled)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.BackendNotConfigured,
                "Allocator integration is disabled.",
                StatusCodes.Status503ServiceUnavailable);
        }
        var room = await WaitForPendingAllocationAsync(
                registration.RoomId,
                registration.ServerInstanceId,
                cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound, "Room was not found.", StatusCodes.Status404NotFound);
        if (room.MatchId != registration.MatchId
            || room.Lifecycle != RoomLifecycle.Allocating
            || room.PendingServerInstanceId != registration.ServerInstanceId)
        {
            throw Invalid("GameServer registration does not match the room allocation.");
        }

        var acknowledgement = await allocator.ConfirmRegistrationAsync(
            requestId, registration, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var resultCredential = CreateResultCredential();
        room = RoomStateMachine.Transition(room, RoomLifecycle.Waiting, timeProvider) with
        {
            PendingServerInstanceId = null,
            Route = new GameServerRoute(
                requestId,
                string.Empty,
                room.RoomId,
                registration.ServerInstanceId,
                room.MatchId,
                registration.ListenIp,
                registration.ListenPort,
                string.Empty,
                now),
            LastServerInstanceId = registration.ServerInstanceId,
            ResultCredentialHash = HashCredential(resultCredential)
        };
        if (!await store.UpdateRoomAsync(room, cancellationToken))
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InternalError,
                "Room route could not be persisted.",
                StatusCodes.Status500InternalServerError);
        }
        await events.PublishAsync(LobbyEventTypes.ServerAssigned, ToDirectoryItem(room), cancellationToken);
        await events.PublishAsync(LobbyEventTypes.RoomUpdated, ToDirectoryItem(room), cancellationToken);
        return new GameServerRegistrationAck(
            requestId,
            acknowledgement.Accepted,
            acknowledgement.HeartbeatIntervalSeconds,
            acknowledgement.HeartbeatCredential,
            resultCredential,
            new ManagedRoomBootstrap(
                room.RoomId,
                room.RoomCode,
                room.MatchId,
                room.OwnerPlayerId,
                room.RoundCount,
                room.MaximumPlayers,
                room.PublicRoom,
                room.AutoStart,
                room.Password is not null,
                CloneRuleSnapshot(room.RuleSnapshot)));
    }

    public async Task RecordGameServerHeartbeatAsync(
        string requestId,
        string serverInstanceId,
        GameServerHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        await allocator.RecordHeartbeatAsync(requestId, serverInstanceId, heartbeat, cancellationToken);
        var room = await store.GetRoomByIdAsync(heartbeat.RoomId, cancellationToken);
        if (room is null || room.Route?.ServerInstanceId != serverInstanceId) return;

        var now = timeProvider.GetUtcNow();
        if (heartbeat.ConnectedPlayerIds is { } connectedPlayerIds)
        {
            var distinctPlayerIds = connectedPlayerIds
                .Where(playerId => !string.IsNullOrWhiteSpace(playerId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (distinctPlayerIds.Length != connectedPlayerIds.Length
                || distinctPlayerIds.Length != heartbeat.ConnectedPlayers
                || distinctPlayerIds.Length > room.MaximumPlayers
                || distinctPlayerIds.Any(playerId => playerId.Length > 80))
            {
                throw Invalid("GameServer heartbeat player membership is invalid.");
            }
            await store.RefreshConnectedPlayersAsync(
                room.RoomId, distinctPlayerIds, now, cancellationToken);
        }

        await RecordRuntimeTelemetryAsync(
            requestId, serverInstanceId, room, heartbeat, now, cancellationToken);

        var reported = heartbeat.RoomLifecycle switch
        {
            "Playing" => RoomLifecycle.Playing,
            "Settling" => RoomLifecycle.Settling,
            _ => room.Lifecycle
        };
        var updated = room;
        var changed = false;
        if (reported != room.Lifecycle && RoomStateMachine.CanTransition(room.Lifecycle, reported))
        {
            updated = RoomStateMachine.Transition(room, reported, timeProvider);
            changed = true;
        }

        var shouldDrainEmptyRoom = false;
        if (updated.Lifecycle is RoomLifecycle.Waiting or RoomLifecycle.Playing or RoomLifecycle.Settling)
        {
            if (heartbeat.ConnectedPlayers == 0)
            {
                var emptySinceUtc = updated.EmptySinceUtc ?? now;
                if (!changed
                    && now - emptySinceUtc >= TimeSpan.FromSeconds(options.EmptyRoomTimeoutSeconds))
                {
                    var terminalLifecycle = updated.Lifecycle == RoomLifecycle.Playing
                        ? RoomLifecycle.Failed
                        : RoomLifecycle.Closed;
                    updated = RoomStateMachine.Transition(updated, terminalLifecycle, timeProvider) with
                    {
                        Route = null,
                        PendingServerInstanceId = null,
                        LastServerInstanceId = serverInstanceId,
                        EmptySinceUtc = emptySinceUtc
                    };
                    changed = true;
                    shouldDrainEmptyRoom = true;
                }
                else if (updated.EmptySinceUtc is null)
                {
                    updated = updated with
                    {
                        EmptySinceUtc = emptySinceUtc,
                        StateSequence = changed ? updated.StateSequence : updated.StateSequence + 1,
                        UpdatedAtUtc = now
                    };
                    changed = true;
                }
            }
            else if (updated.EmptySinceUtc is not null)
            {
                updated = updated with
                {
                    EmptySinceUtc = null,
                    StateSequence = changed ? updated.StateSequence : updated.StateSequence + 1,
                    UpdatedAtUtc = now
                };
                changed = true;
            }
        }

        if (!changed || !await store.UpdateRoomAsync(updated, cancellationToken)) return;
        await events.PublishAsync(
            shouldDrainEmptyRoom ? LobbyEventTypes.RoomClosed : LobbyEventTypes.RoomUpdated,
            ToDirectoryItem(updated),
            cancellationToken);
        if (!shouldDrainEmptyRoom) return;

        logger.LogInformation(
            "Empty room timed out and is being reclaimed RequestId={RequestId} RoomId={RoomId} InstanceId={InstanceId}",
            requestId,
            updated.RoomId,
            serverInstanceId);
        try
        {
            await allocator.DrainAsync(requestId, serverInstanceId, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                exception,
                "Empty room closed but Dedicated Server drain failed RoomId={RoomId} InstanceId={InstanceId}",
                updated.RoomId,
                serverInstanceId);
        }
    }

    private async Task RecordRuntimeTelemetryAsync(
        string requestId,
        string serverInstanceId,
        LobbyRoom room,
        GameServerHeartbeat heartbeat,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateRuntimeMetric(heartbeat.ServerTickMilliseconds, 0, 10_000, "server tick");
        ValidateRuntimeMetric(heartbeat.ServerFramesPerSecond, 0, 1_000, "server FPS");
        ValidateRuntimeMetric(heartbeat.ProcessCpuPercent, 0, 10_000, "process CPU");
        if (heartbeat.RpcReceivedCount is < 0
            || heartbeat.ProcessMemoryBytes is < 0
            || heartbeat.NetworkIngressBytes is < 0
            || heartbeat.NetworkEgressBytes is < 0)
        {
            throw Invalid("GameServer heartbeat cumulative metric is invalid.");
        }

        var players = heartbeat.Players
            ?? (heartbeat.ConnectedPlayerIds ?? [])
                .Select(playerId => new PlayerRuntimeTelemetry(
                    playerId, -1, "Connected", null, null, null))
                .ToArray();
        var distinctPlayers = players
            .Select(player => player.PlayerId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctPlayers.Length != players.Length
            || players.Length > room.MaximumPlayers
            || players.Any(player =>
                string.IsNullOrWhiteSpace(player.PlayerId)
                || player.PlayerId.Length > 80
                || player.SeatIndex is < -1 or > 3
                || player.LatencyMilliseconds is < 0 or > 120_000
                || !IsFinite(player.LatencyMilliseconds)
                || player.ConnectionState is not ("Connected" or "Disconnected" or "Reconnecting")))
        {
            throw Invalid("GameServer heartbeat player telemetry is invalid.");
        }

        var previous = await monitoringStore.GetRuntimeAsync(room.RoomId, cancellationToken);
        var runtime = new RoomRuntimeTelemetry(
            room.RoomId,
            serverInstanceId,
            observedAtUtc,
            heartbeat.GameStartedAtUtc ?? previous?.GameStartedAtUtc,
            heartbeat.RoomLifecycle,
            heartbeat.RoundId,
            heartbeat.ConnectedPlayers,
            heartbeat.ServerTickMilliseconds,
            heartbeat.ServerFramesPerSecond,
            heartbeat.RpcReceivedCount,
            heartbeat.ProcessMemoryBytes,
            heartbeat.ProcessCpuPercent,
            heartbeat.NetworkIngressBytes,
            heartbeat.NetworkEgressBytes,
            heartbeat.BuildVersion,
            players);
        await monitoringStore.SetRuntimeAsync(runtime, cancellationToken);

        if (previous is null)
        {
            await AppendRuntimeEventAsync(
                room,
                requestId,
                "ServerTelemetryStarted",
                new Dictionary<string, object?>
                {
                    ["serverInstanceId"] = serverInstanceId,
                    ["buildVersion"] = heartbeat.BuildVersion
                },
                observedAtUtc,
                cancellationToken);
            return;
        }

        if (!previous.Lifecycle.Equals(runtime.Lifecycle, StringComparison.Ordinal))
        {
            await AppendRuntimeEventAsync(
                room,
                requestId,
                "RoomLifecycleChanged",
                new Dictionary<string, object?>
                {
                    ["from"] = previous.Lifecycle,
                    ["to"] = runtime.Lifecycle,
                    ["roundId"] = runtime.CurrentRound
                },
                observedAtUtc,
                cancellationToken);
        }

        var previousConnections = previous.Players.ToDictionary(
            player => player.PlayerId, player => player.ConnectionState, StringComparer.Ordinal);
        foreach (var player in runtime.Players)
        {
            if (!previousConnections.TryGetValue(player.PlayerId, out var oldState)
                || oldState.Equals(player.ConnectionState, StringComparison.Ordinal))
            {
                continue;
            }
            await AppendRuntimeEventAsync(
                room,
                requestId,
                "PlayerConnectionChanged",
                new Dictionary<string, object?>
                {
                    ["playerId"] = player.PlayerId,
                    ["from"] = oldState,
                    ["to"] = player.ConnectionState,
                    ["latencyMilliseconds"] = player.LatencyMilliseconds
                },
                observedAtUtc,
                cancellationToken);
        }
    }

    private Task AppendRuntimeEventAsync(
        LobbyRoom room,
        string traceId,
        string eventType,
        Dictionary<string, object?> data,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken) =>
        monitoringStore.AppendEventAsync(
            room.RoomId,
            new RoomTimelineEvent(
                Guid.NewGuid().ToString(),
                eventType,
                occurredAtUtc,
                room.StateSequence,
                traceId,
                data),
            cancellationToken);

    private static void ValidateRuntimeMetric(
        double? value, double minimum, double maximum, string name)
    {
        if (value.HasValue
            && (!double.IsFinite(value.Value) || value < minimum || value > maximum))
        {
            throw Invalid($"GameServer heartbeat {name} metric is invalid.");
        }
    }

    private static bool IsFinite(double? value) =>
        !value.HasValue || double.IsFinite(value.Value);

    public async Task<MatchResultAck> SubmitMatchResultAsync(
        string requestId,
        string matchId,
        string resultCredential,
        MatchResultReport report,
        CancellationToken cancellationToken) => await SubmitMatchResultCoreAsync(
            requestId, matchId, resultCredential, report, trustedRecovery: false, cancellationToken);

    public async Task<MatchResultAck> RecoverMatchResultAsync(
        string requestId,
        string matchId,
        MatchResultReport report,
        CancellationToken cancellationToken) => await SubmitMatchResultCoreAsync(
            requestId, matchId, string.Empty, report, trustedRecovery: true, cancellationToken);

    private async Task<MatchResultAck> SubmitMatchResultCoreAsync(
        string requestId,
        string matchId,
        string resultCredential,
        MatchResultReport report,
        bool trustedRecovery,
        CancellationToken cancellationToken)
    {
        ValidateMatchResult(matchId, report);
        var room = await store.GetRoomByIdAsync(report.RoomId, cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound, "结算房间不存在", StatusCodes.Status404NotFound);
        var authoritativeInstanceId = room.LastServerInstanceId ?? room.Route?.ServerInstanceId;
        var scopeMatches = trustedRecovery
            ? authoritativeInstanceId == report.ServerInstanceId
            : room.Lifecycle == RoomLifecycle.Closed
                ? authoritativeInstanceId is null || authoritativeInstanceId == report.ServerInstanceId
                : room.Route?.ServerInstanceId == report.ServerInstanceId;
        if (room.MatchId != matchId
            || !scopeMatches
            || (!trustedRecovery && !VerifyCredential(resultCredential, room.ResultCredentialHash)))
        {
            throw new LobbyOperationException(
                LobbyErrorCode.SessionExpired,
                "GameServer 结算凭据或作用域无效",
                StatusCodes.Status401Unauthorized);
        }
        if (room.Lifecycle is not RoomLifecycle.Settling and not RoomLifecycle.Closed
            && !(trustedRecovery && room.Lifecycle == RoomLifecycle.Failed))
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest,
                "房间尚未进入最终结算阶段",
                StatusCodes.Status409Conflict);
        }
        var expectedPlayers = room.PlayerIds.Order(StringComparer.Ordinal).ToArray();
        var reportedPlayers = report.Players.Select(player => player.PlayerId).Order(StringComparer.Ordinal).ToArray();
        if (report.CompletedRounds != room.RoundCount
            || !expectedPlayers.SequenceEqual(reportedPlayers, StringComparer.Ordinal))
        {
            throw Invalid("结算局数或玩家集合与权威房间不一致");
        }

        var closedRoom = room.Lifecycle == RoomLifecycle.Closed
            ? room
            : RoomStateMachine.Transition(room, RoomLifecycle.Closed, timeProvider) with
            {
                Route = null,
                PendingServerInstanceId = null,
                LastServerInstanceId = authoritativeInstanceId ?? report.ServerInstanceId
            };
        var finalizeStatus = await store.FinalizeMatchAsync(closedRoom, report, cancellationToken);
        if (finalizeStatus == FinalizeMatchStatus.Conflict)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest,
                "相同结算序号已提交不同结果",
                StatusCodes.Status409Conflict);
        }
        var duplicate = finalizeStatus == FinalizeMatchStatus.Duplicate;
        if (!duplicate)
        {
            logger.LogInformation(
                "牌局结算已持久化 RequestId={RequestId} RoomId={RoomId} MatchId={MatchId} ResultSequence={ResultSequence}",
                requestId, room.RoomId, matchId, report.ResultSequence);
            await events.PublishAsync(LobbyEventTypes.RoomClosed, ToDirectoryItem(closedRoom), cancellationToken);
        }
        if (allocator.Enabled && !trustedRecovery)
        {
            try
            {
                await allocator.DrainAsync(requestId, report.ServerInstanceId, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                throw new LobbyOperationException(
                    LobbyErrorCode.ServerUnavailable,
                    "结算已保存，GameServer 回收暂未完成，请重试确认",
                    StatusCodes.Status503ServiceUnavailable,
                    1000);
            }
        }
        return new MatchResultAck(requestId, matchId, report.ResultSequence, true, duplicate);
    }

    public async Task MarkGameServerFailedAsync(
        GameServerFailure failure,
        CancellationToken cancellationToken)
    {
        var room = await store.GetRoomByIdAsync(failure.RoomId, cancellationToken);
        if (room is null
            || (room.Route?.ServerInstanceId != failure.ServerInstanceId
                && room.PendingServerInstanceId != failure.ServerInstanceId)
            || room.Lifecycle is RoomLifecycle.Closed or RoomLifecycle.Failed)
        {
            return;
        }

        room = RoomStateMachine.Transition(room, RoomLifecycle.Failed, timeProvider) with
        {
            Route = null,
            PendingServerInstanceId = null
        };
        await store.UpdateRoomAsync(room, cancellationToken);
        logger.LogWarning(
            "GameServer failure closed room RoomId={RoomId} InstanceId={InstanceId} Reason={Reason}",
            failure.RoomId,
            failure.ServerInstanceId,
            failure.Reason);
        await events.PublishAsync(LobbyEventTypes.RoomClosed, ToDirectoryItem(room), cancellationToken);
    }

    private GameServerRoute GetAuthorizedRoute(string requestId, PlayerIdentity player, LobbyRoom room)
    {
        if (!room.PlayerIds.Contains(player.PlayerId, StringComparer.Ordinal))
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest, "玩家尚未加入该房间", StatusCodes.Status403Forbidden);
        }
        if (room.Lifecycle is RoomLifecycle.Closed or RoomLifecycle.Failed)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.RoomClosed, "房间已经关闭", StatusCodes.Status409Conflict);
        }
        if (room.Route is null)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.ServerUnavailable,
                "牌桌服务器仍在分配中",
                StatusCodes.Status503ServiceUnavailable,
                1000);
        }
        var issued = joinTicketIssuer.Issue(
            player.PlayerId,
            player.DisplayName,
            room,
            room.Route.ServerInstanceId);
        return room.Route with
        {
            RequestId = requestId,
            PlayerId = player.PlayerId,
            JoinTicket = issued.Ticket,
            TicketExpireAtUtc = issued.ExpiresAtUtc
        };
    }

    private async Task<LobbyRoom?> WaitForPendingAllocationAsync(
        string roomId,
        string serverInstanceId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var room = await store.GetRoomByIdAsync(roomId, cancellationToken);
            if (room is null
                || room.PendingServerInstanceId == serverInstanceId
                || room.Lifecycle != RoomLifecycle.Allocating)
            {
                return room;
            }
            await Task.Delay(25, cancellationToken);
        }
        return await store.GetRoomByIdAsync(roomId, cancellationToken);
    }

    private static RoomDirectoryItem ToDirectoryItem(LobbyRoom room) => new(
        room.RoomCode,
        room.Lifecycle,
        room.PlayerIds.Length,
        room.MaximumPlayers,
        room.Password is not null,
        room.RoundCount);

    private static void ValidateCreateRequest(CreateRoomRequest request)
    {
        if (request.RoundCount is < 1 or > 16) throw Invalid("局数必须为 1 到 16");
        if (request.RuleSnapshot is null) throw Invalid("缺少规则快照");
        if (request.RuleSnapshot.Count is < 1 or > 64
            || System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request.RuleSnapshot).Length > 16 * 1024
            || !request.RuleSnapshot.TryGetValue("ruleId", out var ruleIdValue)
            || !TryReadRuleId(ruleIdValue, out var ruleId)
            || ruleId.Length > 64)
        {
            throw Invalid("规则快照格式无效");
        }
        if (request.PasswordProtected && (request.Password is null || request.Password.Length is < 6 or > 12))
        {
            throw Invalid("房间密码必须为 6 到 12 个字符");
        }
        if (!request.PasswordProtected && !string.IsNullOrEmpty(request.Password))
        {
            throw Invalid("非密码房不得提交密码");
        }
    }

    private static Dictionary<string, object?> CloneRuleSnapshot(Dictionary<string, object?> snapshot) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
            System.Text.Json.JsonSerializer.Serialize(snapshot))
        ?? throw new InvalidOperationException("Rule snapshot could not be cloned.");

    private static bool TryReadRuleId(object? value, out string ruleId)
    {
        ruleId = value switch
        {
            string text => text.Trim(),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element =>
                element.GetString()?.Trim() ?? string.Empty,
            _ => string.Empty
        };
        return ruleId.Length > 0 && ruleId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static LobbyOperationException Invalid(string message) => new(
        LobbyErrorCode.InvalidRequest, message, StatusCodes.Status400BadRequest);

    private static void ValidateMatchResult(string matchId, MatchResultReport report)
    {
        if (!Guid.TryParse(matchId, out _)
            || !Guid.TryParse(report.RoomId, out _)
            || !Guid.TryParse(report.ServerInstanceId, out _)
            || report.ResultSequence < 1
            || report.CompletedRounds is < 1 or > 16
            || report.Players.Length is < 1 or > 4
            || report.Players.Select(player => player.PlayerId).Distinct(StringComparer.Ordinal).Count()
                != report.Players.Length
            || report.Players.Select(player => player.SeatIndex).Distinct().Count() != report.Players.Length
            || report.Players.Select(player => player.Rank).Distinct().Count() != report.Players.Length
            || report.Players.Any(player => string.IsNullOrWhiteSpace(player.PlayerId)
                || player.PlayerId.Length > 80
                || player.SeatIndex is < 0 or > 3
                || player.Rank is < 1 or > 4))
        {
            throw Invalid("结算结果格式无效");
        }
    }

    private static string CreateResultCredential() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashCredential(string credential) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private static bool VerifyCredential(string supplied, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(supplied) || supplied.Length > 256 || string.IsNullOrEmpty(expectedHash))
            return false;
        var suppliedHash = Encoding.ASCII.GetBytes(HashCredential(supplied.Trim()));
        var expected = Encoding.ASCII.GetBytes(expectedHash);
        var valid = suppliedHash.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(suppliedHash, expected);
        CryptographicOperations.ZeroMemory(suppliedHash);
        return valid;
    }
}
