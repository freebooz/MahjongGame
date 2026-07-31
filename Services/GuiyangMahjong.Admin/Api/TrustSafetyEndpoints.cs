using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Storage;
using GuiyangMahjong.Admin.TrustSafety;

namespace GuiyangMahjong.Admin.Api;

/// <summary>TrustSafety 规范只读 API；管理命令仍必须进入既有二次确认和审批工作流。</summary>
public static class TrustSafetyEndpoints
{
    /// <summary>注册房间和玩家监控规范视图；端点不提供修改处罚或结算的能力。</summary>
    public static void MapTrustSafetyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/operations/v1/trust-safety");
        group.MapGet("/rooms/{roomId}", async (
            string roomId,
            HttpContext context,
            TrustSafetyReadModelService service,
            AdminAbacPolicyService abacPolicy,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            var result = await service.GetRoomAsync(roomId, cancellationToken);
            if (result is not null)
                abacPolicy.RequireRegion(context, result.Detail.Summary.RegionId);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
        group.MapGet("/players/{playerId}", async (
            string playerId,
            string? caseId,
            HttpContext context,
            TrustSafetyReadModelService service,
            IAdminCaseStore investigations,
            AdminAbacPolicyService abacPolicy,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.PlayerViewer);
            if (!string.IsNullOrWhiteSpace(caseId))
            {
                var investigation = await investigations.GetAsync(caseId.Trim(), cancellationToken);
                if (investigation is null
                    || investigation.TargetType != "Player"
                    || investigation.TargetId != playerId)
                {
                    return Results.NotFound();
                }
                // 工单标识只有在服务端确认案件归属和 ABAC 授权后才可进入监控响应。
                abacPolicy.RequireCase(context, investigation);
            }
            var result = await service.GetPlayerAsync(playerId, caseId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });
    }

    /// <summary>服务端强制执行 RBAC；前端隐藏按钮不能替代此校验。</summary>
    private static void RequireRole(HttpContext context, string role)
    {
        if (!AdminPrincipalContext.Get(context).HasRole(role))
            throw new Services.AdminOperationException("ADMIN_FORBIDDEN", $"缺少角色：{role}", StatusCodes.Status403Forbidden);
    }
}
