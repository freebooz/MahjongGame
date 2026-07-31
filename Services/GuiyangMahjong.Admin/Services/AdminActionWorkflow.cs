using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// Admin 高风险操作的领域工作流。
/// 依次执行 RBAC/ABAC、输入与前置状态校验、二次确认、职责分离审批和持久化迁移；
/// 实际下游副作用由事务 Outbox 执行器完成，工作流不允许普通运营直接修改对局结果。
/// </summary>
public sealed partial class AdminActionWorkflow(
    IAdminActionStore store,
    IAdminCaseStore caseStore,
    MonitoringAggregationService monitoring,
    PlayerMonitoringService playerMonitoring,
    AdminAbacPolicyService abacPolicy,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider)
{
    // 管理策略在服务启动时验证并冻结，定义开关、确认/审批 TTL 和限制阈值。
    private readonly AdminManagementOptions management = options.Value.Management;

    /// <summary>
    /// 创建非显式幂等调用的管理动作；服务端生成随机动作标识。
    /// 仅供兼容入口使用，新 HTTP 写入口应优先传入 Idempotency-Key 重载。
    /// </summary>
    public async Task<AdminActionRecord> CreateAsync(
        AdminPrincipal principal,
        CreateAdminActionRequest request,
        string traceId,
        CancellationToken cancellationToken)
        => await CreateAsync(
            principal,
            request,
            traceId,
            null,
            cancellationToken);

    /// <summary>
    /// 创建 AwaitingConfirmation 动作并写首条审计。
    /// 同一操作者的 Idempotency-Key 确定性映射为动作标识，冲突载荷被拒绝；
    /// 创建前读取目标快照并校验预期序号、案件和制裁引用，不执行下游命令。
    /// </summary>
    public async Task<AdminActionRecord> CreateAsync(
        AdminPrincipal principal,
        CreateAdminActionRequest request,
        string traceId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        RequireRole(principal, IsPlayerAction(request.ActionType)
            ? AdminRoles.PlayerViewer
            : AdminRoles.RoomViewer);
        RequireRole(principal, RequiredOperatorRole(request.ActionType));
        var parameters = ValidateInput(request);
        var reasonCode = NormalizeReasonCode(request.ReasonCode);
        var operationDescription = NormalizeOperationDescription(
            request.OperationDescription,
            request.Reason);
        var actionRequestId = string.IsNullOrEmpty(idempotencyKey)
            ? Guid.NewGuid().ToString()
            : CreateDeterministicActionId(principal.OperatorId, idempotencyKey);
        var existing = await store.GetAsync(
            actionRequestId,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.ActionType != request.ActionType
                || existing.TargetId != request.TargetId
                || existing.RequestedBy != principal.OperatorId
                || existing.Reason != NormalizeText(request.Reason)
                || existing.TicketId != NormalizeText(request.TicketId)
                || existing.ReasonCode != reasonCode
                || existing.OperationDescription != operationDescription
                || !SameJson(existing.Parameters, parameters))
            {
                throw AdminOperationException.Conflict(
                    "Idempotency-Key was reused for a different management request.");
            }
            return existing;
        }
        await EnsureAssetCaseAsync(
            request.ActionType,
            parameters,
            cancellationToken);
        await EnsureSanctionReferenceAsync(
            request.ActionType,
            request.TargetId,
            parameters,
            cancellationToken);
        var target = await LoadTargetAsync(
            request.ActionType, request.TargetId, cancellationToken);
        EnsureExpectedState(
            request.ExpectedStateSequence, null, target);
        var now = timeProvider.GetUtcNow();
        var action = new AdminActionRecord(
            actionRequestId,
            request.ActionType,
            target.TargetType,
            request.TargetId,
            principal.OperatorId,
            now,
            now.AddMinutes(management.ConfirmationTtlMinutes),
            now.AddMinutes(management.ApprovalTtlMinutes),
            null,
            NormalizeText(request.Reason),
            NormalizeText(request.TicketId),
            NormalizeTraceId(traceId),
            request.ExpectedStateSequence,
            target.StateHash,
            target.State,
            AdminActionStatus.AwaitingConfirmation,
            null,
            1,
            parameters,
            reasonCode,
            operationDescription,
            null,
            NormalizeIdempotencyKey(idempotencyKey));
        await store.CreateAsync(
            action,
            Audit(
                now, principal.OperatorId, "ActionRequested", action,
                null, JsonSerializer.SerializeToElement(action), null),
            cancellationToken);
        return await store.GetAsync(
                action.ActionRequestId,
                cancellationToken)
            ?? action;
    }

    private static string CreateDeterministicActionId(
        string operatorId,
        string idempotencyKey)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{operatorId}\n{idempotencyKey}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString();
    }

    private static bool SameJson(JsonElement? left, JsonElement? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue
            || JsonElement.DeepEquals(left.Value, right!.Value));

    /// <summary>
    /// 在确认 TTL 内校验操作者本人和目标确认文本，将动作迁移到 PendingApproval。
    /// 版本或状态已变化时失败，不延长原审批过期时间。
    /// </summary>
    public async Task<AdminActionRecord> ConfirmAsync(
        AdminPrincipal principal,
        string actionRequestId,
        ConfirmAdminActionRequest request,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var action = await RequireActionAsync(actionRequestId, cancellationToken);
        if (!string.Equals(action.RequestedBy, principal.OperatorId, StringComparison.Ordinal))
            throw AdminOperationException.Forbidden("只有申请人可以完成二次确认。");
        if (action.Status != AdminActionStatus.AwaitingConfirmation)
            throw AdminOperationException.Conflict("申请当前状态不允许二次确认。");
        var now = timeProvider.GetUtcNow();
        if (action.ConfirmationExpiresAtUtc <= now || action.ExpiresAtUtc <= now)
            return await ExpireAsync(action, principal.OperatorId, now, cancellationToken);
        var confirmation = request.TargetConfirmation?.Trim() ?? string.Empty;
        if (!string.Equals(
                confirmation,
                action.TargetId,
                StringComparison.Ordinal))
        {
            throw AdminOperationException.Invalid(
                "二次确认内容必须与目标 ID 完全一致。");
        }
        var target = await LoadTargetAsync(
            action.ActionType, action.TargetId, cancellationToken);
        EnsureExpectedState(
            action.ExpectedStateSequence, action.ExpectedStateHash, target);
        var replacement = action with
        {
            ConfirmedAtUtc = now,
            Confirmation = confirmation,
            Status = AdminActionStatus.PendingApproval,
            Version = action.Version + 1
        };
        await TransitionAsync(
            action,
            replacement,
            Audit(now, principal.OperatorId, "ActionConfirmed", action,
                JsonSerializer.SerializeToElement(action),
                JsonSerializer.SerializeToElement(replacement),
                null),
            cancellationToken);
        return replacement;
    }

    /// <summary>
    /// 不带 HTTP 上下文的审批入口，适用于受控内部调用和测试。
    /// 仍执行审批角色、申请/审批人分离、状态及过期校验。
    /// </summary>
    public async Task<AdminActionRecord> ApproveAsync(
        AdminPrincipal principal,
        string actionRequestId,
        ApproveAdminActionRequest request,
        CancellationToken cancellationToken)
        => await ApproveCoreAsync(
            principal,
            actionRequestId,
            request,
            null,
            cancellationToken);

    /// <summary>
    /// 处理来自 HTTP 管理入口的审批；除角色和职责分离外，还执行班次及高额补偿 ABAC 校验。
    /// </summary>
    public async Task<AdminActionRecord> ApproveAsync(
        AdminPrincipal principal,
        string actionRequestId,
        ApproveAdminActionRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
        => await ApproveCoreAsync(
            principal,
            actionRequestId,
            request,
            context,
            cancellationToken);

    private async Task<AdminActionRecord> ApproveCoreAsync(
        AdminPrincipal principal,
        string actionRequestId,
        ApproveAdminActionRequest request,
        HttpContext? context,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var action = await RequireActionAsync(actionRequestId, cancellationToken);
        RequireRole(principal, RequiredApproverRole(action.ActionType));
        if (context is not null
            && request.Decision == ApprovalDecision.Approve)
        {
            abacPolicy.RequireCompensationApproval(context, action);
        }
        if (string.Equals(action.RequestedBy, principal.OperatorId, StringComparison.Ordinal))
            throw AdminOperationException.Forbidden("申请人不得审批自己的操作。");
        if (action.Status != AdminActionStatus.PendingApproval)
            throw AdminOperationException.Conflict("申请当前状态不允许审批。");
        if (!Enum.IsDefined(request.Decision))
            throw AdminOperationException.Invalid("审批决定无效。");
        var comment = NormalizeText(request.Comment);
        if (comment.Length is < 3 or > 1000)
            throw AdminOperationException.Invalid("审批意见长度必须为 3 到 1000 个字符。");
        var now = timeProvider.GetUtcNow();
        if (action.ExpiresAtUtc <= now)
            return await ExpireAsync(action, principal.OperatorId, now, cancellationToken);
        var target = await LoadTargetAsync(
            action.ActionType, action.TargetId, cancellationToken);
        EnsureExpectedState(
            action.ExpectedStateSequence, action.ExpectedStateHash, target);
        var approval = new AdminActionApproval(
            Guid.NewGuid().ToString(),
            principal.OperatorId,
            now,
            request.Decision,
            comment);
        var replacement = action with
        {
            Approval = approval,
            Status = request.Decision == ApprovalDecision.Approve
                ? AdminActionStatus.ApprovedAwaitingExecution
                : AdminActionStatus.Rejected,
            Version = action.Version + 1
        };
        await TransitionAsync(
            action,
            replacement,
            Audit(now, principal.OperatorId, "ActionApprovalRecorded", action,
                JsonSerializer.SerializeToElement(action),
                JsonSerializer.SerializeToElement(replacement),
                JsonSerializer.SerializeToElement(approval)),
            cancellationToken);
        return replacement;
    }

    /// <summary>
    /// 按当前主体角色过滤并返回有界管理动作列表。
    /// 审计查看者可跨操作类型读取，其他人员只看到其职责范围或本人申请记录。
    /// </summary>
    public async Task<IReadOnlyList<AdminActionRecord>> ListAsync(
        AdminPrincipal principal,
        int limit,
        CancellationToken cancellationToken)
    {
        EnsureAnyRole(principal, AdminRoles.RoomOperator, AdminRoles.RoomApprover,
            AdminRoles.PlayerOperator, AdminRoles.PlayerApprover,
            AdminRoles.SanctionOperator, AdminRoles.RiskAnalyst,
            AdminRoles.SupportOperator,
            AdminRoles.InfrastructureOperator, AdminRoles.CompensationOperator,
            AdminRoles.AuditViewer);
        var actions = await store.ListAsync(Math.Clamp(limit, 1, 500), cancellationToken);
        if (principal.HasRole(AdminRoles.AuditViewer)) return actions;
        return actions.Where(action =>
                action.RequestedBy == principal.OperatorId
                || (action.TargetType == "Player"
                    && principal.HasRole(AdminRoles.PlayerApprover))
                || (action.TargetType != "Player"
                    && principal.HasRole(AdminRoles.RoomApprover))
                || principal.HasRole(RequiredOperatorRole(action.ActionType)))
            .ToArray();
    }

    /// <summary>要求审计查看角色后读取不可变审计链；limit 由 API 层限制为有界值。</summary>
    public Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        AdminPrincipal principal,
        int limit,
        CancellationToken cancellationToken)
    {
        RequireRole(principal, AdminRoles.AuditViewer);
        return store.ListAuditAsync(Math.Clamp(limit, 1, 1000), cancellationToken);
    }

    /// <summary>要求管理审计角色后读取命令 Outbox 观察视图，不领取或改变命令。</summary>
    public Task<IReadOnlyList<AdminCommandOutboxRecord>> ListOutboxAsync(
        AdminPrincipal principal,
        int limit,
        CancellationToken cancellationToken)
    {
        RequireRole(principal, AdminRoles.AuditViewer);
        return store.ListOutboxAsync(Math.Clamp(limit, 1, 500), cancellationToken);
    }

    private async Task<TargetSnapshot> LoadTargetAsync(
        AdminManagementActionType actionType,
        string targetId,
        CancellationToken cancellationToken)
    {
        if (IsPlayerAction(actionType))
        {
            // 管理决策必须重新读取权威实时状态，禁止复用只读页面展示的最后成功快照。
            var player = await playerMonitoring.GetPlayerForActionAsync(
                    targetId,
                    cancellationToken)
                ?? throw AdminOperationException.NotFound("玩家不存在。");
            var controlState = JsonSerializer.SerializeToElement(new
            {
                player.Summary.PlayerId,
                player.Summary.AccountStatus,
                player.Summary.Online,
                player.Summary.CurrentDeviceId,
                player.Summary.CurrentMaskedIp,
                player.Summary.LobbyId,
                player.Summary.RoomId,
                player.Summary.ServerInstanceId,
                player.Summary.ActiveSessionCount,
                player.Summary.ControlVersion,
                player.Summary.FrozenUntilUtc,
                player.Summary.MutedUntilUtc,
                player.Summary.RiskLabels,
                player.Sessions
            });
            var auditState = JsonSerializer.SerializeToElement(new
            {
                player.Summary,
                player.Sessions,
                player.KnownDeviceIds,
                player.RoomHistory,
                player.DisconnectHistory,
                player.DataScope
            });
            return new TargetSnapshot(
                "Player",
                null,
                auditState,
                ComputeStateHash(controlState));
        }

        if (actionType == AdminManagementActionType.TerminateAbnormalServer)
        {
            var instance = (await monitoring.ListInstancesForActionAsync(cancellationToken))
                .FirstOrDefault(item =>
                    item.Instance.ServerInstanceId.Equals(targetId, StringComparison.Ordinal));
            if (instance is null)
                throw AdminOperationException.NotFound("Dedicated Server 实例不存在。");
            var serverState = JsonSerializer.SerializeToElement(new
            {
                instance.ClusterId,
                instance.NodeId,
                instance.Instance.ServerInstanceId,
                instance.Instance.RoomId,
                instance.Instance.MatchId,
                instance.Instance.ProcessId,
                instance.Instance.State,
                instance.Instance.BuildVersion,
                instance.Instance.FailureReason
            });
            return new TargetSnapshot(
                "DedicatedServer",
                null,
                serverState,
                ComputeStateHash(serverState));
        }

        var room = await monitoring.GetRoomForActionAsync(targetId, cancellationToken)
            ?? throw AdminOperationException.NotFound("房间不存在。");
        var state = JsonSerializer.SerializeToElement(new
        {
            room.Summary,
            room.Rules,
            room.OwnerPlayerId,
            room.PlayerIds,
            room.PublicRoom,
            room.AutoStart,
            room.NewPlayersProhibited,
            room.MaintenanceMode,
            room.MarkedAbnormal,
            room.DedicatedServer,
            room.Runtime,
            room.Timeline,
            room.TelemetryStatus
        });
        return new TargetSnapshot(
            "Room",
            room.Summary.StateSequence,
            state,
            ComputeStateHash(state));
    }

    private static void EnsureExpectedState(
        long? expectedSequence,
        string? expectedHash,
        TargetSnapshot actual)
    {
        if (actual.StateSequence.HasValue
            && (!expectedSequence.HasValue
                || expectedSequence.Value != actual.StateSequence.Value))
        {
            throw AdminOperationException.Conflict(
                $"目标状态已变化，当前状态序号为 {actual.StateSequence.Value}，请重新检查后发起申请。");
        }
        if (!actual.StateSequence.HasValue
            && expectedHash is not null
            && !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedHash),
                Encoding.ASCII.GetBytes(actual.StateHash)))
        {
            throw AdminOperationException.Conflict(
                "目标账号、会话或服务状态已经变化，请重新检查后发起申请。");
        }
    }

    private static string RequiredOperatorRole(AdminManagementActionType actionType) =>
        actionType switch
        {
            AdminManagementActionType.TerminateAbnormalServer =>
                AdminRoles.InfrastructureOperator,
            AdminManagementActionType.TriggerCompensation or
            AdminManagementActionType.GrantPlayerCompensation or
            AdminManagementActionType.RevokeErroneousReward =>
                AdminRoles.CompensationOperator,
            AdminManagementActionType.OrderRefund =>
                AdminRoles.RefundOperator,
            AdminManagementActionType.RulePublish or
            AdminManagementActionType.ConfigurationPublish =>
                AdminRoles.GovernancePublisher,
            AdminManagementActionType.BatchSanction =>
                AdminRoles.BatchSanctionOperator,
            AdminManagementActionType.TemporaryFreezePlayer or
            AdminManagementActionType.PermanentBanPlayer or
            AdminManagementActionType.LiftPlayerBan or
            AdminManagementActionType.MutePlayer or
            AdminManagementActionType.UnmutePlayer =>
                AdminRoles.SanctionOperator,
            AdminManagementActionType.MarkRiskAccount =>
                AdminRoles.RiskAnalyst,
            AdminManagementActionType.ViewPlayerReplay or
            AdminManagementActionType.CreatePlayerSupportTicket =>
                AdminRoles.SupportOperator,
            AdminManagementActionType.ForceLogoutPlayer or
            AdminManagementActionType.ResetAbnormalPlayerSession =>
                AdminRoles.PlayerOperator,
            _ => AdminRoles.RoomOperator
        };

    private static string RequiredApproverRole(AdminManagementActionType actionType) =>
        actionType is AdminManagementActionType.OrderRefund
            or AdminManagementActionType.RulePublish
            or AdminManagementActionType.ConfigurationPublish
            or AdminManagementActionType.BatchSanction
            ? AdminRoles.GovernanceApprover
            : IsPlayerAction(actionType) ? AdminRoles.PlayerApprover : AdminRoles.RoomApprover;

    private static bool IsPlayerAction(AdminManagementActionType actionType) =>
        actionType is
            AdminManagementActionType.ForceLogoutPlayer or
            AdminManagementActionType.TemporaryFreezePlayer or
            AdminManagementActionType.PermanentBanPlayer or
            AdminManagementActionType.LiftPlayerBan or
            AdminManagementActionType.MutePlayer or
            AdminManagementActionType.UnmutePlayer or
            AdminManagementActionType.ResetAbnormalPlayerSession or
            AdminManagementActionType.MarkRiskAccount or
            AdminManagementActionType.GrantPlayerCompensation or
            AdminManagementActionType.RevokeErroneousReward or
            AdminManagementActionType.ViewPlayerReplay or
            AdminManagementActionType.CreatePlayerSupportTicket or
            AdminManagementActionType.BatchSanction;

    private static JsonElement? ValidateInput(CreateAdminActionRequest request)
    {
        if (!Enum.IsDefined(request.ActionType))
            throw AdminOperationException.Invalid("管理操作类型无效。");
        var targetId = request.TargetId?.Trim() ?? string.Empty;
        var reason = NormalizeText(request.Reason);
        var ticket = NormalizeText(request.TicketId);
        if (targetId.Length is < 3 or > 128 || !SafeIdentifierPattern().IsMatch(targetId))
            throw AdminOperationException.Invalid("目标 ID 格式无效。");
        if (reason.Length is < 10 or > 1000)
            throw AdminOperationException.Invalid("操作原因长度必须为 10 到 1000 个字符。");
        if (ticket.Length is < 3 or > 128 || !SafeIdentifierPattern().IsMatch(ticket))
            throw AdminOperationException.Invalid("关联工单格式无效。");
        return request.ActionType switch
        {
            AdminManagementActionType.GrantPlayerCompensation =>
                NormalizeGrantParameters(request.Parameters),
            AdminManagementActionType.RevokeErroneousReward =>
                NormalizeRevokeParameters(request.Parameters),
            AdminManagementActionType.LiftPlayerBan or
            AdminManagementActionType.UnmutePlayer =>
                NormalizeSanctionReversalParameters(request.Parameters),
            // 仓库当前没有订单、规则发布、配置发布或批量处罚的权威业务所有者；
            // 在对应管理命令 API 建立前必须失败关闭，避免 Admin 自行写表或伪造成功。
            AdminManagementActionType.OrderRefund or
            AdminManagementActionType.RulePublish or
            AdminManagementActionType.ConfigurationPublish or
            AdminManagementActionType.BatchSanction =>
                throw new AdminOperationException(
                    "ADMIN_OWNER_CAPABILITY_UNAVAILABLE",
                    "目标业务所有者尚未提供受控管理命令，本操作当前保持关闭。",
                    StatusCodes.Status503ServiceUnavailable),
            _ => RejectUnexpectedParameters(request.Parameters)
        };
    }

    /// <summary>规范化结构化原因码；旧客户端缺失时使用显式兼容值而非空字符串。</summary>
    private static string NormalizeReasonCode(string? value)
    {
        var normalized = NormalizeText(value);
        if (normalized.Length == 0) return "LEGACY_UNSPECIFIED";
        if (normalized.Length > 64 || !SafeIdentifierPattern().IsMatch(normalized))
            throw AdminOperationException.Invalid("reasonCode 格式无效。");
        return normalized.ToUpperInvariant();
    }

    /// <summary>规范化操作说明；旧客户端缺失时复制已验证的原因文本以保持审计可读性。</summary>
    private static string NormalizeOperationDescription(string? description, string reason)
    {
        var normalized = NormalizeText(description);
        if (normalized.Length == 0) return NormalizeText(reason);
        if (normalized.Length is < 10 or > 1000)
            throw AdminOperationException.Invalid("operationDescription 长度必须为 10 到 1000 个字符。");
        return normalized;
    }

    /// <summary>持久化调用方幂等键用于审计；兼容入口未提供时保存 null，不生成可误认的值。</summary>
    private static string? NormalizeIdempotencyKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static JsonElement NormalizeGrantParameters(JsonElement? parameters)
    {
        var value = RequireParameterObject(parameters);
        var caseId = RequireCaseId(value);
        var assetCode = RequireSafeParameter(value, "assetCode", 2, 32)
            .ToUpperInvariant();
        if (!value.TryGetProperty("amount", out var amountElement)
            || !amountElement.TryGetInt64(out var amount)
            || amount is < 1 or > 1_000_000_000)
        {
            throw AdminOperationException.Invalid(
                "补偿数量必须是 1 到 1000000000 之间的整数。");
        }
        return JsonSerializer.SerializeToElement(new
        {
            caseId,
            assetCode,
            amount
        });
    }

    private static JsonElement NormalizeRevokeParameters(JsonElement? parameters)
    {
        var value = RequireParameterObject(parameters);
        return JsonSerializer.SerializeToElement(new
        {
            caseId = RequireCaseId(value),
            rewardGrantId = RequireSafeParameter(
                value,
                "rewardGrantId",
                3,
                128)
        });
    }

    private static JsonElement NormalizeSanctionReversalParameters(
        JsonElement? parameters)
    {
        var value = RequireParameterObject(parameters);
        return JsonSerializer.SerializeToElement(new
        {
            originalCommandId = RequireSafeParameter(
                value,
                "originalCommandId",
                16,
                128)
        });
    }

    private static JsonElement RequireParameterObject(JsonElement? parameters)
    {
        if (!parameters.HasValue
            || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            throw AdminOperationException.Invalid(
                "资产操作必须提供结构化参数。");
        }
        return parameters.Value;
    }

    private static string RequireCaseId(JsonElement parameters)
    {
        var caseId = RequireSafeParameter(parameters, "caseId", 36, 36);
        if (!Guid.TryParse(caseId, out var parsed))
            throw AdminOperationException.Invalid("补偿案件 ID 格式无效。");
        return parsed.ToString();
    }

    private static string RequireSafeParameter(
        JsonElement parameters,
        string propertyName,
        int minimumLength,
        int maximumLength)
    {
        if (!parameters.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            throw AdminOperationException.Invalid(
                $"资产操作参数 {propertyName} 缺失。");
        }
        var value = NormalizeText(element.GetString());
        if (value.Length < minimumLength
            || value.Length > maximumLength
            || !SafeIdentifierPattern().IsMatch(value))
        {
            throw AdminOperationException.Invalid(
                $"资产操作参数 {propertyName} 格式无效。");
        }
        return value;
    }

    private static JsonElement? RejectUnexpectedParameters(
        JsonElement? parameters)
    {
        if (parameters.HasValue
            && parameters.Value.ValueKind is not (
                JsonValueKind.Null or JsonValueKind.Undefined))
        {
            throw AdminOperationException.Invalid(
                "当前管理操作不接受附加参数。");
        }
        return null;
    }

    private async Task EnsureAssetCaseAsync(
        AdminManagementActionType actionType,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (actionType is not (
                AdminManagementActionType.GrantPlayerCompensation
                or AdminManagementActionType.RevokeErroneousReward))
        {
            return;
        }
        var caseId = parameters!.Value.GetProperty("caseId").GetString()!;
        var compensationCase = await caseStore.GetAsync(
            caseId,
            cancellationToken);
        if (compensationCase is null)
            throw AdminOperationException.NotFound("补偿审查案件不存在。");
        if (compensationCase.CaseType != AdminCaseType.CompensationReview
            || compensationCase.Status != "Open")
        {
            throw AdminOperationException.Conflict(
                "资产操作只能关联处于 Open 状态的补偿审查案件。");
        }
    }

    private async Task EnsureSanctionReferenceAsync(
        AdminManagementActionType actionType,
        string playerId,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (actionType is not (
                AdminManagementActionType.LiftPlayerBan
                or AdminManagementActionType.UnmutePlayer))
        {
            return;
        }
        var originalCommandId = parameters!.Value
            .GetProperty("originalCommandId")
            .GetString()!;
        var player = await playerMonitoring.GetPlayerForActionAsync(
            playerId,
            cancellationToken)
            ?? throw AdminOperationException.NotFound(
                "Player was not found.");
        var original = player.ControlHistory.SingleOrDefault(
            item => item.CommandId == originalCommandId);
        var expectedOriginalType = actionType ==
            AdminManagementActionType.LiftPlayerBan
                ? AdminManagementActionType.PermanentBanPlayer.ToString()
                : AdminManagementActionType.MutePlayer.ToString();
        if (original is null
            || original.ActionType != expectedOriginalType)
        {
            throw AdminOperationException.Conflict(
                "The referenced original sanction does not exist or has the wrong type.");
        }
    }

    private static string NormalizeText(string? value) =>
        new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .ToArray())
            .Trim();

    private static string NormalizeTraceId(string? traceId)
    {
        var value = NormalizeText(traceId);
        return value.Length is >= 8 and <= 64 && SafeIdentifierPattern().IsMatch(value)
            ? value
            : Guid.NewGuid().ToString();
    }

    private static string ComputeStateHash(JsonElement state) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(state.GetRawText())));

    private void EnsureEnabled()
    {
        if (!management.Enabled)
            throw AdminOperationException.Forbidden("管理操作工作流当前未启用。");
    }

    private static void RequireRole(AdminPrincipal principal, string role)
    {
        if (!principal.HasRole(role))
            throw AdminOperationException.Forbidden($"缺少角色：{role}");
    }

    private static void EnsureAnyRole(AdminPrincipal principal, params string[] roles)
    {
        if (!roles.Any(principal.HasRole))
            throw AdminOperationException.Forbidden("没有查看管理申请的权限。");
    }

    private async Task<AdminActionRecord> RequireActionAsync(
        string actionRequestId,
        CancellationToken cancellationToken) =>
        await store.GetAsync(actionRequestId, cancellationToken)
        ?? throw AdminOperationException.NotFound("管理申请不存在。");

    private async Task TransitionAsync(
        AdminActionRecord current,
        AdminActionRecord replacement,
        AdminAuditDraft audit,
        CancellationToken cancellationToken)
    {
        if (!await store.TryTransitionAsync(
                current.Version, replacement, audit, cancellationToken))
        {
            throw AdminOperationException.Conflict("管理申请已被其他操作更新，请刷新后重试。");
        }
    }

    private async Task<AdminActionRecord> ExpireAsync(
        AdminActionRecord action,
        string operatorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var replacement = action with
        {
            Status = AdminActionStatus.Expired,
            Version = action.Version + 1
        };
        await TransitionAsync(
            action,
            replacement,
            Audit(now, operatorId, "ActionExpired", action,
                JsonSerializer.SerializeToElement(action),
                JsonSerializer.SerializeToElement(replacement),
                null),
            cancellationToken);
        return replacement;
    }

    private static AdminAuditDraft Audit(
        DateTimeOffset now,
        string operatorId,
        string operation,
        AdminActionRecord action,
        JsonElement? before,
        JsonElement? after,
        JsonElement? approval) =>
        new(
            now,
            operatorId,
            operation,
            action.TargetType,
            action.TargetId,
            action.Reason,
            before,
            after,
            approval,
            action.TraceId,
            action.TicketId);

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();

    private sealed record TargetSnapshot(
        string TargetType,
        long? StateSequence,
        JsonElement State,
        string StateHash);
}

