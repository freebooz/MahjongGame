// Admin 管理领域模型：定义房间、玩家和服务器高风险操作、审批及审计载荷。
// 这些 DTO 属于持久化和 API 共享契约；新增字段必须明确脱敏、复制与向后兼容语义。
using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

/// <summary>
/// 管理后台允许编排的高风险动作类型。
/// 房间、服务器和玩家动作共享同一审批流水，但实际执行器必须按目标类型再次校验权限。
/// </summary>
public enum AdminManagementActionType
{
    ForceDissolveRoom,
    TerminateAbnormalServer,
    MarkRoomAbnormal,
    TriggerCompensation,
    ProhibitNewPlayers,
    EnableMaintenanceMode,
    ExportRoomLogs,
    ViewReplay,
    StartDisputeInvestigation,
    ForceLogoutPlayer,
    TemporaryFreezePlayer,
    PermanentBanPlayer,
    LiftPlayerBan,
    MutePlayer,
    UnmutePlayer,
    ResetAbnormalPlayerSession,
    MarkRiskAccount,
    GrantPlayerCompensation,
    RevokeErroneousReward,
    ViewPlayerReplay,
    CreatePlayerSupportTicket,
    OrderRefund,
    RulePublish,
    ConfigurationPublish,
    BatchSanction
}

/// <summary>
/// 管理动作从二次确认、审批到执行完成的持久化状态。
/// 状态只能由工作流按乐观版本迁移，不能由 API 调用方直接指定。
/// </summary>
public enum AdminActionStatus
{
    AwaitingConfirmation,
    PendingApproval,
    ApprovedAwaitingExecution,
    Rejected,
    Expired,
    Cancelled,
    Executing,
    Succeeded,
    Failed
}

/// <summary>独立审批人的决定；拒绝为终态，批准仍需进入受控执行阶段。</summary>
public enum ApprovalDecision { Approve, Reject }

/// <summary>
/// 创建管理动作的外部请求。
/// <paramref name="ExpectedStateSequence"/> 用于拒绝基于陈旧房间快照的操作，
/// <paramref name="Parameters"/> 只能携带动作白名单允许的附加参数，不能包含凭据或最终对局结果。
/// </summary>
public sealed record CreateAdminActionRequest(
    AdminManagementActionType ActionType,
    string TargetId,
    string Reason,
    string TicketId,
    long? ExpectedStateSequence,
    JsonElement? Parameters = null,
    string? ReasonCode = null,
    string? OperationDescription = null);

/// <summary>二次确认请求；确认文本必须与服务端生成的目标确认值一致。</summary>
public sealed record ConfirmAdminActionRequest(string TargetConfirmation);

/// <summary>审批请求；审批人与申请人必须角色分离，意见会进入不可变审计链。</summary>
public sealed record ApproveAdminActionRequest(ApprovalDecision Decision, string Comment);

/// <summary>
/// 已持久化的审批证据，记录审批身份、UTC 时间、决定和意见。
/// 该记录属于审计事实，动作执行后也不得覆盖。
/// </summary>
public sealed record AdminActionApproval(
    string ApprovalId,
    string ApprovedBy,
    DateTimeOffset ApprovedAtUtc,
    ApprovalDecision Decision,
    string Comment);

/// <summary>
/// 管理动作聚合根的持久化快照。
/// 状态哈希与版本用于并发保护，前置状态和审批记录用于事后还原，
/// 确认/过期时间均为 UTC，参数保持创建时的 JSON 值语义。
/// </summary>
public sealed record AdminActionRecord(
    string ActionRequestId,
    AdminManagementActionType ActionType,
    string TargetType,
    string TargetId,
    string RequestedBy,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ConfirmationExpiresAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    string Reason,
    string TicketId,
    string TraceId,
    long? ExpectedStateSequence,
    string ExpectedStateHash,
    JsonElement BeforeState,
    AdminActionStatus Status,
    AdminActionApproval? Approval,
    int Version,
    JsonElement? Parameters = null,
    string ReasonCode = "LEGACY_UNSPECIFIED",
    string OperationDescription = "",
    string? Confirmation = null,
    string? IdempotencyKey = null);

/// <summary>
/// 防篡改审计链中的一条不可变记录。
/// <paramref name="Sequence"/> 是全局持久化顺序，前后哈希连接相邻记录；
/// 操作前后状态必须是经过脱敏的管理投影，不得写入访问令牌和聊天正文。
/// </summary>
public sealed record AdminAuditRecord(
    string AuditId,
    long Sequence,
    DateTimeOffset OccurredAtUtc,
    string OperatorId,
    string Operation,
    string TargetType,
    string TargetId,
    string Reason,
    JsonElement? BeforeState,
    JsonElement? AfterState,
    JsonElement? ApprovalRecord,
    string TraceId,
    string TicketId,
    string? PreviousHash,
    string RecordHash);

/// <summary>
/// 待追加的审计草稿；存储层负责分配序号、审计标识并计算哈希链。
/// TraceId 与工单号是跨服务调查的强制关联键。
/// </summary>
public sealed record AdminAuditDraft(
    DateTimeOffset OccurredAtUtc,
    string OperatorId,
    string Operation,
    string TargetType,
    string TargetId,
    string Reason,
    JsonElement? BeforeState,
    JsonElement? AfterState,
    JsonElement? ApprovalRecord,
    string TraceId,
    string TicketId);

/// <summary>
/// 事务外箱中的管理命令。
/// 锁和租约字段支持多副本安全领取，尝试次数、下次可用时间及最后错误用于有界重试；
/// 载荷必须是执行器白名单生成的内部命令，不能直接复用外部请求 JSON。
/// </summary>
public sealed record AdminCommandOutboxRecord(
    string OutboxId,
    string ActionRequestId,
    AdminManagementActionType ActionType,
    string TargetType,
    string TargetId,
    JsonElement Payload,
    string TraceId,
    string Status,
    int AttemptCount,
    DateTimeOffset AvailableAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LockedAtUtc,
    string? LockOwner,
    DateTimeOffset? LeaseExpiresAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? LastError);

/// <summary>
/// 下游命令执行结果。
/// <paramref name="Retryable"/> 仅描述瞬态失败；失败结果仍需提供脱敏后的状态证据，
/// 由工作流决定重试、终止或转入人工调查。
/// </summary>
public sealed record AdminCommandExecutionResult(
    bool Succeeded,
    bool Retryable,
    JsonElement AfterState,
    string? Error);
