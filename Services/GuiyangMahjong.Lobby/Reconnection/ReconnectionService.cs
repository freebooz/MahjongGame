using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.GameRouting;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Rooms;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Reconnection;

/// <summary>
/// 重连控制面服务，根据玩家权威房间映射签发当前 Epoch 的短期路由。
/// 客户端提交的 RoomId/MatchId 只作为陈旧提示，不能选择服务器或恢复私有手牌。
/// </summary>
public sealed class ReconnectionService(
    IRoomReader rooms,
    IJoinTicketIssuer joinTickets,
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider,
    ILogger<ReconnectionService> logger)
{
    private readonly TimeSpan reconnectionWindow =
        TimeSpan.FromSeconds(
            options.Value.Matchmaking.ReconnectionWindowSeconds);

    /// <summary>
    /// 查询玩家当前路由并签发新票据。
    /// 房间不存在、已终止、没有健康路由或 Epoch 不一致时显式失败，不回退到客户端提示。
    /// </summary>
    public async Task<GameServerRoute> GetRouteAsync(
        string requestId,
        PlayerIdentity player,
        ReconnectRouteRequest request,
        CancellationToken cancellationToken)
    {
        var room = await rooms.GetActiveRoomByPlayerAsync(
                player.PlayerId,
                cancellationToken)
            ?? throw new LobbyOperationException(
                LobbyErrorCode.RoomNotFound,
                "原房间不存在",
                StatusCodes.Status404NotFound);

        if ((!string.IsNullOrWhiteSpace(request.RoomId)
                && !string.Equals(room.RoomId, request.RoomId, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(request.MatchId)
                && !string.Equals(room.MatchId, request.MatchId, StringComparison.Ordinal)))
        {
            logger.LogInformation(
                "重连提示已过期，采用权威路由 RequestId={RequestId} PlayerId={PlayerId} RoomId={RoomId} RoomEpoch={RoomEpoch}",
                requestId,
                player.PlayerId,
                room.RoomId,
                room.RoomEpoch);
        }

        if (!room.PlayerIds.Contains(player.PlayerId, StringComparer.Ordinal))
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest,
                "玩家不属于当前房间",
                StatusCodes.Status403Forbidden);
        }

        if (room.Lifecycle is
            RoomLifecycle.Finished
            or RoomLifecycle.Aborted
            or RoomLifecycle.Archived
            or RoomLifecycle.Terminating)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.RoomClosed,
                "房间已经关闭",
                StatusCodes.Status409Conflict);
        }

        // 只有恢复/挂起状态才应用窗口；正常 Waiting/Playing 房间不能因很久没有控制面写入而误过期。
        if (room.Lifecycle is
                RoomLifecycle.Recovering
                or RoomLifecycle.Suspended
            && timeProvider.GetUtcNow()
                - (room.EmptySinceUtc ?? room.UpdatedAtUtc)
                > reconnectionWindow)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.Timeout,
                "房间重连窗口已结束",
                StatusCodes.Status409Conflict);
        }

        var route = room.Route;
        if (route is null || route.RoomEpoch != room.RoomEpoch)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.ServerUnavailable,
                "牌桌服务器仍在恢复或分配中",
                StatusCodes.Status503ServiceUnavailable,
                1000);
        }

        var issued = joinTickets.Issue(player, room, route.ServerInstanceId);
        return route with
        {
            RequestId = requestId,
            PlayerId = player.PlayerId,
            JoinTicket = issued.Ticket,
            TicketExpireAtUtc = issued.ExpiresAtUtc
        };
    }
}
