using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Storage;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 玩家通用证据查询分区，负责按证据类型授权并记录不可绕过的读取审计。
/// </summary>
public static partial class PlayerEvidenceEndpoints
{
    /// <summary>注册举报、资产、奖励和支付投影查询端点。</summary>
    private static void MapEvidenceQueryEndpoints(RouteGroupBuilder adminApi)
    {
        MapEvidenceQuery(
            adminApi,
            "/reports",
            PlayerEvidenceType.Report,
            [
                AdminRoles.RiskAnalyst,
                AdminRoles.SanctionOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer
            ]);
        MapEvidenceQuery(
            adminApi,
            "/asset-changes",
            PlayerEvidenceType.AssetChange,
            [
                AdminRoles.CompensationOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer
            ]);
        MapEvidenceQuery(
            adminApi,
            "/reward-claims",
            PlayerEvidenceType.RewardClaim,
            [
                AdminRoles.CompensationOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer
            ]);
        MapEvidenceQuery(
            adminApi,
            "/payment-orders",
            PlayerEvidenceType.PaymentOrder,
            [
                AdminRoles.CompensationOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer
            ]);
    }

    /// <summary>
    /// 注册单类敏感证据查询，并统一执行角色、标识、数量上限和读取审计。
    /// </summary>
    private static void MapEvidenceQuery(
        RouteGroupBuilder group,
        string pattern,
        PlayerEvidenceType evidenceType,
        string[] roles)
    {
        group.MapGet(pattern, async (
            string playerId,
            string ticketId,
            HttpContext context,
            IPlayerEvidenceStore evidenceStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(context, roles);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(ticketId, "ticketId");
            var records = await evidenceStore.ListAsync(
                playerId,
                evidenceType,
                Math.Clamp(limit ?? 100, 1, 200),
                cancellationToken);
            await AppendReadAuditAsync(
                auditStore,
                context,
                timeProvider.GetUtcNow(),
                playerId,
                "SensitivePlayerEvidenceViewed",
                $"Authorized {evidenceType} projection read.",
                ticketId,
                evidenceType,
                records,
                cancellationToken);
            return Results.Ok(records);
        });
    }

    /// <summary>
    /// 追加敏感证据读取审计；只记录来源引用和数量，不复制完整受限数据。
    /// </summary>
    private static async Task AppendReadAuditAsync(
        IAdminActionStore auditStore,
        HttpContext context,
        DateTimeOffset now,
        string playerId,
        string operation,
        string reason,
        string ticketId,
        PlayerEvidenceType evidenceType,
        IReadOnlyCollection<PlayerEvidenceRecord> records,
        CancellationToken cancellationToken)
    {
        var principal = AdminPrincipalContext.Get(context);
        await auditStore.AppendAuditAsync(
            new AdminAuditDraft(
                now,
                principal.OperatorId,
                operation,
                "Player",
                playerId,
                reason,
                null,
                JsonSerializer.SerializeToElement(new
                {
                    evidenceType,
                    count = records.Count,
                    sourceReferences = records
                        .Select(item => item.SourceReference)
                        .Take(200)
                        .ToArray()
                }),
                null,
                GetTraceId(context),
                ticketId),
            cancellationToken);
    }
}
