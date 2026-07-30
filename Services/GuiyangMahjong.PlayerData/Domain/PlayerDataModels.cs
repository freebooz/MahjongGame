// PlayerData 领域模型：定义玩家资产、奖励、支付证据、补偿和投影查询契约。
// 金额与数量必须使用明确单位和范围，资产变化只能由受信命令及幂等来源证据驱动。
using System.Text.Json;

namespace GuiyangMahjong.PlayerData.Domain;

/// <summary>PlayerData 接受并投影到 Admin 调查面的证据类型。</summary>
public enum PlayerEvidenceType
{
    Report,
    AssetChange,
    RewardClaim,
    PaymentOrder,
    Replay
}

/// <summary>证据敏感等级；Financial 需要更严格的角色和字段脱敏，二者都不能携带凭据。</summary>
public enum PlayerEvidenceSensitivity
{
    Restricted,
    Financial
}

/// <summary>
/// 记录外部业务证据的内部请求。
/// EventId 与 SourceReference 提供双重幂等，Data 必须是经过白名单投影的 JSON 对象。
/// </summary>
public sealed record RecordEvidenceRequest(
    string EventId,
    string PlayerId,
    PlayerEvidenceType EvidenceType,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    JsonElement Data,
    PlayerEvidenceSensitivity Sensitivity);

/// <summary>
/// 奖励领取命令。
/// Amount 使用 AssetCode 对应的最小整数单位且必须为正；
/// RewardGrantId、EventId 和 SourceReference 共同防止重复发放。
/// </summary>
public sealed record RewardClaimRequest(
    string EventId,
    string RewardGrantId,
    string PlayerId,
    string AssetCode,
    long Amount,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    string TraceId);

/// <summary>
/// 由 Admin 双人审批后提交的钱包操作。
/// 仅支持增量补偿或按 RewardGrantId 撤销原奖励，不能传入最终余额；
/// 可空资产/金额字段的必需性由 OperationType 决定。
/// </summary>
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

/// <summary>单个玩家资产余额；Balance 为最小整数单位，Version 每次原子变更递增。</summary>
public sealed record WalletBalance(
    string PlayerId,
    string AssetCode,
    long Balance,
    long Version,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 钱包命令的持久化回执。
/// Amount 是本次有符号增量，BalanceAfter/BalanceVersion 是同一事务后的权威状态，
/// Duplicate 表示复用原 CommandId 的既有结果。
/// </summary>
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

/// <summary>证据写入结果；重复事件只返回原 EventId，不创建第二个投影任务。</summary>
public sealed record EvidenceRecordResult(
    string EventId,
    bool Duplicate);

/// <summary>
/// 等待投影到 Admin 的事务 Outbox 记录。
/// 锁与租约支持多副本领取，LastError 必须截断和脱敏，Payload 不包含认证秘密。
/// </summary>
public sealed record ProjectionOutboxRecord(
    string EventId,
    JsonElement Payload,
    string Status,
    int AttemptCount,
    DateTimeOffset AvailableAtUtc,
    string? LockOwner,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? LastError);

/// <summary>玩家聊天发送策略判定；禁言截止时间为 UTC，拒绝原因可安全返回客户端。</summary>
public sealed record ChatPolicyResult(
    string PlayerId,
    bool Allowed,
    DateTimeOffset? MutedUntilUtc,
    string Reason);

/// <summary>聊天消息发送前的授权请求；MessageId 用于幂等审计，不携带消息正文。</summary>
public sealed record AuthorizeChatMessageRequest(
    string MessageId,
    string PlayerId,
    string RoomId,
    DateTimeOffset RequestedAtUtc);
