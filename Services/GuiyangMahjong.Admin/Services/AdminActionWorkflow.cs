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
    MonitoringAggregationService monitoring,
    PlayerMonitoringService playerMonitoring,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider)
{
    private readonly AdminManagementOptions management = options.Value.Management;

    public async Task<AdminActionRecord> CreateAsync(
        AdminPrincipal principal,
        CreateAdminActionRequest request,
        string traceId,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        RequireRole(principal, IsPlayerAction(request.ActionType)
            ? AdminRoles.PlayerViewer
            : AdminRoles.RoomViewer);
        RequireRole(principal, RequiredOperatorRole(request.ActionType));
        ValidateInput(request);
        var target = await LoadTargetAsync(
            request.ActionType, request.TargetId, cancellationToken);
        EnsureExpectedState(
            request.ExpectedStateSequence, null, target);
        var now = timeProvider.GetUtcNow();
        var action = new AdminActionRecord(
            Guid.NewGuid().ToString(),
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
            1);
        await store.CreateAsync(
            action,
            Audit(
                now, principal.OperatorId, "ActionRequested", action,
                null, JsonSerializer.SerializeToElement(action), null),
            cancellationToken);
        return action;
    }

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
    {
        EnsureEnabled();
        var action = await RequireActionAsync(actionRequestId, cancellationToken);
        RequireRole(principal, RequiredApproverRole(action.ActionType));
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
                action.TargetType == "Player"
                    ? principal.HasRole(AdminRoles.PlayerViewer)
                    : principal.HasRole(AdminRoles.RoomViewer))
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
            var player = await playerMonitoring.GetPlayerAsync(targetId, cancellationToken)
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
            var instance = (await monitoring.ListInstancesAsync(cancellationToken))
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

        var room = await monitoring.GetRoomAsync(targetId, cancellationToken)
            ?? throw AdminOperationException.NotFound("房间不存在。");
        var state = JsonSerializer.SerializeToElement(new
        {
            room.Summary,
            room.Rules,
            room.OwnerPlayerId,
            room.PlayerIds,
            room.PublicRoom,
            room.AutoStart,
            room.DedicatedServer,
            room.Runtime,
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

    private static void ValidateInput(CreateAdminActionRequest request)
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
