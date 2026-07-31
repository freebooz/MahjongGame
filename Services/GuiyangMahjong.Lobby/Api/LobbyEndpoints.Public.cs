using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Lobby;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Realtime;
using GuiyangMahjong.Lobby.Reconnection;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Api;

/// <summary>
/// 玩家认证后的 Lobby 公开 API 与 OpenAPI 文档分区。
/// 房间写操作必须使用幂等键，WebSocket 事件流必须绑定当前认证玩家。
/// </summary>
public static partial class LobbyEndpoints
{
    /// <summary>注册大厅启动、房间生命周期、重连路由和实时事件端点。</summary>
    private static void MapPublicEndpoints(WebApplication app)
    {
        app.MapGet("/openapi/v1.yaml", async (HttpContext context) =>
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "OpenAPI",
                "lobby-v1.openapi.yaml");
            context.Response.ContentType = "application/yaml; charset=utf-8";
            await context.Response.SendFileAsync(
                path,
                context.RequestAborted);
        });

        var v1 = app.MapGroup("/v1");
        v1.MapGet("/lobby/bootstrap", async (
            HttpContext context,
            IOptions<LobbyOptions> options,
            IOnlinePresenceService presence,
            CancellationToken cancellationToken) =>
        {
            var player = PlayerAuthenticationMiddleware.GetPlayer(context);
            var onlineCount =
                await presence.GetOnlineCountAsync(cancellationToken);
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
            LobbyReadService lobby) =>
            Results.Ok(await lobby.ListRoomsAsync(cancellationToken)));

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
                    JsonSerializer.SerializeToElement(
                        await lobbyService.CreateRoomAsync(
                            RequestIdMiddleware.GetRequestId(context),
                            player,
                            request,
                            cancellationToken),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))),
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
                    var requestId =
                        RequestIdMiddleware.GetRequestId(context);
                    var closed = await lobbyService.CloseOwnedRoomAsync(
                        requestId,
                        player,
                        cancellationToken);
                    context.Response.OnCompleted(() =>
                        lobbyService.ReleaseClosedRoomServerAsync(
                            requestId,
                            closed.RoomId,
                            CancellationToken.None));
                    return new IdempotentHttpResponse(
                        StatusCodes.Status200OK,
                        JsonSerializer.SerializeToElement(
                            closed,
                            new JsonSerializerOptions(
                                JsonSerializerDefaults.Web)));
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
                        RequestIdMiddleware.GetRequestId(context),
                        player,
                        roomCode,
                        request,
                        cancellationToken);
                    return new IdempotentHttpResponse(
                        value is GameServerRoute
                            ? StatusCodes.Status200OK
                            : StatusCodes.Status202Accepted,
                        JsonSerializer.SerializeToElement(
                            value,
                            value.GetType(),
                            new JsonSerializerOptions(
                                JsonSerializerDefaults.Web)));
                },
                cancellationToken);
            return Results.Json(result.Body, statusCode: result.StatusCode);
        });

        v1.MapGet("/rooms/{roomCode}/route", async (
            string roomCode,
            HttpContext context,
            LobbyService lobbyService,
            CancellationToken cancellationToken) =>
            Results.Ok(await lobbyService.GetRouteAsync(
                RequestIdMiddleware.GetRequestId(context),
                PlayerAuthenticationMiddleware.GetPlayer(context),
                roomCode,
                cancellationToken)));

        v1.MapPost("/reconnect/route", async (
            HttpContext context,
            ReconnectRouteRequest request,
            ReconnectionService reconnection,
            CancellationToken cancellationToken) =>
        {
            RequireIdempotencyKey(context);
            return Results.Ok(await reconnection.GetRouteAsync(
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
                PlayerAuthenticationMiddleware.GetPlayer(context),
                socket,
                context.RequestAborted);
        });
    }
}
