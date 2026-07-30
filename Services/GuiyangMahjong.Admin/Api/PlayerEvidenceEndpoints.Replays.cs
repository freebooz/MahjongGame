using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 玩家回放调查分区，要求独立审批的开放案件，并通过短期签名访问真实回放内容。
/// </summary>
public static partial class PlayerEvidenceEndpoints
{
    /// <summary>
    /// 注册回放目录、签名访问和内容下载端点。
    /// 下载前会再次核验案件状态、操作者绑定、签名和内容哈希。
    /// </summary>
    private static void MapReplayEndpoints(RouteGroupBuilder adminApi)
    {
        adminApi.MapGet("/replays", async (
            string playerId,
            string caseId,
            HttpContext context,
            IAdminCaseStore caseStore,
            IPlayerEvidenceStore evidenceStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(
                context,
                AdminRoles.SupportOperator,
                AdminRoles.RiskAnalyst,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(caseId, "caseId");
            var review = await caseStore.GetAsync(caseId, cancellationToken);
            if (review is null
                || review.CaseType != AdminCaseType.ReplayReview
                || review.TargetType != "Player"
                || review.TargetId != playerId
                || review.Status != "Open")
            {
                throw AdminOperationException.Forbidden(
                    "An open, separately approved replay review case is required.");
            }
            var records = await evidenceStore.ListAsync(
                playerId,
                PlayerEvidenceType.Replay,
                Math.Clamp(limit ?? 100, 1, 200),
                cancellationToken);
            await AppendReadAuditAsync(
                auditStore,
                context,
                timeProvider.GetUtcNow(),
                playerId,
                "PlayerReplayMetadataViewed",
                review.Reason,
                review.TicketId,
                PlayerEvidenceType.Replay,
                records,
                cancellationToken);
            return Results.Ok(records);
        });

        adminApi.MapGet("/replays/{eventId}/access", async (
            string playerId,
            string eventId,
            string caseId,
            HttpContext context,
            IAdminCaseStore caseStore,
            IPlayerEvidenceStore evidenceStore,
            IReplayArchiveClient replayArchive,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(
                context,
                AdminRoles.SupportOperator,
                AdminRoles.RiskAnalyst,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(caseId, "caseId");
            if (!Guid.TryParse(eventId, out _))
                throw AdminOperationException.Invalid("eventId must be a UUID.");
            var review = await caseStore.GetAsync(caseId, cancellationToken);
            if (review is null
                || review.CaseType != AdminCaseType.ReplayReview
                || review.TargetType != "Player"
                || review.TargetId != playerId
                || review.Status != "Open")
            {
                throw AdminOperationException.Forbidden(
                    "An open replay review case for this player is required.");
            }
            var evidence = (await evidenceStore.ListAsync(
                    playerId,
                    PlayerEvidenceType.Replay,
                    200,
                    cancellationToken))
                .SingleOrDefault(item => item.EventId == eventId)
                ?? throw AdminOperationException.NotFound(
                    "Replay catalog entry was not found.");
            if (!evidence.Data.TryGetProperty("objectKey", out _))
                throw AdminOperationException.Conflict(
                    "Replay catalog entry has no object reference.");
            var principal = AdminPrincipalContext.Get(context);
            return Results.Ok(replayArchive.CreateAccess(
                caseId,
                playerId,
                eventId,
                principal.OperatorId,
                timeProvider.GetUtcNow()));
        });

        adminApi.MapGet("/replay-content/{eventId}", async (
            string playerId,
            string eventId,
            string caseId,
            long expires,
            string signature,
            HttpContext context,
            IAdminCaseStore caseStore,
            IPlayerEvidenceStore evidenceStore,
            IReplayArchiveClient replayArchive,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(caseId, "caseId");
            var principal = AdminPrincipalContext.Get(context);
            var now = timeProvider.GetUtcNow();
            if (!replayArchive.ValidateAccess(
                    caseId,
                    playerId,
                    eventId,
                    principal.OperatorId,
                    expires,
                    signature,
                    now))
            {
                throw AdminOperationException.Forbidden(
                    "Replay access URL is invalid, expired, or belongs to another operator.");
            }
            var review = await caseStore.GetAsync(caseId, cancellationToken);
            if (review is null
                || review.CaseType != AdminCaseType.ReplayReview
                || review.TargetId != playerId
                || review.Status != "Open")
            {
                throw AdminOperationException.Forbidden(
                    "The replay review case is no longer active.");
            }
            var evidence = (await evidenceStore.ListAsync(
                    playerId,
                    PlayerEvidenceType.Replay,
                    200,
                    cancellationToken))
                .SingleOrDefault(item => item.EventId == eventId)
                ?? throw AdminOperationException.NotFound(
                    "Replay catalog entry was not found.");
            var objectKey = evidence.Data.GetProperty("objectKey").GetString()
                ?? throw AdminOperationException.Conflict(
                    "Replay object reference is invalid.");
            var expectedHash =
                evidence.Data.TryGetProperty("contentHash", out var hashElement)
                    ? hashElement.GetString()
                    : null;
            var content = await replayArchive.DownloadAsync(
                objectKey,
                expectedHash,
                cancellationToken);
            await auditStore.AppendAuditAsync(
                new AdminAuditDraft(
                    now,
                    principal.OperatorId,
                    "PlayerReplayContentDownloaded",
                    "Player",
                    playerId,
                    review.Reason,
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        eventId,
                        byteLength = content.Length,
                        contentHash = expectedHash
                    }),
                    JsonSerializer.SerializeToElement(new
                    {
                        review.ApprovedBy,
                        review.CaseId
                    }),
                    GetTraceId(context),
                    review.TicketId),
                cancellationToken);
            return Results.File(
                content,
                "application/octet-stream",
                $"replay-{eventId}.bin");
        });
    }
}
