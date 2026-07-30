using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Storage;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 玩家 GM 操作历史分区，只返回当前操作者按角色、审批职责或本人发起范围可见的记录。
/// </summary>
public static partial class PlayerEvidenceEndpoints
{
    /// <summary>注册玩家 GM 操作历史查询端点，并为查询本身追加审计。</summary>
    private static void MapGmOperationEndpoints(RouteGroupBuilder adminApi)
    {
        adminApi.MapGet("/gm-operations", async (
            string playerId,
            string ticketId,
            HttpContext context,
            IAdminActionStore actionStore,
            TimeProvider timeProvider,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(
                context,
                AdminRoles.PlayerOperator,
                AdminRoles.SanctionOperator,
                AdminRoles.RiskAnalyst,
                AdminRoles.SupportOperator,
                AdminRoles.CompensationOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(ticketId, "ticketId");
            var principal = AdminPrincipalContext.Get(context);
            var records = (await actionStore.ListAsync(
                    500,
                    cancellationToken))
                .Where(item =>
                    item.TargetType == "Player"
                    && item.TargetId == playerId
                    && (principal.HasRole(AdminRoles.AuditViewer)
                        || principal.HasRole(AdminRoles.PlayerApprover)
                        || item.RequestedBy == principal.OperatorId
                        || principal.HasRole(
                            RequiredOperationRole(item.ActionType))))
                .OrderByDescending(item => item.RequestedAtUtc)
                .Take(Math.Clamp(limit ?? 100, 1, 200))
                .ToArray();
            await actionStore.AppendAuditAsync(
                new AdminAuditDraft(
                    timeProvider.GetUtcNow(),
                    principal.OperatorId,
                    "PlayerGmOperationsViewed",
                    "Player",
                    playerId,
                    "Authorized player GM operation history read.",
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        count = records.Length,
                        actionRequestIds = records
                            .Select(item => item.ActionRequestId)
                            .ToArray()
                    }),
                    null,
                    GetTraceId(context),
                    ticketId),
                cancellationToken);
            return Results.Ok(records);
        });
    }
}
