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

public sealed partial class AdminActionWorkflow(
    IAdminActionStore store,
    IAdminCaseStore caseStore,
    MonitoringAggregationService monitoring,
    PlayerMonitoringService playerMonitoring,
    AdminAbacPolicyService abacPolicy,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider)
{
    private readonly AdminManagementOptions management = options.Value.Management;

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
            parameters);
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
        if (!string.Equals(
                request.TargetConfirmation?.Trim(),
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

    public Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        AdminPrincipal principal,
        int limit,
        CancellationToken cancellationToken)
    {
        RequireRole(principal, AdminRoles.AuditViewer);
        return store.ListAuditAsync(Math.Clamp(limit, 1, 1000), cancellationToken);
    }

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
        IsPlayerAction(actionType) ? AdminRoles.PlayerApprover : AdminRoles.RoomApprover;

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
            AdminManagementActionType.CreatePlayerSupportTicket;

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
            _ => RejectUnexpectedParameters(request.Parameters)
        };
    }

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

public sealed class AdminOperationException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static AdminOperationException Invalid(string message) =>
        new("ADMIN_INVALID_REQUEST", message, StatusCodes.Status400BadRequest);
    public static AdminOperationException Forbidden(string message) =>
        new("ADMIN_FORBIDDEN", message, StatusCodes.Status403Forbidden);
    public static AdminOperationException NotFound(string message) =>
        new("ADMIN_NOT_FOUND", message, StatusCodes.Status404NotFound);
    public static AdminOperationException Conflict(string message) =>
        new("ADMIN_STATE_CONFLICT", message, StatusCodes.Status409Conflict);
}
