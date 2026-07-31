// 玩家资产操作模型：描述补偿发放、错误奖励撤销及其幂等证据和审批状态。
// 资产变更必须绑定工单、TraceId 和不可复用的操作标识，不得允许调用方直接提交最终余额。
using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

/// <summary>
/// 允许进入玩家资产闭环的操作类型；只表达增量补偿或撤销既有奖励，
/// 不提供直接设置余额的能力。
/// </summary>
public enum PlayerAssetOperationType
{
    GrantCompensation,
    RevokeReward
}

/// <summary>
/// 玩家资产操作的不可变业务证据。
/// 金额使用资产最小整数单位，奖励撤销通过原始发放标识定位；
/// 申请人、审批人、工单和 TraceId 必须贯穿 Admin 与 Economy 两侧。
/// </summary>
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

/// <summary>
/// 资产操作创建结果；<paramref name="Duplicate"/> 表示同一来源命令已存在，
/// 调用方应复用返回记录而不能重复执行经济变更。
/// </summary>
public sealed record PlayerAssetOperationCreateResult(
    PlayerAssetOperationRecord Operation,
    bool Duplicate);
