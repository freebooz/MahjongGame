using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Options;
using GuiyangMahjong.PlayerData.Services;
using GuiyangMahjong.PlayerData.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.PlayerData.Api;

public static class PlayerDataEndpoints
{
    public static void MapPlayerDataEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () =>
            Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (
            IPlayerDataStore store,
            CancellationToken cancellationToken) =>
            await store.CheckHealthAsync(cancellationToken)
                ? Results.Ok(new { status = "ready" })
                : Results.Json(
                    new { status = "not-ready" },
                    statusCode:
                        StatusCodes.Status503ServiceUnavailable));

        var sources = app.MapGroup("/internal/sources");
        sources.MapPost("/reward-claims", async (
            HttpContext context,
            RewardClaimRequest request,
            IOptions<PlayerDataOptions> options,
            IPlayerDataStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireCredential(
                context,
                options.Value.SourceIngestionToken);
            var idempotencyKey =
                PlayerDataValidation.RequireIdempotencyKey(context);
            var now = timeProvider.GetUtcNow();
            PlayerDataValidation.ValidateReward(request, now);
            if (idempotencyKey !=
                Guid.Parse(request.EventId).ToString())
                throw PlayerDataOperationException.Invalid(
                    "Idempotency-Key must match eventId.");
            var result = await store.RecordRewardClaimAsync(
                request,
                now,
                cancellationToken);
            return result.Duplicate
                ? Results.Ok(result)
                : Results.Json(
                    result,
                    statusCode: StatusCodes.Status201Created);
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024));
        MapEvidenceSource(
            sources,
            "/payment-orders",
            PlayerEvidenceType.PaymentOrder);
        MapEvidenceSource(
            sources,
            "/reports",
            PlayerEvidenceType.Report);
        MapEvidenceSource(
            sources,
            "/replays",
            PlayerEvidenceType.Replay);

        app.MapPost("/internal/admin/wallet-operations", async (
            HttpContext context,
            AdminWalletOperationRequest request,
            IOptions<PlayerDataOptions> options,
            IPlayerDataStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireCredential(
                context,
                options.Value.AdminCommandToken);
            var commandId =
                PlayerDataValidation.RequireIdempotencyKey(context);
            var now = timeProvider.GetUtcNow();
            PlayerDataValidation.ValidateWalletOperation(request, now);
            return Results.Ok(await store.ApplyWalletOperationAsync(
                commandId,
                request,
                now,
                cancellationToken));
        }).WithMetadata(new RequestSizeLimitAttribute(12 * 1024));

        app.MapGet(
            "/internal/monitoring/health",
            async (
                HttpContext context,
                IOptions<PlayerDataOptions> options,
                IPlayerDataStore store,
                CancellationToken cancellationToken) =>
            {
                // Admin 使用只读专用凭据探测依赖，避免把公开存活端点误当成授权监控契约。
                RequireCredential(
                    context,
                    options.Value.MonitoringToken);
                return await store.CheckHealthAsync(cancellationToken)
                    ? Results.Ok(new { status = "ready" })
                    : Results.Json(
                        new { status = "not-ready" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            });

        app.MapGet(
            "/internal/monitoring/players/{playerId}/balances",
            async (
                string playerId,
                HttpContext context,
                IOptions<PlayerDataOptions> options,
                IPlayerDataStore store,
                CancellationToken cancellationToken) =>
            {
                RequireCredential(
                    context,
                    options.Value.MonitoringToken);
                PlayerDataValidation.ValidateIdentifier(
                    playerId,
                    "playerId");
                return Results.Ok(await store.ListBalancesAsync(
                    playerId,
                    cancellationToken));
            });

        app.MapPost("/internal/chat/messages/authorize", async (
            HttpContext context,
            AuthorizeChatMessageRequest request,
            IOptions<PlayerDataOptions> options,
            IChatPolicyClient policyClient,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireCredential(
                context,
                options.Value.ChatGatewayToken);
            PlayerDataValidation.ValidateChatAuthorization(
                request,
                timeProvider.GetUtcNow());
            var policy = await policyClient.GetPolicyAsync(
                request.PlayerId,
                cancellationToken);
            return policy.Allowed
                ? Results.Ok(policy)
                : Results.Json(
                    policy,
                    statusCode: StatusCodes.Status423Locked);
        }).WithMetadata(new RequestSizeLimitAttribute(4 * 1024));
    }

    private static void MapEvidenceSource(
        RouteGroupBuilder sources,
        string pattern,
        PlayerEvidenceType evidenceType)
    {
        sources.MapPost(pattern, async (
            HttpContext context,
            RecordEvidenceRequest request,
            IOptions<PlayerDataOptions> options,
            IPlayerDataStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireCredential(
                context,
                options.Value.SourceIngestionToken);
            var idempotencyKey =
                PlayerDataValidation.RequireIdempotencyKey(context);
            var now = timeProvider.GetUtcNow();
            PlayerDataValidation.ValidateEvidence(
                request,
                evidenceType,
                now);
            if (idempotencyKey !=
                Guid.Parse(request.EventId).ToString())
                throw PlayerDataOperationException.Invalid(
                    "Idempotency-Key must match eventId.");
            var result = await store.RecordEvidenceAsync(
                request,
                now,
                cancellationToken);
            return result.Duplicate
                ? Results.Ok(result)
                : Results.Json(
                    result,
                    statusCode: StatusCodes.Status201Created);
        }).WithMetadata(
            new RequestSizeLimitAttribute(24 * 1024));
    }

    private static void RequireCredential(
        HttpContext context,
        string expectedToken)
    {
        if (!PlayerDataValidation.HasBearer(
                context,
                expectedToken))
            throw new PlayerDataOperationException(
                "PLAYER_DATA_UNAUTHORIZED",
                "A valid dedicated service credential is required.",
                StatusCodes.Status401Unauthorized);
    }
}
