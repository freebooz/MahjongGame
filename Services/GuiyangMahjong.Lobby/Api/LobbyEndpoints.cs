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

        app.MapGet("/internal/monitoring/rooms", async (
            HttpContext context,
            ILobbyStore store,
            IOptions<LobbyOptions> options,
            string? cursor,
            int? pageSize,
            int? limit,
            string? lifecycle,
            string? gameMode,
            string? search,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32 || !HasInternalCredential(context, monitoringToken))
            {
                return Results.Unauthorized();
            }

            var filterFingerprint = CreateMonitoringFilterFingerprint(
                lifecycle,
                gameMode,
                search);
            if (!TryReadMonitoringCursor(
                    cursor,
                    filterFingerprint,
                    out var afterCreatedAtUtc,
                    out var afterRoomId))
            {
                return Results.BadRequest(new
                {
                    code = "INVALID_CURSOR",
                    message = "Room cursor is invalid."
                });
            }
            var safePageSize = Math.Clamp(pageSize ?? limit ?? 100, 1, 200);
            if (limit.HasValue) context.Response.Headers["Deprecation"] = "true";
            var loaded = await store.ListRoomsForMonitoringAsync(
                safePageSize + 1,
                afterCreatedAtUtc,
                afterRoomId,
                lifecycle,
                gameMode,
                search,
                cancellationToken);
            var items = loaded.Take(safePageSize).ToArray();
            var nextCursor = loaded.Count > safePageSize && items.Length > 0
                ? WriteMonitoringCursor(
                    items[^1].CreatedAtUtc,
                    items[^1].RoomId,
                    filterFingerprint)
                : null;
            // 滚动升级窗口为旧 Admin 保留数组形状，同时强制缩小巨页；新客户端使用 pageSize/cursor。
            if (limit.HasValue
                && pageSize is null
                && string.IsNullOrWhiteSpace(cursor))
                return Results.Ok(items);
            return Results.Ok(new
            {
                items,
                nextCursor,
                hasMore = nextCursor is not null,
                pageSize = safePageSize
            });
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
        app.MapGet("/internal/monitoring/players/{playerId}/room-history", async (
            string playerId,
            HttpContext context,
            IPlayerHistoryStore historyStore,
            IOptions<LobbyOptions> options,
            int? pageSize,
            DateTimeOffset? beforeAtUtc,
            string? beforeRoomId,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalCredential(
                    context,
                    options.Value.MonitoringReadOnlyToken))
            {
                return Results.Unauthorized();
            }
            if (playerId.Length is < 1 or > 80)
                return Results.BadRequest();
            return Results.Ok(await historyStore.ListRoomsAsync(
                playerId,
                Math.Clamp(pageSize ?? 100, 1, 200),
                beforeAtUtc,
                beforeRoomId,
                cancellationToken));
        });
        app.MapGet("/internal/monitoring/players/{playerId}/connection-history", async (
            string playerId,
            HttpContext context,
            IPlayerHistoryStore historyStore,
            IOptions<LobbyOptions> options,
            int? pageSize,
            DateTimeOffset? beforeAtUtc,
            string? beforeEventId,
            CancellationToken cancellationToken) =>
        {
            if (!HasInternalCredential(
                    context,
                    options.Value.MonitoringReadOnlyToken))
            {
                return Results.Unauthorized();
            }
            if (playerId.Length is < 1 or > 80
                || (beforeEventId is not null
                    && !Guid.TryParse(beforeEventId, out _)))
            {
                return Results.BadRequest();
            }
            return Results.Ok(await historyStore.ListConnectionsAsync(
                playerId,
                Math.Clamp(pageSize ?? 100, 1, 200),
                beforeAtUtc,
                beforeEventId,
                cancellationToken));
        });
        app.MapGet("/internal/monitoring/player-presence", async (
            HttpContext context,
            IOnlinePresenceService presence,
            ILobbyStore store,
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
            var snapshots = await presence.GetPlayersAsync(ids, cancellationToken);
            var activeRooms = await store.GetActiveRoomsByPlayersAsync(
                ids,
                cancellationToken);
            // 将活动房间上下文并入同一个批量响应，使 Admin 无需为一页玩家扫描全部房间。
            return Results.Ok(snapshots.Select(snapshot =>
            {
                var room = activeRooms.GetValueOrDefault(snapshot.PlayerId);
                return snapshot with
                {
                    RoomId = room?.RoomId,
                    RoomCode = room?.RoomCode,
                    ServerInstanceId = room?.Route?.ServerInstanceId
                        ?? room?.PendingServerInstanceId
                        ?? room?.LastServerInstanceId
                };
            }).ToArray());
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
            room.MaintenanceMode,
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
        EnableMaintenanceMode,
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

    /// <summary>解析 Lobby 内部键集游标；损坏游标直接拒绝，避免退化为不可控全表扫描。</summary>
    private static bool TryReadMonitoringCursor(
        string? cursor,
        string expectedFilterFingerprint,
        out DateTimeOffset? createdAtUtc,
        out string? roomId)
    {
        createdAtUtc = null;
        roomId = null;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            var payload = JsonSerializer.Deserialize<LobbyMonitoringCursor>(
                Convert.FromBase64String(cursor));
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.RoomId)
                || !string.Equals(
                    payload.FilterFingerprint,
                    expectedFilterFingerprint,
                    StringComparison.Ordinal))
                return false;
            createdAtUtc = payload.CreatedAtUtc;
            roomId = payload.RoomId;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>生成不含玩家信息的房间分页游标，供 Admin 聚合器断点读取下一页。</summary>
    private static string WriteMonitoringCursor(
        DateTimeOffset createdAtUtc,
        string roomId,
        string filterFingerprint) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new LobbyMonitoringCursor(
                createdAtUtc,
                roomId,
                filterFingerprint)));

    /// <summary>将标准化筛选条件绑定到游标，防止跨筛选复用导致错页或越权读取。</summary>
    private static string CreateMonitoringFilterFingerprint(
        string? lifecycle,
        string? gameMode,
        string? search) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(
                '\n',
                lifecycle?.Trim().ToUpperInvariant() ?? string.Empty,
                gameMode?.Trim().ToUpperInvariant() ?? string.Empty,
                search?.Trim().ToUpperInvariant() ?? string.Empty))));

    /// <summary>Lobby 房间键集游标，创建时间和 RoomId 共同保证确定性顺序。</summary>
    private sealed record LobbyMonitoringCursor(
        DateTimeOffset CreatedAtUtc,
        string RoomId,
        string FilterFingerprint);
}
