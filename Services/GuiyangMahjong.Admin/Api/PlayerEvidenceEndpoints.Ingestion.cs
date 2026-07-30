using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 玩家证据端点的内部投影接入分区，仅接受受信服务凭据和严格幂等事件。
/// </summary>
public static partial class PlayerEvidenceEndpoints
{
    /// <summary>
    /// 注册证据事件和聊天访问授权投影入口。
    /// 身份失败返回明确 HTTP 结果，数据冲突统一转换为 Admin 领域冲突。
    /// </summary>
    private static void MapProjectionIngestionEndpoints(WebApplication app)
    {
        var internalApi = app.MapGroup("/internal/projections");
        internalApi.MapPost("/player-evidence", async (
            HttpContext context,
            IngestPlayerEvidenceRequest request,
            IOptions<AdminOptions> options,
            IPlayerEvidenceStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var authenticationError =
                AuthenticateIngestion(context, options.Value);
            if (authenticationError is not null) return authenticationError;
            ValidateIdempotencyKey(context, request.EventId);
            ValidateEvidence(request, timeProvider.GetUtcNow());
            try
            {
                var result = await store.IngestAsync(
                    request,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return result.Duplicate
                    ? Results.Ok(result)
                    : Results.Json(
                        result,
                        statusCode: StatusCodes.Status201Created);
            }
            catch (InvalidOperationException exception)
            {
                throw AdminOperationException.Conflict(exception.Message);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(24 * 1024));

        internalApi.MapPost("/player-chat-access-grants", async (
            HttpContext context,
            IngestPlayerChatAccessGrantRequest request,
            IOptions<AdminOptions> options,
            IPlayerEvidenceStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var authenticationError =
                AuthenticateIngestion(context, options.Value);
            if (authenticationError is not null) return authenticationError;
            ValidateIdempotencyKey(context, request.GrantId);
            ValidateChatGrant(request, options.Value, timeProvider.GetUtcNow());
            try
            {
                var result = await store.IngestChatGrantAsync(
                    request,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return result.Duplicate
                    ? Results.Ok(result)
                    : Results.Json(
                        result,
                        statusCode: StatusCodes.Status201Created);
            }
            catch (InvalidOperationException exception)
            {
                throw AdminOperationException.Conflict(exception.Message);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024));
    }
}
