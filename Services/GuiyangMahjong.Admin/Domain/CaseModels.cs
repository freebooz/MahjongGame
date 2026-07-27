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
    string Status);

public sealed record AdminCaseCreateResult(
    AdminCaseRecord Case,
    bool Duplicate);
