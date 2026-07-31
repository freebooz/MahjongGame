using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GuiyangMahjong.Lobby.Api;

/// <summary>
/// 注册 Dedicated Server、结算回传和 Admin 房间控制使用的内部写接口。
/// 所有入口必须验证内部凭据或一次性命令令牌，并保持幂等执行语义。
/// </summary>
public static partial class LobbyEndpoints
{
    /// <summary>注册游戏服生命周期、结算和管理控制路由。</summary>
    private static void MapInternalEndpoints(WebApplication app)
    {
        var internalApi = app.MapGroup("/internal/gameservers");
        internalApi.MapPost("/register", async (
            HttpContext context,
            GameServerRegistration request,
            LobbyService lobbyService,
            CancellationToken cancellationToken) => Results.Ok(await lobbyService.RegisterGameServerAsync(
                RequestIdMiddleware.GetRequestId(context), request, cancellationToken)));
        internalApi.MapPost("/{serverInstanceId}/heartbeat", async (
            string serverInstanceId,
            HttpContext context,
            GameServerHeartbeat request,
            LobbyService lobbyService,
            CancellationToken cancellationToken) =>
        {
            await lobbyService.RecordGameServerHeartbeatAsync(
                RequestIdMiddleware.GetRequestId(context), serverInstanceId, request, cancellationToken);
            return Results.NoContent();
        });
        internalApi.MapPost("/failure", async (
            HttpContext context,
            GameServerFailure request,
            LobbyService lobbyService,
            IOptions<LobbyOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalCredential(context, options.Value.InternalServiceToken))
            {
                return Results.Unauthorized();
            }
            await lobbyService.MarkGameServerFailedAsync(request, cancellationToken);
            return Results.NoContent();
        });
        internalApi.MapPost("/reallocate", async (
            HttpContext context,
            GameServerReallocationRequest request,
            LobbyService lobbyService,
            IIdempotencyStore idempotency,
            IOptions<LobbyOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalCredential(
                    context,
                    options.Value.InternalServiceToken))
            {
                return Results.Unauthorized();
            }
            var key = RequireIdempotencyKey(context);
            if (request.RoomId.Length is < 1 or > 80
                || request.Reason.Trim().Length is < 5 or > 500)
            {
                return Results.BadRequest();
            }
            var result = await idempotency.ExecuteAsync(
                $"gameserver-reallocate:{request.RoomId}:{key}",
                async () => new IdempotentHttpResponse(
                    StatusCodes.Status202Accepted,
                    JsonSerializer.SerializeToElement(
                        await lobbyService.ReallocateGameServerAsync(
                            $"reallocate:{request.RoomId}:{key}",
                            request.RoomId,
                            request.Reason.Trim(),
                            cancellationToken),
                        new JsonSerializerOptions(
                            JsonSerializerDefaults.Web))),
                cancellationToken);
            return Results.Json(
                result.Body,
                statusCode: result.StatusCode);
        });

        app.MapPost("/internal/matches/{matchId}/result", async (
            string matchId,
            HttpContext context,
            MatchResultReport report,
            LobbyService lobbyService,
            CancellationToken cancellationToken) =>
        {
            RequireIdempotencyKey(context);
            return Results.Ok(await lobbyService.SubmitMatchResultAsync(
                    RequestIdMiddleware.GetRequestId(context),
                    matchId,
                    GetBearerCredential(context),
                    report,
                    cancellationToken));
        });

        app.MapPost("/internal/matches/{matchId}/result/recovery", async (
            string matchId,
            HttpContext context,
            MatchResultReport report,
            LobbyService lobbyService,
            IOptions<LobbyOptions> options,
            CancellationToken cancellationToken) =>
        {
            RequireIdempotencyKey(context);
            if (!HasInternalCredential(context, options.Value.InternalServiceToken))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await lobbyService.RecoverMatchResultAsync(
                RequestIdMiddleware.GetRequestId(context), matchId, report, cancellationToken));
        });

        app.MapPost("/internal/settlement-authority/validate", async (
            HttpContext context,
            SettlementAuthorityRequest request,
            LobbyService lobbyService,
            IOptions<LobbyOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalCredential(context, options.Value.Settlement.AuthorityToken))
                return Results.Unauthorized();
            return Results.Ok(await lobbyService.ValidateSettlementAuthorityAsync(request, cancellationToken));
        });

        app.MapPost("/internal/settlement-authority/committed", async (
            HttpContext context,
            ExternalSettlementCommittedRequest request,
            LobbyService lobbyService,
            IOptions<LobbyOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalCredential(context, options.Value.Settlement.AuthorityToken))
                return Results.Unauthorized();
            return Results.Ok(await lobbyService.MarkExternalSettlementCommittedAsync(
                RequestIdMiddleware.GetRequestId(context), request, cancellationToken));
        });

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
                            RoomLifecycle.Closed or RoomLifecycle.Failed)
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
                            RoomLifecycle.Failed,
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
