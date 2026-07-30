using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

/// <summary>Admin 可创建的调查案件类型；类型决定所需角色、证据范围和后续闭环。</summary>
public enum AdminCaseType
{
    DisputeInvestigation,
    PlayerSupport,
    CompensationReview,
    ReplayReview,
    RoomLogExport
}

/// <summary>
/// 管理案件的不可变业务记录。
/// SourceCommandId/ActionRequestId 关联审批执行，BeforeState 固化立案前证据；
/// 结案字段只能从 Open 单向填写，EvidencePackageHash 绑定最终调查材料。
/// </summary>
public sealed record AdminCaseRecord(
    string CaseId,
    string SourceCommandId,
    string ActionRequestId,
    AdminCaseType CaseType,
    string TargetType,
    string TargetId,
    string RequestedBy,
    string ApprovedBy,
    DateTimeOffset CreatedAtUtc,
    string Reason,
    string TicketId,
    string TraceId,
    JsonElement BeforeState,
    string Status,
    DateTimeOffset? ClosedAtUtc = null,
    string? ClosedBy = null,
    string? Resolution = null,
    string? EvidencePackageHash = null);

/// <summary>案件创建结果；Duplicate 表示来源命令已创建同一案件，调用方应复用返回记录。</summary>
public sealed record AdminCaseCreateResult(
    AdminCaseRecord Case,
    bool Duplicate);

/// <summary>
/// 关闭案件请求；必须引用已生成证据包的 SHA-256，确保结论与调查材料不可分离。
/// </summary>
public sealed record CloseAdminCaseRequest(
    string Resolution,
    string EvidencePackageHash);
