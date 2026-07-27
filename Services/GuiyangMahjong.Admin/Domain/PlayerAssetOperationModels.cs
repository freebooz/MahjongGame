using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

public enum PlayerAssetOperationType
{
    GrantCompensation,
    RevokeReward
}

public sealed record PlayerAssetOperationRecord(
    string OperationId,
    string SourceCommandId,
    string ActionRequestId,
    string CaseId,
    PlayerAssetOperationType OperationType,
    string PlayerId,
    string? AssetCode,
    long? Amount,
    string? RewardGrantId,
    string RequestedBy,
    string ApprovedBy,
    DateTimeOffset CreatedAtUtc,
    string Reason,
    string TicketId,
    string TraceId,
    JsonElement BeforeState,
    string Status);

public sealed record PlayerAssetOperationCreateResult(
    PlayerAssetOperationRecord Operation,
    bool Duplicate);
