using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.Configuration.Domain;
using GuiyangMahjong.Configuration.Infrastructure;
using GuiyangMahjong.Configuration.Options;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Configuration.Services;

/// <summary>配置 Schema、安全或审批校验失败；Code 是稳定外部错误码，Message 不包含配置正文。</summary>
public sealed class ConfigurationOperationException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// 配置发布领域服务，串联草稿、验证、异人审批、不可变版本、签名、Outbox、回滚和应用回执。
/// 服务不持有 Kubernetes/Agones 权限；Fleet 路由只是供 Allocation Service 选择的已签名策略。
/// </summary>
public sealed class PlatformConfigurationService(
    IConfigurationStore store,
    IOptions<ConfigurationOptions> options,
    TimeProvider timeProvider,
    ILogger<PlatformConfigurationService> logger)
{
    public const string PlatformConfigKey = "platform.runtime";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] signingKey = Encoding.UTF8.GetBytes(options.Value.SigningKey);
    private readonly int retainedVersions = options.Value.RetainedVersions;

    /// <summary>以操作者和幂等键创建草稿；同键同正文返回首次结果，同键不同正文冲突。</summary>
    public async Task<ConfigurationDraft> CreateDraftAsync(
        CreateConfigurationDraftRequest request,
        string operatorId,
        string traceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (request.ConfigKey != PlatformConfigKey || request.SchemaVersion != 1)
            throw Invalid("CONFIG_SCHEMA_UNSUPPORTED", "当前仅支持 platform.runtime v1 强类型配置。");
        RequireIdentifier(operatorId, nameof(operatorId));
        RequireIdentifier(request.TicketId, nameof(request.TicketId));
        RequireIdentifier(request.ReasonCode, nameof(request.ReasonCode));
        RequireOperationKey(idempotencyKey, nameof(idempotencyKey));
        var now = timeProvider.GetUtcNow();
        var draft = new ConfigurationDraft(
            Guid.NewGuid().ToString(), request.ConfigKey, request.SchemaVersion, request.Payload,
            ConfigurationPolicy.HashPayload(request.Payload), ConfigurationDraftStatus.Draft,
            operatorId, now, null, null, null, null, request.ReasonCode,
            request.TicketId, NormalizeTrace(traceId), idempotencyKey, 1);
        return await store.CreateDraftAsync(draft, cancellationToken);
    }

    /// <summary>执行强类型 Schema、敏感字段和历史不可变性检查；失败时草稿保持 Draft，可修正后创建新草稿。</summary>
    public async Task<ConfigurationDraft> ValidateDraftAsync(
        string draftId, string operatorId, CancellationToken cancellationToken)
    {
        var draft = await RequireDraftAsync(draftId, cancellationToken);
        if (draft.Status == ConfigurationDraftStatus.Validated) return draft;
        if (draft.Status != ConfigurationDraftStatus.Draft)
            throw Conflict("CONFIG_DRAFT_STATE_INVALID", "只有 Draft 状态可以执行验证。");
        var schema = ConfigurationPolicy.Validate(draft.Payload);
        // 不可变约束必须覆盖全部历史版本，不能因为展示层只保留最近若干版本而漏检旧 Build/RuleSet。
        var history = await store.ListVersionsAsync(draft.ConfigKey, int.MaxValue, cancellationToken);
        var immutable = ConfigurationPolicy.ValidateImmutability(draft.Payload, history);
        var errors = schema.Errors.Concat(immutable.Errors).Distinct(StringComparer.Ordinal).ToArray();
        if (errors.Length > 0)
        {
            logger.LogWarning(
                "配置草稿验证失败 DraftId={DraftId} ErrorCodes={ErrorCodes} TraceId={TraceId}",
                draft.DraftId,
                string.Join(',', errors),
                draft.TraceId);
            throw new ConfigurationOperationException(
                "CONFIG_VALIDATION_FAILED",
                string.Join(',', errors),
                StatusCodes.Status422UnprocessableEntity);
        }
        var validated = draft with
        {
            Status = ConfigurationDraftStatus.Validated,
            ValidatedBy = operatorId,
            ValidatedAtUtc = timeProvider.GetUtcNow(),
            Revision = draft.Revision + 1
        };
        return await store.TransitionDraftAsync(validated, draft.Revision, cancellationToken);
    }

    /// <summary>
    /// 使用 Admin 已完成的异人审批发布草稿。审批人不能等于草稿创建人或发布申请人；
    /// 发布事务失败时当前版本和 Outbox 均不改变，不允许调用方透明重试不同正文。
    /// </summary>
    public async Task<PublishedConfiguration> PublishAsync(
        string draftId,
        PublishConfigurationCommand command,
        CancellationToken cancellationToken)
    {
        RequirePublishCommand(command.OperatorId, command.ApproverId, command.ApprovalId,
            command.TicketId, command.IdempotencyKey);
        var draft = await RequireDraftAsync(draftId, cancellationToken);
        if (draft.Status == ConfigurationDraftStatus.Published)
        {
            var currentVersions = await store.ListVersionsAsync(draft.ConfigKey, retainedVersions, cancellationToken);
            return currentVersions.FirstOrDefault(item => item.PayloadHash == draft.PayloadHash)
                ?? throw Conflict("PUBLISHED_VERSION_NOT_FOUND", "草稿已发布但版本索引不可用。");
        }
        if (draft.Status is not (ConfigurationDraftStatus.Validated or ConfigurationDraftStatus.Approved))
            throw Conflict("CONFIG_DRAFT_NOT_VALIDATED", "配置草稿必须先通过 Schema 与安全验证。");
        if (draft.CreatedBy == command.ApproverId || command.OperatorId == command.ApproverId)
            throw new ConfigurationOperationException(
                "CONFIG_TWO_PERSON_APPROVAL_REQUIRED",
                "配置发布必须由不同管理员审批。",
                StatusCodes.Status403Forbidden);
        if (draft.TicketId != command.TicketId)
            throw Conflict("CONFIG_TICKET_MISMATCH", "发布工单与草稿工单不一致。");

        // 审批状态先持久化；若发布事务随后短暂失败，相同命令可以从 Approved 状态安全续跑。
        var approved = draft;
        if (draft.Status == ConfigurationDraftStatus.Validated)
        {
            approved = draft with
            {
                Status = ConfigurationDraftStatus.Approved,
                ApprovedBy = command.ApproverId,
                ApprovedAtUtc = timeProvider.GetUtcNow(),
                Revision = draft.Revision + 1
            };
            approved = await store.TransitionDraftAsync(approved, draft.Revision, cancellationToken);
        }
        else if (!string.Equals(draft.ApprovedBy, command.ApproverId, StringComparison.Ordinal))
        {
            throw Conflict("CONFIG_APPROVAL_MISMATCH", "重试发布时审批记录必须与首次审批一致。");
        }

        var history = await store.ListVersionsAsync(approved.ConfigKey, int.MaxValue, cancellationToken);
        var immutable = ConfigurationPolicy.ValidateImmutability(approved.Payload, history);
        if (!immutable.IsValid)
            throw new ConfigurationOperationException(
                "CONFIG_IMMUTABILITY_VIOLATION",
                string.Join(',', immutable.Errors),
                StatusCodes.Status409Conflict);
        var now = timeProvider.GetUtcNow();
        var versionNumber = history.Count == 0 ? 1 : history.Max(item => item.Version) + 1;
        var unsigned = new PublishedConfiguration(
            Guid.NewGuid().ToString(), approved.ConfigKey, versionNumber, approved.SchemaVersion,
            approved.Payload, approved.PayloadHash, string.Empty, now, command.OperatorId,
            command.ApproverId, command.TicketId, NormalizeTrace(command.TraceId), null);
        var version = unsigned with { Signature = Sign(unsigned) };
        var publishedDraft = approved with { Status = ConfigurationDraftStatus.Published, Revision = approved.Revision + 1 };
        var envelope = CreateEvent(version, command.IdempotencyKey);
        var result = await store.PublishAsync(
            publishedDraft,
            approved.Revision,
            version,
            JsonSerializer.Serialize(envelope, JsonOptions),
            command.IdempotencyKey,
            cancellationToken);
        ConfigurationTelemetry.RecordPublished(result.ConfigKey, result.Version, false);
        return result;
    }

    /// <summary>复制历史有效正文生成更高的新版本；不会覆盖目标版本，也不会终止使用新版本前创建的旧房间。</summary>
    public async Task<PublishedConfiguration> RollbackAsync(
        string configKey,
        RollbackConfigurationCommand command,
        CancellationToken cancellationToken)
    {
        RequirePublishCommand(command.OperatorId, command.ApproverId, command.ApprovalId,
            command.TicketId, command.IdempotencyKey);
        if (command.OperatorId == command.ApproverId)
            throw new ConfigurationOperationException(
                "CONFIG_TWO_PERSON_APPROVAL_REQUIRED", "配置回滚必须由不同管理员审批。", StatusCodes.Status403Forbidden);
        var target = await store.GetVersionAsync(configKey, command.TargetVersion, cancellationToken)
            ?? throw new ConfigurationOperationException("CONFIG_VERSION_NOT_FOUND", "目标配置版本不存在。", StatusCodes.Status404NotFound);
        // 回滚也会创建新版本，因此版本号必须根据全部历史单调递增。
        var history = await store.ListVersionsAsync(configKey, int.MaxValue, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var nextVersion = history.Max(item => item.Version) + 1;
        var unsigned = target with
        {
            VersionId = Guid.NewGuid().ToString(), Version = nextVersion, Signature = string.Empty,
            PublishedAtUtc = now, PublishedBy = command.OperatorId, ApprovedBy = command.ApproverId,
            TicketId = command.TicketId, TraceId = NormalizeTrace(command.TraceId), RollbackOfVersion = target.Version
        };
        var version = unsigned with { Signature = Sign(unsigned) };
        var envelope = CreateEvent(version, command.IdempotencyKey);
        var result = await store.PublishRollbackAsync(
            version, JsonSerializer.Serialize(envelope, JsonOptions), command.IdempotencyKey, cancellationToken);
        ConfigurationTelemetry.RecordPublished(result.ConfigKey, result.Version, true);
        return result;
    }

    public Task<PublishedConfiguration?> GetCurrentAsync(string configKey, CancellationToken cancellationToken) =>
        store.GetCurrentAsync(configKey, cancellationToken);
    public Task<IReadOnlyList<PublishedConfiguration>> ListVersionsAsync(string configKey, CancellationToken cancellationToken) =>
        store.ListVersionsAsync(configKey, retainedVersions, cancellationToken);
    public Task<IReadOnlyList<ConfigurationDraft>> ListDraftsAsync(CancellationToken cancellationToken) =>
        store.ListDraftsAsync(200, cancellationToken);
    /// <summary>按草稿标识读取审批对象；仅供受信 Admin BFF 展示操作前状态。</summary>
    public Task<ConfigurationDraft?> GetDraftAsync(string draftId, CancellationToken cancellationToken) =>
        store.GetDraftAsync(draftId, cancellationToken);
    public async Task RecordApplicationAsync(ConfigurationApplicationReport report, CancellationToken cancellationToken)
    {
        await store.RecordApplicationAsync(report, cancellationToken);
        ConfigurationTelemetry.RecordApplication(report.Result, report.ServiceName);
    }

    /// <summary>验证服务拉取版本的签名和正文哈希；消费者使用相同规范实现并在失败时保留 LKG。</summary>
    public bool Verify(PublishedConfiguration version)
    {
        var expectedHash = ConfigurationPolicy.HashPayload(version.Payload);
        if (!FixedEquals(expectedHash, version.PayloadHash)) return false;
        return FixedEquals(Sign(version with { Signature = string.Empty }), version.Signature);
    }

    /// <summary>根据客户端上下文求值公开功能开关；只返回单一组，不产生任何写请求影子副作用。</summary>
    public ClientConfigurationView EvaluateClient(PublishedConfiguration version, RolloutSubject subject)
    {
        var canary = version.Payload.Rollouts.Any(rule => StableRolloutEvaluator.IsCanary(rule, subject));
        var blocked = version.Payload.Client.BlockedVersions.Contains(subject.ClientVersion, StringComparer.Ordinal);
        return new ClientConfigurationView(
            version.Version, version.Payload.Client.MinimumVersion, version.Payload.Client.RecommendedVersion,
            blocked, version.Payload.Client.SupportedProtocolVersions, version.Payload.FeatureFlags,
            canary ? "canary" : "stable", version.PublishedAtUtc);
    }

    private EventEnvelope CreateEvent(PublishedConfiguration version, string idempotencyKey) =>
        EventEnvelope.Create(
            new ConfigurationPublished(version.ConfigKey, version.Version, version.PayloadHash,
                version.PublishedAtUtc, version.RollbackOfVersion),
            "configuration", version.ConfigKey, version.Version, "configuration-service",
            version.TraceId, CorrelationId.Parse(version.TraceId), version.PublishedAtUtc,
            idempotencyKey: IdempotencyKey.Parse(idempotencyKey));

    private string Sign(PublishedConfiguration version)
    {
        var material = $"{version.ConfigKey}\n{version.Version}\n{version.SchemaVersion}\n{version.PayloadHash}\n{version.PublishedAtUtc:O}\n{version.RollbackOfVersion}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(material)));
    }

    private async Task<ConfigurationDraft> RequireDraftAsync(string draftId, CancellationToken cancellationToken) =>
        await store.GetDraftAsync(draftId, cancellationToken)
        ?? throw new ConfigurationOperationException("CONFIG_DRAFT_NOT_FOUND", "配置草稿不存在。", StatusCodes.Status404NotFound);

    private static void RequirePublishCommand(string operatorId, string approverId, string approvalId, string ticketId, string key)
    {
        RequireIdentifier(operatorId, nameof(operatorId)); RequireIdentifier(approverId, nameof(approverId));
        RequireIdentifier(approvalId, nameof(approvalId)); RequireIdentifier(ticketId, nameof(ticketId));
        RequireOperationKey(key, nameof(key));
    }

    private static void RequireIdentifier(string value, string field)
    {
        if (!StrongValueValidation.IsIdentifier(value)) throw Invalid("CONFIG_IDENTIFIER_INVALID", $"{field} 格式无效。");
    }
    private static void RequireOperationKey(string value, string field)
    {
        if (!StrongValueValidation.IsOperationKey(value)) throw Invalid("CONFIG_OPERATION_KEY_INVALID", $"{field} 格式无效。");
    }
    private static string NormalizeTrace(string traceId) =>
        StrongValueValidation.IsOperationKey(traceId) ? traceId : Guid.NewGuid().ToString("N");
    private static bool FixedEquals(string left, string right) => left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static ConfigurationOperationException Invalid(string code, string message) => new(code, message, StatusCodes.Status400BadRequest);
    private static ConfigurationOperationException Conflict(string code, string message) => new(code, message, StatusCodes.Status409Conflict);
}
