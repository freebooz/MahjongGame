namespace GuiyangMahjong.Economy.Domain;

/// <summary>奖励领取命令；金额使用资产最小整数单位，客户端不得提交最终余额。</summary>
public sealed record RewardClaimRequest(string EventId, string RewardGrantId, string PlayerId,
    string AssetCode, long Amount, DateTimeOffset OccurredAtUtc, string SourceReference, string TraceId);

/// <summary>经过双人审批的增量资产命令；仅支持补偿和按原奖励撤销。</summary>
public sealed record AdminWalletOperationRequest(string OperationType, string PlayerId, string CaseId,
    string? AssetCode, long? Amount, string? RewardGrantId, string RequestedBy, string ApprovedBy,
    string Reason, string TicketId, string TraceId, DateTimeOffset ApprovedAtUtc);

/// <summary>玩家单项资产的权威余额及乐观版本。</summary>
public sealed record WalletBalance(string PlayerId, string AssetCode, long Balance, long Version,
    DateTimeOffset UpdatedAtUtc);

/// <summary>资产命令的持久化回执；重复命令返回首次结果且不产生第二次副作用。</summary>
public sealed record WalletOperationResult(string CommandId, string TransactionId, string OperationType,
    string PlayerId, string AssetCode, long Amount, long BalanceAfter, long BalanceVersion,
    string Status, bool Duplicate, DateTimeOffset CompletedAtUtc);

/// <summary>奖励领取回执；Duplicate 表示事件或奖励已被当前所有者处理。</summary>
public sealed record RewardClaimResult(string EventId, bool Duplicate);

/// <summary>稳定的 Economy 领域错误，供兼容入口保持旧 HTTP 语义。</summary>
public sealed class EconomyOperationException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
