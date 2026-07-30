using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 承载玩家资产操作、审计记录和命令发件箱查询，集中保留高权限只读入口。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 注册审计类查询端点；每项查询继续由工作流或端点本身执行角色校验。
    /// </summary>
    private static void MapAuditEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/player-asset-operations", async (
            HttpContext context,
            IPlayerAssetOperationStore assetOperationStore,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            if (!principal.HasRole(AdminRoles.CompensationOperator)
                && !principal.HasRole(AdminRoles.PlayerApprover)
                && !principal.HasRole(AdminRoles.AuditViewer))
            {
                throw AdminOperationException.Forbidden(
                    "The current role cannot view player asset operations.");
            }
            return Results.Ok(await assetOperationStore.ListAsync(
                Math.Clamp(limit ?? 200, 1, 500),
                cancellationToken));
        });
        api.MapGet("/audit", async (
            HttpContext context,
            AdminActionWorkflow workflow,
            int? limit,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ListAuditAsync(
                AdminPrincipalContext.Get(context),
                Math.Clamp(limit ?? 200, 1, 1000),
                cancellationToken)));
        api.MapGet("/command-outbox", async (
            HttpContext context,
            AdminActionWorkflow workflow,
            int? limit,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ListOutboxAsync(
                AdminPrincipalContext.Get(context),
                Math.Clamp(limit ?? 100, 1, 500),
                cancellationToken)));
    }
}

