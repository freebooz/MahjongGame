using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;

namespace GuiyangMahjong.Admin.Api;

public static partial class AdminEndpoints
{
    /// <summary>注册配置治理 BFF 路由；服务端 RBAC、MFA 与异人审批不能由前端按钮可见性替代。</summary>
    private static void MapConfigurationEndpoints(RouteGroupBuilder api)
    {
        var group = api.MapGroup("/configurations");
        group.MapGet("/drafts", (HttpContext context, ConfigurationManagementClient client, CancellationToken token) =>
        {
            RequireRole(AdminPrincipalContext.Get(context), AdminRoles.GovernancePublisher, AdminRoles.GovernanceApprover);
            return client.GetAsync("/internal/admin/configurations/drafts", token);
        });
        group.MapGet("/{configKey}/versions", (string configKey, HttpContext context, ConfigurationManagementClient client, CancellationToken token) =>
        {
            RequireRole(AdminPrincipalContext.Get(context), AdminRoles.GovernancePublisher, AdminRoles.GovernanceApprover);
            return client.GetAsync($"/internal/admin/configurations/{Uri.EscapeDataString(configKey)}/versions", token);
        });
        group.MapPost("/drafts", (HttpContext context, JsonElement request, ConfigurationManagementClient client, CancellationToken token) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            RequireRole(principal, AdminRoles.GovernancePublisher);
            return client.PostAsync("/internal/admin/configurations/drafts", request, principal.OperatorId, GetIdempotencyKey(context), token);
        });
        group.MapPost("/drafts/{draftId}/validate", (string draftId, HttpContext context, ConfigurationManagementClient client, CancellationToken token) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            RequireRole(principal, AdminRoles.GovernancePublisher);
            return client.PostAsync($"/internal/admin/configurations/drafts/{Uri.EscapeDataString(draftId)}/validate", null,
                principal.OperatorId, GetIdempotencyKey(context), token);
        });
        group.MapPost("/drafts/{draftId}/publish", async (string draftId, HttpContext context, JsonElement request, ConfigurationManagementClient client, CancellationToken token) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            RequireRole(principal, AdminRoles.GovernanceApprover);
            var draft = await client.GetAsync($"/internal/admin/configurations/drafts/{Uri.EscapeDataString(draftId)}", token);
            var creator = draft.GetProperty("createdBy").GetString() ?? throw new AdminOperationException("CONFIG_DRAFT_CREATOR_MISSING", "草稿缺少创建人审计字段。", 409);
            if (string.Equals(creator, principal.OperatorId, StringComparison.Ordinal)) throw new AdminOperationException("CONFIG_TWO_PERSON_APPROVAL_REQUIRED", "创建人与审批人必须不同。", 403);
            var command = new
            {
                operatorId = creator,
                approverId = principal.OperatorId,
                approvalId = Required(request, "approvalId"),
                reasonCode = Required(request, "reasonCode"),
                ticketId = Required(request, "ticketId"),
                traceId = context.TraceIdentifier,
                idempotencyKey = GetIdempotencyKey(context)
            };
            return await client.PostAsync($"/internal/admin/configurations/drafts/{Uri.EscapeDataString(draftId)}/publish",
                command, principal.OperatorId, GetIdempotencyKey(context), token);
        });
    }

    private static string Required(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new AdminOperationException($"CONFIG_{property.ToUpperInvariant()}_REQUIRED", $"缺少配置发布字段 {property}。", 400);

    private static void RequireRole(AdminPrincipal principal, params string[] roles)
    {
        if (!roles.Any(principal.HasRole)) throw new AdminOperationException("ADMIN_FORBIDDEN", "当前管理员没有配置治理权限。", 403);
    }
}
