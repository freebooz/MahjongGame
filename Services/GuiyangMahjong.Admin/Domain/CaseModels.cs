using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

public enum AdminCaseType
{
    DisputeInvestigation,
    PlayerSupport,
    CompensationReview,
    ReplayReview,
    RoomLogExport
}

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

public sealed record AdminCaseCreateResult(
    AdminCaseRecord Case,
    bool Duplicate);

/// <summary>
/// 关闭案件请求；必须引用已生成证据包的 SHA-256，确保结论与调查材料不可分离。
/// </summary>
public sealed record CloseAdminCaseRequest(
    string Resolution,
    string EvidencePackageHash);
