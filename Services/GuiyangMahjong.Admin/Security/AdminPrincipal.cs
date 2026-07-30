namespace GuiyangMahjong.Admin.Security;

/// <summary>
/// Admin RBAC 稳定角色目录。
/// 角色只授予能力上限，地域、班次、案件和金额仍由 ABAC 收窄；
/// 新角色必须同步配置校验、审批职责分离和前端展示。
/// </summary>
public static class AdminRoles
{
    // 常量属于企业身份 Claim 与授权策略契约，禁止随意重命名或复用旧语义。
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

    /// <summary>配置和身份令牌允许声明的全部角色白名单；未知角色在启动或认证时拒绝。</summary>
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
    /// <summary>以大小写敏感稳定角色值检查能力；不替代 ABAC 和审批职责分离。</summary>
    public bool HasRole(string role) => Roles.Contains(role);

    /// <summary>允许地域集合；未提供时返回共享只读空集合，不解释为全部地域。</summary>
    public IReadOnlySet<string> Regions =>
        AllowedRegions ?? EmptyAttributes;

    /// <summary>已分派案件集合；未提供时返回空，敏感证据读取应关闭式拒绝。</summary>
    public IReadOnlySet<string> CaseIds =>
        AssignedCaseIds ?? EmptyAttributes;

    private static readonly IReadOnlySet<string> EmptyAttributes =
        new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// 在单个 HttpContext 中保存和取得已认证 Admin 主体。
/// 主体只能由认证中间件设置，后续端点不得从请求正文构造或覆盖。
/// </summary>
public static class AdminPrincipalContext
{
    private const string ItemKey = "GuiyangMahjong.AdminPrincipal";

    /// <summary>将已验证主体附加到当前请求生命周期；不跨请求或后台任务持久化。</summary>
    public static void Set(HttpContext context, AdminPrincipal principal) =>
        context.Items[ItemKey] = principal;

    /// <summary>取得当前已验证主体；认证管道缺失时抛出异常而不是创建匿名管理员。</summary>
    public static AdminPrincipal Get(HttpContext context) =>
        context.Items[ItemKey] as AdminPrincipal
        ?? throw new InvalidOperationException("Administrator identity is unavailable.");
}
