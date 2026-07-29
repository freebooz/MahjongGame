namespace GuiyangMahjong.Admin.Security;

public static class AdminRoles
{
    public const string RoomViewer = "room.viewer";
    public const string PlayerViewer = "player.viewer";
    public const string PlayerOperator = "player.operator";
    public const string PlayerApprover = "player.approver";
    public const string SanctionOperator = "sanction.operator";
    public const string RiskAnalyst = "risk.analyst";
    public const string SupportOperator = "support.operator";
    public const string RoomOperator = "room.operator";
    public const string RoomApprover = "room.approver";
    public const string InfrastructureOperator = "infrastructure.operator";
    public const string CompensationOperator = "compensation.operator";
    public const string ChatCompliance = "chat.compliance";
    public const string AuditViewer = "audit.viewer";
    public const string SeniorGovernanceApprover = "governance.senior-approver";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(
        [
            RoomViewer,
            PlayerViewer,
            PlayerOperator,
            PlayerApprover,
            SanctionOperator,
            RiskAnalyst,
            SupportOperator,
            RoomOperator,
            RoomApprover,
            InfrastructureOperator,
            CompensationOperator,
            ChatCompliance,
            AuditViewer,
            SeniorGovernanceApprover
        ],
        StringComparer.Ordinal);
}

/// <summary>
/// 已认证的管理人员上下文；角色定义能力，地域、班次、案件和紧急授权属性用于进一步收窄权限。
/// </summary>
public sealed record AdminPrincipal(
    string OperatorId,
    IReadOnlySet<string> Roles,
    IReadOnlySet<string>? AllowedRegions = null,
    IReadOnlySet<string>? AssignedCaseIds = null,
    string? ShiftId = null,
    bool MfaSatisfied = false,
    DateTimeOffset? BreakGlassUntilUtc = null)
{
    public bool HasRole(string role) => Roles.Contains(role);
    public IReadOnlySet<string> Regions =>
        AllowedRegions ?? EmptyAttributes;
    public IReadOnlySet<string> CaseIds =>
        AssignedCaseIds ?? EmptyAttributes;

    private static readonly IReadOnlySet<string> EmptyAttributes =
        new HashSet<string>(StringComparer.Ordinal);
}

public static class AdminPrincipalContext
{
    private const string ItemKey = "GuiyangMahjong.AdminPrincipal";

    public static void Set(HttpContext context, AdminPrincipal principal) =>
        context.Items[ItemKey] = principal;

    public static AdminPrincipal Get(HttpContext context) =>
        context.Items[ItemKey] as AdminPrincipal
        ?? throw new InvalidOperationException("Administrator identity is unavailable.");
}
