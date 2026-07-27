using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

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
    CreatePlayerSupportTicket
}

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

public enum ApprovalDecision { Approve, Reject }

public sealed record CreateAdminActionRequest(
    AdminManagementActionType ActionType,
    string TargetId,
    string Reason,
    string TicketId,
    long? ExpectedStateSequence,
    JsonElement? Parameters = null);

public sealed record ConfirmAdminActionRequest(string TargetConfirmation);
public sealed record ApproveAdminActionRequest(ApprovalDecision Decision, string Comment);

public sealed record AdminActionApproval(
    string ApprovalId,
    string ApprovedBy,
    DateTimeOffset ApprovedAtUtc,
    ApprovalDecision Decision,
    string Comment);

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
    JsonElement? Parameters = null);

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

public sealed record AdminCommandExecutionResult(
    bool Succeeded,
    bool Retryable,
    JsonElement AfterState,
    string? Error);