/// <summary>
/// 可安全映射为 Admin API 错误响应的领域异常。
/// 消息用于授权用户界面，不能包含服务凭据、连接串或内部堆栈。
/// </summary>
public sealed class AdminOperationException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    /// <summary>稳定机器错误码，供 Angular 页面按类型显示和降级。</summary>
    public string Code { get; } = code;

    /// <summary>由统一异常边界采用的 HTTP 状态码。</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>创建输入格式、范围或组合不合法的 400 错误。</summary>
    public static AdminOperationException Invalid(string message) =>
        new("ADMIN_INVALID_REQUEST", message, StatusCodes.Status400BadRequest);

    /// <summary>创建当前身份/角色/属性策略不允许操作的 403 错误。</summary>
    public static AdminOperationException Forbidden(string message) =>
        new("ADMIN_FORBIDDEN", message, StatusCodes.Status403Forbidden);

    /// <summary>创建目标或关联调查实体不存在的 404 错误。</summary>
    public static AdminOperationException NotFound(string message) =>
        new("ADMIN_NOT_FOUND", message, StatusCodes.Status404NotFound);

    /// <summary>创建幂等键复用、版本或状态前置条件冲突的 409 错误。</summary>
    public static AdminOperationException Conflict(string message) =>
        new("ADMIN_STATE_CONFLICT", message, StatusCodes.Status409Conflict);
}
