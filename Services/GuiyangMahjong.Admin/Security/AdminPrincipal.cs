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
            AuditViewer
        ],
        StringComparer.Ordinal);
}

public sealed record AdminPrincipal(string OperatorId, IReadOnlySet<string> Roles)
{
    public bool HasRole(string role) => Roles.Contains(role);
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
