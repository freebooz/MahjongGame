using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.GameRouting;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Rooms;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Storage;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 承载房间创建、加入、关闭、路由和公开目录查询，维护玩家与房间状态机的一致性。
/// </summary>
public sealed partial class LobbyService
{
    /// <summary>
    /// 创建房间并完成分配请求；失败时保持房间状态机和幂等请求语义。
    /// </summary>
    public async Task<RoomOperation> CreateRoomAsync(
        string requestId,
        PlayerIdentity player,
        CreateRoomRequest request,
        CancellationToken cancellationToken)
    {
        // 房间创建是跨存储、Allocator 与事件总线的业务事务，独立跨度用于定位具体失败阶段。
        using var activity = MahjongTelemetry.ActivitySource.StartActivity(
            "Lobby.CreateRoom",
            ActivityKind.Internal);
        activity?.SetTag("mahjong.player_id", player.PlayerId);
        activity?.SetTag("mahjong.request_id", requestId);
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
                Seats = [new RoomSeat(player.PlayerId, 0, now)],
                Password = protectedPassword,
                MatchId = Guid.NewGuid().ToString(),
                StateSequence = 1,
                RoomEpoch = 1,
                RuleSetVersion = ResolveRuleSetVersion(request.RuleSnapshot),
                BuildVersion = options.Allocator.GameServerBuildVersion,
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
            activity?.SetTag("mahjong.room_id", room.RoomId);
            activity?.SetTag("mahjong.match_id", room.MatchId);
            await events.PublishAsync(LobbyEventTypes.RoomUpdated, ToDirectoryItem(room), cancellationToken);
            if (allocator.Enabled)
            {
                try
                {
                    var allocation = await allocator.AllocateForEpochAsync(
                        requestId,
                        room.RoomId,
                        room.MatchId,
                        room.RoomEpoch,
                        cancellationToken);
                    if (allocation.RoomEpoch != room.RoomEpoch)
                    {
                        throw new HttpRequestException(
                            "Allocator returned a stale RoomEpoch.");
                    }
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

    /// <summary>
    /// 校验密码与容量后加入房间；返回已授权路由或仍在分配中的房间操作结果。
    /// </summary>
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

    /// <summary>
    /// 由房主关闭当前房间；并发状态变化通过有限重试收敛，失败时不产生部分关闭。
    /// </summary>
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

    /// <summary>
    /// Atomically removes the authenticated player from their current room.
    /// The final member closes the room; otherwise ownership is transferred
    /// and an interrupted match returns to Waiting so a replacement may join.
    /// </summary>
    public async Task<RoomOperation> LeaveCurrentRoomAsync(
        string requestId,
        PlayerIdentity player,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var room = await store.GetActiveRoomByPlayerAsync(
                    player.PlayerId, cancellationToken)
                ?? throw new LobbyOperationException(
                    LobbyErrorCode.RoomNotFound,
                    "当前玩家不在活动房间中",
                    StatusCodes.Status404NotFound);
            var remainingPlayers = room.PlayerIds
                .Where(playerId => !string.Equals(
                    playerId, player.PlayerId, StringComparison.Ordinal))
                .ToArray();

            LobbyRoom updated;
            if (remainingPlayers.Length == 0)
            {
                var terminalLifecycle =
                    room.Lifecycle is RoomLifecycle.Creating
                        or RoomLifecycle.Playing
                        ? RoomLifecycle.Failed
                        : RoomLifecycle.Closed;
                updated = RoomStateMachine.Transition(
                    room, terminalLifecycle, timeProvider) with
                {
                    PlayerIds = [],
                    Route = null,
                    PendingServerInstanceId = null,
                    LastServerInstanceId =
                        room.Route?.ServerInstanceId
                        ?? room.PendingServerInstanceId
                        ?? room.LastServerInstanceId
                };
            }
            else
            {
                var nextLifecycle =
                    room.Lifecycle is RoomLifecycle.Playing
                        or RoomLifecycle.Settling
                        ? RoomLifecycle.Waiting
                        : room.Lifecycle;
                updated = nextLifecycle == room.Lifecycle
                    ? room with
                    {
                        StateSequence = room.StateSequence + 1,
                        UpdatedAtUtc = timeProvider.GetUtcNow()
                    }
                    : RoomStateMachine.Transition(
                        room, nextLifecycle, timeProvider);
                updated = updated with
                {
                    PlayerIds = remainingPlayers,
                    OwnerPlayerId = string.Equals(
                        room.OwnerPlayerId,
                        player.PlayerId,
                        StringComparison.Ordinal)
                            ? remainingPlayers[0]
                            : room.OwnerPlayerId,
                    EmptySinceUtc = null
                };
            }

            if (!await store.UpdateRoomAsync(updated, cancellationToken))
            {
                continue;
            }

            await events.PublishAsync(
                updated.Lifecycle is RoomLifecycle.Closed
                    or RoomLifecycle.Failed
                    ? LobbyEventTypes.RoomClosed
                    : LobbyEventTypes.RoomUpdated,
                ToDirectoryItem(updated),
                cancellationToken);
            logger.LogInformation(
                "Player left room RequestId={RequestId} RoomId={RoomId} PlayerId={PlayerId} RemainingPlayers={RemainingPlayers}",
                requestId,
                updated.RoomId,
                player.PlayerId,
                remainingPlayers.Length);
            return new RoomOperation(
                requestId,
                updated.RoomId,
                updated.RoomCode,
                updated.Lifecycle,
                0);
        }

        throw new LobbyOperationException(
            LobbyErrorCode.RequestInProgress,
            "房间状态正在变化，请稍后重试",
            StatusCodes.Status409Conflict,
            250);
    }

    /// <summary>
    /// 释放已关闭房间占用的 Dedicated Server；分配器暂时失败只记录待回收状态。
    /// </summary>
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

    /// <summary>
    /// 获取房间成员的短期加入路由；非成员、关闭房间或未完成分配时拒绝返回凭据。
    /// </summary>
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

    /// <summary>
    /// 按大厅权威映射生成重连路由；忽略客户端过期房间提示，防止错误回连。
    /// </summary>
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

    /// <summary>
    /// 返回公开房间目录的只读投影，不暴露密码、结果凭据或内部实例信息。
    /// </summary>
    public async Task<IReadOnlyList<RoomDirectoryItem>> ListRoomsAsync(CancellationToken cancellationToken) =>
        (await store.ListPublicRoomsAsync(cancellationToken)).Select(ToDirectoryItem).ToArray();

    /// <summary>
    /// 确认玩家房间成员资格并签发短期加入票据；不复用客户端提交的旧票据。
    /// </summary>
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
        if (room.Route.RoomEpoch != room.RoomEpoch)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.ServerUnavailable,
                "牌桌服务器路由正在切换",
                StatusCodes.Status503ServiceUnavailable,
                1000);
        }
        var issued = joinTicketIssuer.Issue(player, room, room.Route.ServerInstanceId);
        return room.Route with
        {
            RequestId = requestId,
            PlayerId = player.PlayerId,
            JoinTicket = issued.Ticket,
            TicketExpireAtUtc = issued.ExpiresAtUtc
        };
    }

    /// <summary>
    /// 短暂等待并发分配者写入实例租约；超过窗口后返回最新状态，由上层决定重试。
    /// </summary>
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

    /// <summary>
    /// 将房间实体转换为公开目录投影，显式排除敏感内部字段。
    /// </summary>
    private static RoomDirectoryItem ToDirectoryItem(LobbyRoom room) => new(
        room.RoomCode,
        room.Lifecycle,
        room.PlayerIds.Length,
        room.MaximumPlayers,
        room.Password is not null,
        room.RoundCount);

    /// <summary>
    /// 在任何存储写入前验证创建请求和规则快照，失败时抛出可映射的业务错误。
    /// </summary>
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

    /// <summary>
    /// 深复制规则快照，避免调用方在房间创建后继续修改持久化语义。
    /// </summary>
    private static Dictionary<string, object?> CloneRuleSnapshot(Dictionary<string, object?> snapshot) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
            System.Text.Json.JsonSerializer.Serialize(snapshot))
        ?? throw new InvalidOperationException("Rule snapshot could not be cloned.");

    /// <summary>
    /// 从规则快照提取稳定版本；旧请求未携带 ruleVersion 时使用 legacy-v1，
    /// 确保 Join Ticket 和 DS 启动配置仍能绑定一个明确的规则版本。
    /// </summary>
    private static string ResolveRuleSetVersion(Dictionary<string, object?> snapshot)
    {
        if (!snapshot.TryGetValue("ruleVersion", out var value))
        {
            return "legacy-v1";
        }

        var version = value switch
        {
            string text => text.Trim(),
            System.Text.Json.JsonElement
                { ValueKind: System.Text.Json.JsonValueKind.String } element =>
                element.GetString()?.Trim() ?? string.Empty,
            System.Text.Json.JsonElement
                { ValueKind: System.Text.Json.JsonValueKind.Number } element =>
                element.GetRawText(),
            int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => string.Empty
        };
        return version is { Length: > 0 and <= 64 } ? version : "legacy-v1";
    }

    /// <summary>
    /// 从字符串或 JSON 字符串读取规则标识，并限制为安全字符集合。
    /// </summary>
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

    /// <summary>
    /// 创建统一的无效请求异常，确保 HTTP 状态和大厅错误码一致。
    /// </summary>
    private static LobbyOperationException Invalid(string message) => new(
        LobbyErrorCode.InvalidRequest, message, StatusCodes.Status400BadRequest);
}
