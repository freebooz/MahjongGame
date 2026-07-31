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

        // 管理命令拥有独立身份、幂等和审计边界，拆分映射可防止 DS 生命周期路由再次膨胀。
        MapInternalAdministrationEndpoints(app);
    }
}
