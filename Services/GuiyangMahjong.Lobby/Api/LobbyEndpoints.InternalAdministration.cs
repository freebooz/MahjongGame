using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Api;

/// <summary>
/// Lobby 内部管理命令路由分区。
/// 仅接受受信管理凭据，并通过幂等键、乐观并发版本和审计事件保护玩家断连及房间控制操作。
/// </summary>
public static partial class LobbyEndpoints
{
    /// <summary>
    /// 注册玩家断连和房间控制端点；调用方必须提供管理凭据与稳定幂等键，失败时不允许部分覆盖房间状态。
    /// </summary>
    private static void MapInternalAdministrationEndpoints(WebApplication app)
    {
        app.MapPost("/internal/admin/players/{playerId}/disconnect", async (
            string playerId,
            AdminDisconnectPlayerRequest request,
            HttpContext context,
            IPlayerAccessRevocationStore revocations,
            IOnlinePresenceService presence,
            IIdempotencyStore idempotency,
            LobbyEventHub eventHub,
            IOptions<LobbyOptions> options,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalCredential(context, options.Value.ManagementCommandToken))
                return Results.Unauthorized();
            var key = RequireIdempotencyKey(context);
            var now = timeProvider.GetUtcNow();
            if (playerId.Length is < 1 or > 80
                || (request.Reason ?? string.Empty).Trim().Length is < 5 or > 500
                || (request.TraceId ?? string.Empty).Trim().Length is < 8 or > 64
                || request.EffectiveAtUtc < now.AddHours(-24)
                || request.EffectiveAtUtc > now.AddMinutes(1))
            {
                return Results.BadRequest();
            }
            var response = await idempotency.ExecuteAsync(
                $"admin-disconnect:{playerId}:{key}",
                async () =>
                {
                    // 先提升权威撤销水位，再清理在线投影和连接，避免断连后旧令牌立即重连。
                    var revokedBefore = await revocations.RevokeBeforeAsync(
                        playerId,
                        request.EffectiveAtUtc,
                        cancellationToken);
                    await presence.RemoveAsync(playerId, cancellationToken);
                    await eventHub.DisconnectPlayerAsync(playerId, cancellationToken);
                    return new IdempotentHttpResponse(
                        StatusCodes.Status200OK,
                        System.Text.Json.JsonSerializer.SerializeToElement(
                            new AdminDisconnectPlayerResult(
                                playerId,
                                revokedBefore,
                                false),
                            new System.Text.Json.JsonSerializerOptions(
                                System.Text.Json.JsonSerializerDefaults.Web)));
                },
                cancellationToken);
            return Results.Json(response.Body, statusCode: response.StatusCode);
        });

