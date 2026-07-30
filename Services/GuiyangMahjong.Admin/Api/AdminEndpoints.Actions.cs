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
/// 承载需要确认、审批和幂等键保护的管理动作请求接口。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 注册管理动作工作流端点；动作执行仍由 AdminActionWorkflow 维持双人复核和事务边界。
    /// </summary>
    private static void MapActionEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/action-requests", async (
            HttpContext context,
            AdminActionWorkflow workflow,
            int? limit,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ListAsync(
                AdminPrincipalContext.Get(context),
                Math.Clamp(limit ?? 100, 1, 500),
                cancellationToken)));
        api.MapPost("/action-requests", async (
            HttpContext context,
            CreateAdminActionRequest request,
            AdminActionWorkflow workflow,
            CancellationToken cancellationToken) =>
            Results.Accepted(value: await workflow.CreateAsync(
                AdminPrincipalContext.Get(context),
                request,
                GetTraceId(context),
                GetIdempotencyKey(context),
                cancellationToken)));
        api.MapPost("/action-requests/{actionRequestId}/confirm", async (
            string actionRequestId,
            HttpContext context,
            ConfirmAdminActionRequest request,
            AdminActionWorkflow workflow,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ConfirmAsync(
                AdminPrincipalContext.Get(context),
                actionRequestId,
                request,
                cancellationToken)));
        api.MapPost("/action-requests/{actionRequestId}/approvals", async (
            string actionRequestId,
            HttpContext context,
            ApproveAdminActionRequest request,
            AdminActionWorkflow workflow,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ApproveAsync(
                AdminPrincipalContext.Get(context),
                actionRequestId,
                request,
                context,
                cancellationToken)));
    }
}

