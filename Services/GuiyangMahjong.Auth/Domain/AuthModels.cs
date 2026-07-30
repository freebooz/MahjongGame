// Auth 领域模型：定义登录身份、访问/刷新令牌、设备会话、账号控制和管理命令契约。
// 密码、刷新令牌和签名密钥不得以明文持久化或序列化到响应，时间字段统一使用 UTC。
namespace GuiyangMahjong.Auth.Domain;

/// <summary>游客登录输入；InstallationId 是设备安装级稳定标识，展示名仅作为可清洗的用户偏好。</summary>
public sealed record GuestLoginRequest(string InstallationId, string? DisplayName);

/// <summary>刷新会话输入；RefreshToken 只在请求处理内短暂使用，禁止进入日志和持久化明文字段。</summary>
public sealed record RefreshSessionRequest(string RefreshToken);

/// <summary>登出输入；服务端通过刷新令牌哈希定位并撤销会话。</summary>
public sealed record LogoutRequest(string RefreshToken);

/// <summary>登录风控观察值；IP 必须预先脱敏，客户端摘要不得包含设备秘密或完整 User-Agent 指纹。</summary>
public sealed record LoginObservation(string MaskedIp, string ClientSummary);

/// <summary>Admin 强制撤销玩家会话的内部命令；生效时间为服务端认可的 UTC 时间。</summary>
public sealed record AdminRevokePlayerSessionsRequest(
    string Reason,
    string TraceId,
    DateTimeOffset EffectiveAtUtc);

/// <summary>会话撤销结果；CommandId 支持幂等重放，撤销数量只统计本次真正改变的会话。</summary>
public sealed record AdminRevokePlayerSessionsResult(
    string CommandId,
    string PlayerId,
    bool PlayerFound,
    int RevokedSessionCount,
    DateTimeOffset EffectiveAtUtc,
    bool Duplicate);

/// <summary>Auth 可执行的玩家控制动作白名单；不包含资产或对局结果修改能力。</summary>
public enum AdminPlayerControlAction
{
    TemporaryFreezePlayer,
    PermanentBanPlayer,
    LiftPlayerBan,
    MutePlayer,
    UnmutePlayer,
    MarkRiskAccount
}

/// <summary>
/// Admin 提交给 Auth 的玩家控制命令。
/// ExpectedVersion 提供乐观并发保护，申请人与审批人必须分离；
/// 临时冻结、禁言和风险标签必须带受策略限制的 UTC 到期时间。
/// </summary>
public sealed record AdminUpdatePlayerControlRequest(
    string ActionType,
    long ExpectedVersion,
    string Reason,
    string TraceId,
    string TicketId,
    string RequestedBy,
    string ApprovedBy,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? RiskLabel);

/// <summary>
/// 玩家控制状态快照。
/// Version 每次成功控制递增；冻结、禁言和风险标签按各自到期时间惰性规范化，
/// RiskLabels 是服务端排序去重后的不可变投影。
/// </summary>
public sealed record PlayerControlState(
    long Version,
    string AccountStatus,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels,
    DateTimeOffset? RiskLabelsExpireAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 玩家控制审计事件，完整保存命令关联、双人身份、前后状态和会话撤销数量。
/// 事件属于追加事实，不能在后续解封或解除禁言时覆盖。
/// </summary>
public sealed record PlayerControlEvent(
    string CommandId,
    string PlayerId,
    string ActionType,
    string Reason,
    string TraceId,
    string TicketId,
    string RequestedBy,
    string ApprovedBy,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? RiskLabel,
    int RevokedSessionCount,
    PlayerControlState BeforeState,
    PlayerControlState AfterState);

/// <summary>存储层执行控制命令的分类结果，用于区分幂等、并发冲突和非法状态迁移。</summary>
public enum AdminPlayerControlStatus
{
    Applied,
    Duplicate,
    PlayerNotFound,
    VersionConflict,
    InvalidTransition
}

/// <summary>成功或幂等执行的玩家控制结果；Duplicate=true 时复用原命令事实。</summary>
public sealed record AdminUpdatePlayerControlResult(
    string CommandId,
    string PlayerId,
    string ActionType,
    PlayerControlState BeforeState,
    PlayerControlState AfterState,
    int RevokedSessionCount,
    bool Duplicate);

/// <summary>
/// Auth 存储返回给服务层的完整判定。
/// 非成功状态可附带当前状态帮助调用方刷新快照，但 Error 必须脱敏。
/// </summary>
public sealed record AdminPlayerControlStoreResult(
    AdminPlayerControlStatus Status,
    AdminUpdatePlayerControlResult? Result,
    PlayerControlState? CurrentState,
    string? Error);

/// <summary>
/// 成功认证响应。
/// 访问令牌短期用于 API，刷新令牌仅此处返回一次；两个过期时间均为 UTC，
/// 响应不得写入结构化日志、指标标签或审计状态快照。
/// </summary>
public sealed record AuthSessionResponse(
    string PlayerId,
    string DisplayName,
    string Provider,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);

/// <summary>稳定玩家身份；不包含密码、令牌、IP 或设备历史。</summary>
public sealed record AuthIdentity(
    string PlayerId,
    string DisplayName,
    string Provider,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// 刷新会话持久化实体。
/// TokenHash 只保存不可逆哈希；RevokedAtUtc 非空即不可再次轮换，字节数组不得暴露到 API。
/// </summary>
public sealed record RefreshSession(
    string SessionId,
    string PlayerId,
    byte[] TokenHash,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc);

/// <summary>脱敏登录历史；DeviceId 是内部设备引用，MaskedIp 不能还原完整地址。</summary>
public sealed record AuthLoginEvent(
    string EventId,
    string PlayerId,
    string DeviceId,
    string MaskedIp,
    string ClientSummary,
    string Outcome,
    DateTimeOffset OccurredAtUtc);

/// <summary>供管理监控使用的会话投影；SessionReference 不能用作认证凭据。</summary>
public sealed record AuthSessionMonitor(
    string SessionReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    bool Active);

/// <summary>
/// 玩家目录列表投影，聚合基本身份、账号状态、最近登录、当前脱敏设备/IP 和控制版本。
/// ActiveSessionCount 是查询时点计数，不保证跨页面请求保持不变。
/// </summary>
public sealed record PlayerDirectoryItem(
    string PlayerId,
    string DisplayName,
    string Provider,
    string AccountStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    string? CurrentDeviceId,
    string? CurrentMaskedIp,
    int ActiveSessionCount,
    long ControlVersion,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels);

/// <summary>
/// 玩家目录详情；会话、登录和控制历史均为有界数组，
/// 返回前仍需按调用角色执行字段级脱敏。
/// </summary>
public sealed record PlayerDirectoryDetail(
    PlayerDirectoryItem Player,
    AuthSessionMonitor[] Sessions,
    AuthLoginEvent[] LoginHistory,
    string[] KnownDeviceIds,
    PlayerControlEvent[] ControlHistory);

/// <summary>创建刷新会话的结果；冻结或封禁时不得产生任何新令牌。</summary>
public enum SessionCreationStatus { Created, Frozen, Banned }

/// <summary>刷新令牌单次轮换的结果分类；只有 Rotated 会返回新的身份与会话。</summary>
public enum RefreshRotationStatus
{
    Rotated,
    NotFound,
    Invalid,
    Expired,
    Revoked,
    Frozen,
    Banned
}

/// <summary>刷新轮换结果；失败状态下 Identity 通常为空，调用方不得沿用旧访问令牌。</summary>
public sealed record RefreshRotationResult(RefreshRotationStatus Status, AuthIdentity? Identity);
