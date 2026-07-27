using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

public enum PlayerEvidenceType
{
    Report,
    AssetChange,
    RewardClaim,
    PaymentOrder,
    Replay
}

public enum PlayerEvidenceSensitivity
{
    Operational,
    Restricted,
    Financial
}

public sealed record PlayerEvidenceRecord(
    string EventId,
    string PlayerId,
    PlayerEvidenceType EvidenceType,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    JsonElement Data,
    PlayerEvidenceSensitivity Sensitivity,
    DateTimeOffset IngestedAtUtc);

public sealed record PlayerEvidenceIngestResult(
    PlayerEvidenceRecord Evidence,
    bool Duplicate);

public sealed record IngestPlayerEvidenceRequest(
    string EventId,
    string PlayerId,
    PlayerEvidenceType EvidenceType,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    JsonElement Data,
    PlayerEvidenceSensitivity Sensitivity);

public sealed record PlayerChatAccessGrant(
    string GrantId,
    string PlayerId,
    string TicketId,
    string GrantedTo,
    string ApprovedBy,
    string Reason,
    string TraceId,
    DateTimeOffset WindowStartsAtUtc,
    DateTimeOffset WindowEndsAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string[] Scopes,
    DateTimeOffset CreatedAtUtc);

public sealed record PlayerChatAccessGrantIngestResult(
    PlayerChatAccessGrant Grant,
    bool Duplicate);

public sealed record IngestPlayerChatAccessGrantRequest(
    string GrantId,
    string PlayerId,
    string TicketId,
    string GrantedTo,
    string ApprovedBy,
    string Reason,
    string TraceId,
    DateTimeOffset WindowStartsAtUtc,
    DateTimeOffset WindowEndsAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string[] Scopes);

public sealed record PlayerChatPermissionResult(
    bool Allowed,
    string PlayerId,
    string TicketId,
    string[] Scopes,
    DateTimeOffset? WindowStartsAtUtc,
    DateTimeOffset? WindowEndsAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string Reason);