        app.MapPost("/internal/admin/rooms/{roomId}/controls", async (
            string roomId,
            AdminUpdateRoomControlRequest request,
            HttpContext context,
            ILobbyStore store,
            IRoomMonitoringStore monitoring,
            IIdempotencyStore idempotency,
            LobbyEventHub eventHub,
            LobbyService lobbyService,
            IOptions<LobbyOptions> options,
            TimeProvider timeProvider,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalCredential(context, options.Value.ManagementCommandToken))
                return Results.Unauthorized();
            var key = RequireIdempotencyKey(context);
            if (roomId.Length is < 1 or > 80
                || request.ActionType is not (
                    nameof(AdminManagementRoomAction.MarkRoomAbnormal)
                    or nameof(AdminManagementRoomAction.ProhibitNewPlayers)
                    or nameof(AdminManagementRoomAction.EnableMaintenanceMode)
                    or nameof(AdminManagementRoomAction.ForceDissolveRoom))
                || request.ExpectedStateSequence < 1
                || (request.Reason ?? string.Empty).Trim().Length is < 5 or > 500
                || (request.TraceId ?? string.Empty).Trim().Length is < 8 or > 64)
            {
                return Results.BadRequest();
            }
            var response = await idempotency.ExecuteAsync(
                $"admin-room-control:{roomId}:{key}",
                async () =>
                {
                    var room = await store.GetRoomByIdAsync(roomId, cancellationToken);
                    if (room is null)
                    {
                        return JsonResponse(
                            StatusCodes.Status404NotFound,
                            new { code = "ROOM_NOT_FOUND", roomId });
                    }
                    var forceDissolve = request.ActionType ==
                        nameof(AdminManagementRoomAction.ForceDissolveRoom);
                    if (forceDissolve
                        && room.Lifecycle is
                            RoomLifecycle.Finished or RoomLifecycle.Aborted)
                    {
                        return JsonResponse(
                            StatusCodes.Status200OK,
                            ToRoomControlResult(
                                room,
                                request.ActionType,
                                true));
                    }
                    if (room.StateSequence != request.ExpectedStateSequence)
                    {
                        return JsonResponse(
                            StatusCodes.Status409Conflict,
                            new
                            {
                                code = "ROOM_STATE_CHANGED",
                                roomId,
                                expectedStateSequence = request.ExpectedStateSequence,
                                actualStateSequence = room.StateSequence
                            });
                    }
                    var now = timeProvider.GetUtcNow();
                    var serverInstanceId =
                        room.Route?.ServerInstanceId
                        ?? room.PendingServerInstanceId
                        ?? room.LastServerInstanceId;
                    var updated = forceDissolve
                        ? RoomStateMachine.Transition(
                            room,
                            RoomLifecycle.Aborted,
                            timeProvider) with
                        {
                            Route = null,
                            PendingServerInstanceId = null,
                            LastServerInstanceId = serverInstanceId,
                            NewPlayersProhibited = true,
                            MaintenanceMode = room.MaintenanceMode,
                            MarkedAbnormal = true
                        }
                        : room with
                        {
                            NewPlayersProhibited =
                                room.NewPlayersProhibited
                                || request.ActionType ==
                                    nameof(AdminManagementRoomAction.ProhibitNewPlayers)
                                || request.ActionType ==
                                    nameof(AdminManagementRoomAction.EnableMaintenanceMode),
                            MaintenanceMode =
                                room.MaintenanceMode
                                || request.ActionType ==
                                    nameof(AdminManagementRoomAction.EnableMaintenanceMode),
                            MarkedAbnormal =
                                room.MarkedAbnormal
                                || request.ActionType ==
                                    nameof(AdminManagementRoomAction.MarkRoomAbnormal),
                            StateSequence = room.StateSequence + 1,
                            UpdatedAtUtc = now
                        };
                    if (!await store.UpdateRoomAsync(updated, cancellationToken))
                    {
                        return JsonResponse(
                            StatusCodes.Status409Conflict,
                            new { code = "ROOM_STATE_CHANGED", roomId });
                    }
                    try
                    {
                        await monitoring.AppendEventAsync(
                            roomId,
                            new RoomTimelineEvent(
                                Guid.NewGuid().ToString(),
                                $"admin.{request.ActionType}",
                                now,
                                updated.StateSequence,
                                request.TraceId!,
                                new Dictionary<string, object?>
                                {
                                    ["reason"] = request.Reason,
                                    ["newPlayersProhibited"] =
                                        updated.NewPlayersProhibited,
                                    ["maintenanceMode"] =
                                        updated.MaintenanceMode,
                                    ["markedAbnormal"] = updated.MarkedAbnormal
                                }),
                            cancellationToken);
                        await eventHub.PublishAsync(
                            forceDissolve
                                ? LobbyEventTypes.RoomClosed
                                : LobbyEventTypes.RoomUpdated,
                            ToRoomControlResult(
                                updated,
                                request.ActionType,
                                false),
                            cancellationToken);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException)
                    {
                        // 房间权威写入已成功，事件发布失败只能记录并由后续投影修复，不能回滚状态版本。
                        loggerFactory.CreateLogger("AdminRoomControl")
                            .LogError(
                                exception,
                                "Room control event publication failed RoomId={RoomId}",
                                roomId);
                    }
                    if (forceDissolve)
                    {
                        await lobbyService.ReleaseClosedRoomServerAsync(
                            request.TraceId!,
                            roomId,
                            cancellationToken);
                    }
                    return JsonResponse(
                        StatusCodes.Status200OK,
                        ToRoomControlResult(
                            updated,
                            request.ActionType,
                            false));
                },
                cancellationToken);
            return Results.Json(response.Body, statusCode: response.StatusCode);
        });
    }
}
