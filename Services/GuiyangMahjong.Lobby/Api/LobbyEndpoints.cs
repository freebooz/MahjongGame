using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace GuiyangMahjong.Lobby.Api;

public static class LobbyEndpoints
{
    public static void MapLobbyEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (
            ILobbyStore store,
            IAllocatorClient allocator,
            CancellationToken cancellationToken) =>
        {
            var persistenceReady = await store.CheckHealthAsync(cancellationToken);
            var allocatorReady = await allocator.CheckReadinessAsync(cancellationToken);
            return persistenceReady && allocatorReady
                ? Results.Ok(new { status = "ready", persistence = "ready", allocator = "ready" })
                : Results.Json(
                    new
                    {
                        status = "not-ready",
                        persistence = persistenceReady ? "ready" : "unavailable",
                        allocator = allocatorReady ? "ready" : "unavailable"
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

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
                            MarkedAbnormal = true
                        }
                        : room with
                        {
                            NewPlayersProhibited =
                                room.NewPlayersProhibited
                                || request.ActionType ==
                                    nameof(AdminManagementRoomAction.ProhibitNewPlayers),
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

        app.MapGet("/internal/monitoring/rooms", async (
            HttpContext context,
            ILobbyStore store,
            IOptions<LobbyOptions> options,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32 || !HasInternalCredential(context, monitoringToken))
            {
                return Results.Unauthorized();
            }

            var safeLimit = Math.Clamp(limit ?? 1000, 1, 5000);
            return Results.Ok(await store.ListRoomsForMonitoringAsync(safeLimit, cancellationToken));
        });
        app.MapGet("/internal/monitoring/rooms/{roomId}/runtime", async (
            string roomId,
            HttpContext context,
            IRoomMonitoringStore monitoring,
            IOptions<LobbyOptions> options,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32 || !HasInternalCredential(context, monitoringToken))
            {
                return Results.Unauthorized();
            }
            var runtime = await monitoring.GetRuntimeAsync(roomId, cancellationToken);
            return runtime is null ? Results.NotFound() : Results.Ok(runtime);
        });
        app.MapGet("/internal/monitoring/rooms/{roomId}/events", async (
            string roomId,
            HttpContext context,
            IRoomMonitoringStore monitoring,
            IOptions<LobbyOptions> options,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32 || !HasInternalCredential(context, monitoringToken))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await monitoring.ListEventsAsync(
                roomId, Math.Clamp(limit ?? 200, 1, 500), cancellationToken));
        });
        app.MapGet("/internal/monitoring/player-presence", async (
            HttpContext context,
            IOnlinePresenceService presence,
            IOptions<LobbyOptions> options,
            string? playerIds,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32 || !HasInternalCredential(context, monitoringToken))
            {
                return Results.Unauthorized();
            }
            var ids = (playerIds ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(500)
                .ToArray();
            if (ids.Any(playerId => playerId.Length is < 1 or > 80))
            {
                return Results.BadRequest();
            }
            return Results.Ok(await presence.GetPlayersAsync(ids, cancellationToken));
        });

        app.MapGet("/openapi/v1.yaml", async (HttpContext context) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "OpenAPI", "lobby-v1.openapi.yaml");
            context.Response.ContentType = "application/yaml; charset=utf-8";
            await context.Response.SendFileAsync(path, context.RequestAborted);
        });

        var v1 = app.MapGroup("/v1");

        v1.MapGet("/lobby/bootstrap", async (
            HttpContext context,
            IOptions<LobbyOptions> options,
            IOnlinePresenceService presence,
            CancellationToken cancellationToken) =>
        {
            var player = PlayerAuthenticationMiddleware.GetPlayer(context);
            var onlineCount = await presence.GetOnlineCountAsync(cancellationToken);
            return Results.Ok(new LobbyBootstrapResponse(
                RequestIdMiddleware.GetRequestId(context),
                player.PlayerId,
                player.DisplayName,
                (int)Math.Min(onlineCount, int.MaxValue),
                options.Value.Announcements,
                options.Value.ProtocolVersion));
        });

        v1.MapGet("/rooms", async (
            CancellationToken cancellationToken,
            LobbyService lobbyService) => Results.Ok(await lobbyService.ListRoomsAsync(cancellationToken)));

        v1.MapPost("/rooms", async (
            HttpContext context,
            CreateRoomRequest request,
            LobbyService lobbyService,
            IIdempotencyStore idempotency,
            CancellationToken cancellationToken) =>
        {
            var key = RequireIdempotencyKey(context);
            var player = PlayerAuthenticationMiddleware.GetPlayer(context);
            var result = await idempotency.ExecuteAsync(
                $"create:{player.PlayerId}:{key}",
                async () => new IdempotentHttpResponse(
                    StatusCodes.Status202Accepted,
                    System.Text.Json.JsonSerializer.SerializeToElement(
                        await lobbyService.CreateRoomAsync(
                            RequestIdMiddleware.GetRequestId(context), player, request, cancellationToken),
                        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))),
                cancellationToken);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });

        v1.MapPost("/rooms/current/close", async (
            HttpContext context,
            LobbyService lobbyService,
            IIdempotencyStore idempotency,
            CancellationToken cancellationToken) =>
        {
            var key = RequireIdempotencyKey(context);
            var player = PlayerAuthenticationMiddleware.GetPlayer(context);
            var result = await idempotency.ExecuteAsync(
                $"close-owned:{player.PlayerId}:{key}",
                async () =>
                {
                    var requestId = RequestIdMiddleware.GetRequestId(context);
                    var closed = await lobbyService.CloseOwnedRoomAsync(requestId, player, cancellationToken);
                    context.Response.OnCompleted(() => lobbyService.ReleaseClosedRoomServerAsync(
                        requestId, closed.RoomId, CancellationToken.None));
                    return new IdempotentHttpResponse(
                        StatusCodes.Status200OK,
                        System.Text.Json.JsonSerializer.SerializeToElement(
                            closed,
                            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                },
                cancellationToken);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });

        v1.MapPost("/rooms/{roomCode}/join", async (
            string roomCode,
            HttpContext context,
            JoinRoomRequest request,
            LobbyService lobbyService,
            IIdempotencyStore idempotency,
            CancellationToken cancellationToken) =>
        {
            var key = RequireIdempotencyKey(context);
            var player = PlayerAuthenticationMiddleware.GetPlayer(context);
            var result = await idempotency.ExecuteAsync(
                $"join:{player.PlayerId}:{roomCode}:{key}",
                async () =>
                {
                    var value = await lobbyService.JoinRoomAsync(
                        RequestIdMiddleware.GetRequestId(context), player, roomCode, request, cancellationToken);
                    return new IdempotentHttpResponse(
                        value is GameServerRoute ? StatusCodes.Status200OK : StatusCodes.Status202Accepted,
                        System.Text.Json.JsonSerializer.SerializeToElement(
                            value,
                            value.GetType(),
                            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                },
                cancellationToken);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });

        v1.MapGet("/rooms/{roomCode}/route", async (
            string roomCode,
            HttpContext context,
            LobbyService lobbyService,
            CancellationToken cancellationToken) => Results.Ok(await lobbyService.GetRouteAsync(
                RequestIdMiddleware.GetRequestId(context),
                PlayerAuthenticationMiddleware.GetPlayer(context),
                roomCode,
                cancellationToken)));

        v1.MapPost("/reconnect/route", async (
            HttpContext context,
            ReconnectRouteRequest request,
            LobbyService lobbyService,
            CancellationToken cancellationToken) =>
        {
            RequireIdempotencyKey(context);
            return Results.Ok(await lobbyService.GetReconnectRouteAsync(
                RequestIdMiddleware.GetRequestId(context),
                PlayerAuthenticationMiddleware.GetPlayer(context),
                request,
                cancellationToken));
        });

        v1.MapGet("/events", async (
            HttpContext context,
            LobbyEventHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                throw new LobbyOperationException(
                    LobbyErrorCode.InvalidRequest,
                    "该端点需要 WebSocket 连接",
                    StatusCodes.Status400BadRequest);
            }
            var socket = await context.WebSockets.AcceptWebSocketAsync();
            await hub.HandleClientAsync(
                PlayerAuthenticationMiddleware.GetPlayer(context), socket, context.RequestAborted);
        });
    }

    private static string RequireIdempotencyKey(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (key.Length is < 16 or > 128)
        {
            throw new LobbyOperationException(
                LobbyErrorCode.InvalidRequest,
                "Idempotency-Key 长度必须为 16 到 128",
                StatusCodes.Status400BadRequest);
        }
        return key;
    }

    private static IdempotentHttpResponse JsonResponse(int statusCode, object body) =>
        new(
            statusCode,
            System.Text.Json.JsonSerializer.SerializeToElement(
                body,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)));

    private static AdminUpdateRoomControlResult ToRoomControlResult(
        LobbyRoom room,
        string actionType,
        bool alreadyTerminal) =>
        new(
            room.RoomId,
            actionType,
            room.StateSequence,
            room.NewPlayersProhibited,
            room.MarkedAbnormal,
            room.Lifecycle,
            room.Route?.ServerInstanceId
                ?? room.PendingServerInstanceId
                ?? room.LastServerInstanceId,
            alreadyTerminal);

    private enum AdminManagementRoomAction
    {
        MarkRoomAbnormal,
        ProhibitNewPlayers,
        ForceDissolveRoom
    }

    private static bool HasInternalCredential(HttpContext context, string expectedToken)
    {
        if (expectedToken.Length < 32) return false;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        var valid = supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
        CryptographicOperations.ZeroMemory(supplied);
        return valid;
    }

    private static string GetBearerCredential(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return authorization[7..].Trim();
    }
}
