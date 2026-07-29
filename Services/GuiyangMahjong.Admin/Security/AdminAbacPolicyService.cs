using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Security;

/// <summary>
/// 管理端 ABAC 决策点；在既有 RBAC 之后校验地域、班次、案件归属、金额阈值与紧急访问窗口。
/// 该服务不信任查询参数或请求正文中的权限属性，全部授权范围必须来自已认证主体。
/// </summary>
public sealed class AdminAbacPolicyService(
    IOptions<AdminOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminAbacPolicyService> logger)
{
    private readonly AdminAbacOptions policy = options.Value.Abac;
    /// <summary>指示属性策略是否启用；仅用于兼容非生产本地环境，生产启动验证强制为 true。</summary>
    public bool Enabled => policy.Enabled;

    /// <summary>
    /// 校验地域查询范围；启用 ABAC 后禁止无地域查询，防止同一角色默认枚举所有集群。
    /// </summary>
    public void RequireRegion(HttpContext context, string? regionId)
    {
        if (!policy.Enabled) return;
        var principal = RequireActiveShift(context);
        var normalized = regionId?.Trim() ?? string.Empty;
        var allowed = normalized.Length > 0
            && (principal.Regions.Contains("*")
                || principal.Regions.Contains(normalized));
        RecordDecision(context, principal, "region", allowed, normalized);
        if (!allowed)
        {
            throw AdminOperationException.Forbidden(
                "A permitted regionId is required for this operation.");
        }
    }

    /// <summary>
    /// 校验敏感案件访问；常规路径要求人员与案件直接关联且在身份属性中获分派，审计角色也不能绕过归属。
    /// 符合强 MFA、短时授权和明确原因时允许 Break-glass，并产生独立告警指标。
    /// </summary>
    public void RequireCase(
        HttpContext context,
        AdminCaseRecord investigation)
    {
        if (!policy.Enabled)
        {
            RequireLegacyCaseLink(
                AdminPrincipalContext.Get(context),
                investigation);
            return;
        }

        var principal = RequireActiveShift(context);
        var linked = principal.OperatorId == investigation.RequestedBy
            || principal.OperatorId == investigation.ApprovedBy;
        var assigned = principal.CaseIds.Contains(investigation.CaseId)
            || principal.CaseIds.Contains(investigation.TicketId);
        if (linked && assigned)
        {
            RecordDecision(
                context,
                principal,
                "case_ownership",
                true,
                investigation.CaseId);
            return;
        }

        if (TryAuthorizeBreakGlass(context, principal, investigation.CaseId))
            return;

        RecordDecision(
            context,
            principal,
            "case_ownership",
            false,
            investigation.CaseId);
        throw AdminOperationException.Forbidden(
            "The current operator is not assigned to this investigation case.");
    }

    /// <summary>对高额玩家补偿强制增加高级治理审批，拒绝单纯依赖普通玩家审批角色。</summary>
    public void RequireCompensationApproval(
        HttpContext context,
        AdminActionRecord action)
    {
        if (!policy.Enabled
            || action.ActionType
                != AdminManagementActionType.GrantPlayerCompensation
            || !action.Parameters.HasValue
            || !action.Parameters.Value.TryGetProperty(
                "amount",
                out var amountElement)
            || !amountElement.TryGetInt64(out var amount)
            || amount < policy.HighValueCompensationThreshold)
        {
            return;
        }

        var principal = RequireActiveShift(context);
        var allowed = principal.HasRole(
            AdminRoles.SeniorGovernanceApprover);
        RecordDecision(
            context,
            principal,
            "high_value_compensation",
            allowed,
            amount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        if (!allowed)
        {
            throw AdminOperationException.Forbidden(
                "High-value compensation requires a senior governance approver.");
        }
    }

    private AdminPrincipal RequireActiveShift(HttpContext context)
    {
        var principal = AdminPrincipalContext.Get(context);
        var allowed = !string.IsNullOrWhiteSpace(principal.ShiftId);
        RecordDecision(
            context,
            principal,
            "active_shift",
            allowed,
            principal.ShiftId ?? string.Empty);
        if (!allowed)
        {
            throw AdminOperationException.Forbidden(
                "An active administrator shift assignment is required.");
        }
        return principal;
    }

    private bool TryAuthorizeBreakGlass(
        HttpContext context,
        AdminPrincipal principal,
        string caseId)
    {
        var now = timeProvider.GetUtcNow();
        var until = principal.BreakGlassUntilUtc;
        var reason = context.Request.Headers["X-Break-Glass-Reason"]
            .ToString()
            .Trim();
        var allowed = principal.MfaSatisfied
            && until > now
            && until <= now.AddMinutes(policy.BreakGlassMaximumMinutes)
            && reason.Length is >= 10 and <= 500;
        if (!allowed) return false;

        MahjongTelemetry.RecordAdminBreakGlass("allowed");
        MahjongTelemetry.RecordAdminAbacDecision(
            "break_glass",
            "allowed");
        logger.LogCritical(
            "AdminBreakGlassAllowed OperatorId={OperatorId} CaseId={CaseId} UntilUtc={UntilUtc} Reason={Reason} TraceId={TraceId}",
            principal.OperatorId,
            caseId,
            until,
            reason,
            context.TraceIdentifier);
        return true;
    }

    private void RecordDecision(
        HttpContext context,
        AdminPrincipal principal,
        string policyName,
        bool allowed,
        string resource)
    {
        var outcome = allowed ? "allowed" : "denied";
        MahjongTelemetry.RecordAdminAbacDecision(policyName, outcome);
        logger.LogInformation(
            "AdminAbacDecision Policy={Policy} Outcome={Outcome} OperatorId={OperatorId} Resource={Resource} TraceId={TraceId}",
            policyName,
            outcome,
            principal.OperatorId,
            resource,
            context.TraceIdentifier);
    }

    private static void RequireLegacyCaseLink(
        AdminPrincipal principal,
        AdminCaseRecord investigation)
    {
        if (principal.OperatorId != investigation.RequestedBy
            && principal.OperatorId != investigation.ApprovedBy
            && !principal.HasRole(AdminRoles.AuditViewer))
        {
            throw AdminOperationException.Forbidden(
                "The current operator is not linked to this investigation case.");
        }
    }
}
