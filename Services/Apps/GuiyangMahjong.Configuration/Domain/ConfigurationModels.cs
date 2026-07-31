using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuiyangMahjong.Configuration.Domain;

/// <summary>配置草稿状态机；任何跳步都会被领域服务拒绝。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConfigurationDraftStatus
{
    Draft,
    Validated,
    Approved,
    Published,
    Rejected
}

/// <summary>灰度主体上下文；玩家未登录时 PlayerId 为空并改用不可逆设备摘要稳定分桶。</summary>
public sealed record RolloutSubject(
    string? PlayerId,
    string? DeviceDigest,
    string Channel,
    string ClientVersion,
    string Platform,
    string Region,
    bool IsTestAccount);

/// <summary>
/// 单个灰度实验。PercentageBasisPoints 使用万分比；白名单优先于固定分桶，区域等维度只做收窄，
/// 写请求永远只选择一个组，不允许影子执行副作用。
/// </summary>
public sealed record RolloutRule(
    string ExperimentId,
    int PercentageBasisPoints,
    string[] PlayerWhitelist,
    string[] DeviceWhitelist,
    string[] Channels,
    string[] ClientVersions,
    string[] Platforms,
    string[] Regions,
    bool IncludeTestAccounts,
    bool Enabled);

/// <summary>
/// DS Fleet 不可变路由。相同 ServerBuild 必须始终绑定相同镜像摘要，相同 RuleSetVersion 必须始终绑定相同规则包摘要。
/// StopNewAllocations 只停止新房间流量，不终止已经运行的旧房间。
/// </summary>
public sealed record FleetRoute(
    string RouteId,
    string Fleet,
    string ServerBuild,
    string ServerImageDigest,
    string RuleSetVersion,
    string RuleSetPackageHash,
    string ProtocolVersion,
    string Region,
    string Cell,
    string CanaryGroup,
    string? ExperimentId,
    bool StopNewAllocations);

/// <summary>客户端兼容策略；阻断版本优先于最低版本，推荐版本只提示升级而不直接拒绝。</summary>
public sealed record ClientVersionPolicy(
    string MinimumVersion,
    string RecommendedVersion,
    string[] BlockedVersions,
    string[] SupportedProtocolVersions);

/// <summary>房间模板只保存控制面默认值，不包含完整手牌、随机种子或结算结果。</summary>
public sealed record RoomTemplate(
    string TemplateId,
    string RuleSetVersion,
    int RoundCount,
    int MaximumPlayers,
    bool Enabled);

/// <summary>
/// 平台动态配置唯一强类型载荷。它只包含可公开或可由服务读取的业务策略，禁止数据库连接、令牌、证书和私钥。
/// </summary>
public sealed record PlatformConfigurationPayload(
    ClientVersionPolicy Client,
    int ApiProtocolVersion,
    IReadOnlyDictionary<string, bool> FeatureFlags,
    RolloutRule[] Rollouts,
    FleetRoute[] FleetRoutes,
    RoomTemplate[] RoomTemplates,
    string RiskPolicyVersion);

/// <summary>可审计配置草稿；PayloadHash 固定草稿正文，后续审批不得静默替换。</summary>
public sealed record ConfigurationDraft(
    string DraftId,
    string ConfigKey,
    int SchemaVersion,
    PlatformConfigurationPayload Payload,
    string PayloadHash,
    ConfigurationDraftStatus Status,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    string? ValidatedBy,
    DateTimeOffset? ValidatedAtUtc,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAtUtc,
    string? ReasonCode,
    string TicketId,
    string TraceId,
    string IdempotencyKey,
    long Revision);

/// <summary>
/// 已发布不可变配置版本。Signature 覆盖键、版本、哈希和发布时间；RollbackOfVersion 只描述来源，
/// 回滚仍生成新版本，绝不覆盖历史版本正文。
/// </summary>
public sealed record PublishedConfiguration(
    string VersionId,
    string ConfigKey,
    long Version,
    int SchemaVersion,
    PlatformConfigurationPayload Payload,
    string PayloadHash,
    string Signature,
    DateTimeOffset PublishedAtUtc,
    string PublishedBy,
    string ApprovedBy,
    string TicketId,
    string TraceId,
    long? RollbackOfVersion);

/// <summary>业务服务应用配置后的回执；不包含配置正文或敏感启动参数。</summary>
public sealed record ConfigurationApplicationReport(
    string ReportId,
    string ConfigKey,
    long Version,
    string ServiceName,
    string ServiceVersion,
    string Region,
    string Cell,
    string Result,
    string? ErrorCode,
    DateTimeOffset AppliedAtUtc,
    string TraceId);

/// <summary>创建草稿请求；Idempotency-Key 从请求头读取，不允许正文自行覆盖。</summary>
public sealed record CreateConfigurationDraftRequest(
    string ConfigKey,
    int SchemaVersion,
    PlatformConfigurationPayload Payload,
    string ReasonCode,
    string TicketId);

/// <summary>审批并发布命令；审批人与创建人必须不同，且必须由 Admin 高风险工作流产生。</summary>
public sealed record PublishConfigurationCommand(
    string OperatorId,
    string ApproverId,
    string ApprovalId,
    string ReasonCode,
    string TicketId,
    string TraceId,
    string IdempotencyKey);

/// <summary>快速回滚请求；目标必须是已发布历史版本，回滚会产生新的不可变版本。</summary>
public sealed record RollbackConfigurationCommand(
    long TargetVersion,
    string OperatorId,
    string ApproverId,
    string ApprovalId,
    string ReasonCode,
    string TicketId,
    string TraceId,
    string IdempotencyKey);

/// <summary>客户端仅获取经过灰度求值后的非敏感视图，不返回签名密钥、完整白名单或内部 Fleet 策略。</summary>
public sealed record ClientConfigurationView(
    long ConfigVersion,
    string MinimumVersion,
    string RecommendedVersion,
    bool Blocked,
    string[] SupportedProtocolVersions,
    IReadOnlyDictionary<string, bool> FeatureFlags,
    string CanaryGroup,
    DateTimeOffset PublishedAtUtc);
