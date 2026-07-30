// 玩家调查证据模型：承载举报、资产、奖励、支付、回放及聊天授权的受限投影。
// 模型只保存经过脱敏的调查数据和来源引用；凭据、完整 IP、私密手牌及直接身份信息禁止进入投影。
using System.Text.Json;

namespace GuiyangMahjong.Admin.Domain;

/// <summary>可进入玩家调查视图的证据类别；聊天内容采用单独授权模型，不属于普通证据枚举。</summary>
public enum PlayerEvidenceType
{
    Report,
    AssetChange,
    RewardClaim,
    PaymentOrder,
    Replay
}

/// <summary>
/// 证据敏感等级，用于确定可见角色、审计强度与展示脱敏规则。
/// Financial 不代表可修改资产，只允许查看经过裁剪的财务投影。
/// </summary>
public enum PlayerEvidenceSensitivity
{
    Operational,
    Restricted,
    Financial
}

/// <summary>
/// 已接收的玩家证据投影。
/// EventId 是跨重试幂等键，发生时间来自源系统，接收时间由 Admin 生成；
/// Data 必须通过字段黑名单、大小和敏感等级校验。
/// </summary>
public sealed record PlayerEvidenceRecord(
    string EventId,
    string PlayerId,
    PlayerEvidenceType EvidenceType,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    JsonElement Data,
    PlayerEvidenceSensitivity Sensitivity,
    DateTimeOffset IngestedAtUtc);

/// <summary>证据接收结果；重复事件返回既有证据，不能再次追加调查事实。</summary>
public sealed record PlayerEvidenceIngestResult(
    PlayerEvidenceRecord Evidence,
    bool Duplicate);

/// <summary>
/// 受信内部服务提交的证据请求。
/// SourceReference 只保存可回查的源记录标识，Data 不得包含完整 IP、身份信息或任何凭据。
/// </summary>
public sealed record IngestPlayerEvidenceRequest(
    string EventId,
    string PlayerId,
    PlayerEvidenceType EvidenceType,
    DateTimeOffset OccurredAtUtc,
    string SourceReference,
    JsonElement Data,
    PlayerEvidenceSensitivity Sensitivity);

/// <summary>
/// 聊天查询的限时授权证据。
/// 阅读人与审批人必须不同；授权同时受玩家、工单、UTC 查询窗口、过期时间和字段范围约束。
/// </summary>
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

/// <summary>聊天授权接收结果；重复 GrantId 只返回原记录，不延长授权有效期。</summary>
public sealed record PlayerChatAccessGrantIngestResult(
    PlayerChatAccessGrant Grant,
    bool Duplicate);

/// <summary>
/// 内部合规系统提交的聊天授权请求。
/// Scopes 只能取服务端白名单值，过期时间不能超出短时访问策略。
/// </summary>
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

/// <summary>
/// 某玩家和工单在当前时刻的聊天访问判定。
/// 拒绝结果使用空范围与原因说明；允许结果返回的窗口不得超过原始授权。
/// </summary>
public sealed record PlayerChatPermissionResult(
    bool Allowed,
    string PlayerId,
    string TicketId,
    string[] Scopes,
    DateTimeOffset? WindowStartsAtUtc,
    DateTimeOffset? WindowEndsAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string Reason);
