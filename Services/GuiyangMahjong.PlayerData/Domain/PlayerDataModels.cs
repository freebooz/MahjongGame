using System.Text.Json;

namespace GuiyangMahjong.PlayerData.Domain;

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
    Restricted,
    Financial
}

public sealed record RecordEvidenceRequest(
    string EventId,
    string PlayerId,
    PlayerEvidenceType EvidenceType,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    JsonElement Data,
    PlayerEvidenceSensitivity Sensitivity);

public sealed record RewardClaimRequest(
    string EventId,
    string RewardGrantId,
    string PlayerId,
    string AssetCode,
    long Amount,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    string TraceId);

public sealed record AdminWalletOperationRequest(
    string OperationType,
    string PlayerId,
    string CaseId,
    string? AssetCode,
    long? Amount,
    string? RewardGrantId,
    string RequestedBy,
    string ApprovedBy,
    string Reason,
    string TicketId,
    string TraceId,
    DateTimeOffset ApprovedAtUtc);

public sealed record WalletBalance(
    string PlayerId,
    string AssetCode,
    long Balance,
    long Version,
    DateTimeOffset UpdatedAtUtc);

public sealed record WalletOperationResult(
    string CommandId,
    string TransactionId,
    string OperationType,
    string PlayerId,
    string AssetCode,
    long Amount,
    long BalanceAfter,
    long BalanceVersion,
    string Status,
    bool Duplicate,
    DateTimeOffset CompletedAtUtc);

public sealed record EvidenceRecordResult(
    string EventId,
    bool Duplicate);

public sealed record ProjectionOutboxRecord(
    string EventId,
    JsonElement Payload,
    string Status,
    int AttemptCount,
    DateTimeOffset AvailableAtUtc,
    string? LockOwner,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? LastError);

public sealed record ChatPolicyResult(
    string PlayerId,
    bool Allowed,
    DateTimeOffset? MutedUntilUtc,
    string Reason);

public sealed record AuthorizeChatMessageRequest(
    string MessageId,
    string PlayerId,
    string RoomId,
    DateTimeOffset RequestedAtUtc);
