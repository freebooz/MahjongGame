using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 玩家聊天合规查询分区，强制绑定独立审批授权、工单、时间窗口、字段范围和操作者水印。
/// </summary>
public static partial class PlayerEvidenceEndpoints
{
    /// <summary>注册聊天权限预检和受控聊天记录查询端点。</summary>
    private static void MapChatComplianceEndpoints(RouteGroupBuilder adminApi)
    {
        adminApi.MapGet("/chat-permission", async (
            string playerId,
            string ticketId,
            HttpContext context,
            IPlayerEvidenceStore evidenceStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(
                context,
                AdminRoles.ChatCompliance,
                AdminRoles.AuditViewer);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(ticketId, "ticketId");
            var now = timeProvider.GetUtcNow();
            var principal = AdminPrincipalContext.Get(context);
            var grant = await evidenceStore.GetActiveChatGrantAsync(
                playerId,
                ticketId,
                principal.OperatorId,
                now,
                cancellationToken);
            var result = grant is null
                ? new PlayerChatPermissionResult(
                    false,
                    playerId,
                    ticketId,
                    [],
                    null,
                    null,
                    null,
                    "No active separately approved chat access grant exists.")
                : new PlayerChatPermissionResult(
                    true,
                    playerId,
                    ticketId,
                    grant.Scopes,
                    grant.WindowStartsAtUtc,
                    grant.WindowEndsAtUtc,
                    grant.ExpiresAtUtc,
                    "Access is limited to the approved time window and scopes.");
            await auditStore.AppendAuditAsync(
                new AdminAuditDraft(
                    now,
                    principal.OperatorId,
                    "PlayerChatPermissionChecked",
                    "Player",
                    playerId,
                    result.Reason,
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        result.Allowed,
                        result.TicketId,
                        result.Scopes,
                        result.WindowStartsAtUtc,
                        result.WindowEndsAtUtc,
                        result.ExpiresAtUtc
                    }),
                    grant is null
                        ? null
                        : JsonSerializer.SerializeToElement(new
                        {
                            grant.GrantId,
                            grant.ApprovedBy,
                            grant.CreatedAtUtc
                        }),
                    GetTraceId(context),
                    ticketId),
                cancellationToken);
            return Results.Ok(result);
        });

        adminApi.MapGet("/chat-records", async (
            string playerId,
            string ticketId,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            string? scopes,
            HttpContext context,
            IPlayerEvidenceStore evidenceStore,
            IChatArchiveQueryClient chatArchive,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(
                context,
                AdminRoles.ChatCompliance,
                AdminRoles.AuditViewer);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(ticketId, "ticketId");
            var now = timeProvider.GetUtcNow();
            var principal = AdminPrincipalContext.Get(context);
            var grant = await evidenceStore.GetActiveChatGrantAsync(
                playerId,
                ticketId,
                principal.OperatorId,
                now,
                cancellationToken);
            if (grant is null)
            {
                throw AdminOperationException.Forbidden(
                    "An active separately approved chat grant is required.");
            }
            var requestedFrom = fromUtc ?? grant.WindowStartsAtUtc;
            var requestedTo = toUtc ?? grant.WindowEndsAtUtc;
            if (requestedFrom < grant.WindowStartsAtUtc
                || requestedTo > grant.WindowEndsAtUtc
                || requestedFrom >= requestedTo)
            {
                throw AdminOperationException.Forbidden(
                    "The requested chat range exceeds the approved window.");
            }
            var requestedScopes = string.IsNullOrWhiteSpace(scopes)
                ? grant.Scopes
                : scopes.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            if (requestedScopes.Length == 0
                || requestedScopes.Any(scope =>
                    !grant.Scopes.Contains(scope, StringComparer.Ordinal)))
            {
                throw AdminOperationException.Forbidden(
                    "The requested chat scope exceeds the approved grant.");
            }
            var records = await chatArchive.QueryAsync(
                playerId,
                requestedFrom,
                requestedTo,
                requestedScopes,
                cancellationToken);
            var traceId = GetTraceId(context);
            await auditStore.AppendAuditAsync(
                new AdminAuditDraft(
                    now,
                    principal.OperatorId,
                    "PlayerChatContentViewed",
                    "Player",
                    playerId,
                    grant.Reason,
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        count = records.Length,
                        requestedFrom,
                        requestedTo,
                        requestedScopes,
                        grant.GrantId,
                        watermark = $"{principal.OperatorId}:{ticketId}:{traceId}"
                    }),
                    JsonSerializer.SerializeToElement(new
                    {
                        grant.ApprovedBy,
                        grant.ExpiresAtUtc
                    }),
                    traceId,
                    ticketId),
                cancellationToken);
            return Results.Ok(new
            {
                records,
                watermark = new
                {
                    operatorId = principal.OperatorId,
                    ticketId,
                    traceId,
                    viewedAtUtc = now
                },
                range = new { requestedFrom, requestedTo },
                scopes = requestedScopes
            });
        });
    }
}
