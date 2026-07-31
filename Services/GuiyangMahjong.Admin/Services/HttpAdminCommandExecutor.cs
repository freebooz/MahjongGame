// Admin 命令执行器：把已审批操作转换为对 Auth、Lobby、Allocator 或 Economy 的受控 HTTP 调用。
// 所有请求必须携带幂等键、短期服务凭据和 TraceId；超时或不确定结果不得伪装为成功。
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// 已审批管理命令的 HTTP 执行适配器。
/// 只接受工作流生成的 Outbox 记录并按动作白名单路由到下游服务，
/// 所有调用携带幂等键、TraceId 和最小权限服务凭据；资产操作还需先固化案件证据。
/// </summary>
public sealed class HttpAdminCommandExecutor(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options,
    IAdminCaseStore caseStore,
    IPlayerAssetOperationStore assetOperationStore) : IAdminCommandExecutor
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly AdminOptions admin = options.Value;

    /// <summary>
    /// 执行一个已领取命令并分类返回成功、可重试失败或永久失败。
    /// 方法不直接迁移 Admin 动作状态；调用方负责根据结果原子完成 Outbox 与审计。
    /// </summary>
    public async Task<AdminCommandExecutionResult> ExecuteAsync(
        AdminCommandOutboxRecord command,
        CancellationToken cancellationToken)
    {
        var action = command.Payload.Deserialize<AdminActionRecord>(JsonOptions);
        if (action is null)
            return Failure(false, "InvalidCommandPayload", "Action payload is invalid.");
        if (command.ActionType is
            AdminManagementActionType.MarkRoomAbnormal
            or AdminManagementActionType.ProhibitNewPlayers
            or AdminManagementActionType.EnableMaintenanceMode
            or AdminManagementActionType.ForceDissolveRoom)
        {
            return await ExecuteRoomControlAsync(
                command,
                action,
                cancellationToken);
        }
        if (command.ActionType ==
            AdminManagementActionType.TerminateAbnormalServer)
        {
            return await ExecuteInstanceTerminationAsync(
                command,
                action,
                cancellationToken);
        }
        if (IsPlayerControlAction(command.ActionType))
        {
            return await ExecutePlayerControlAsync(
                command,
                action,
                cancellationToken);
        }
        if (TryGetCaseType(command.ActionType, out var caseType))
        {
            return await ExecuteCaseCreationAsync(
                command,
                action,
                caseType,
                cancellationToken);
        }
        if (TryGetAssetOperationType(
                command.ActionType,
                out var assetOperationType))
        {
            return await ExecuteAssetOperationAsync(
                command,
                action,
                assetOperationType,
                cancellationToken);
        }
        if (command.ActionType is not (
            AdminManagementActionType.ForceLogoutPlayer
            or AdminManagementActionType.ResetAbnormalPlayerSession))
        {
            return Failure(
                false,
                "AdapterNotConfigured",
                $"No command adapter is configured for {command.ActionType}.");
        }

        var body = new
        {
            action.Reason,
            action.TraceId,
            EffectiveAtUtc = command.CreatedAtUtc
        };
        var auth = await SendAsync(
            admin.Auth.BaseUrl,
            $"/internal/admin/players/{Uri.EscapeDataString(command.TargetId)}/sessions/revoke",
            admin.Management.AuthCommandToken,
            command.OutboxId,
            command.TraceId,
            body,
            cancellationToken);
        if (!auth.Succeeded)
            return Failure(auth.Retryable, "AuthCommandFailed", auth.Error, auth.Body);

        var lobby = await SendAsync(
            admin.Lobby.BaseUrl,
            $"/internal/admin/players/{Uri.EscapeDataString(command.TargetId)}/disconnect",
            admin.Management.LobbyCommandToken,
            command.OutboxId,
            command.TraceId,
            body,
            cancellationToken);
        if (!lobby.Succeeded)
        {
            return new AdminCommandExecutionResult(
                false,
                lobby.Retryable,
                JsonSerializer.SerializeToElement(new
                {
                    status = "LobbyCommandFailed",
                    auth = auth.Body,
                    lobby = lobby.Body
                }, JsonOptions),
                lobby.Error);
        }

        return new AdminCommandExecutionResult(
            true,
            false,
            JsonSerializer.SerializeToElement(new
            {
                status = "PlayerSessionTerminated",
                auth = auth.Body,
                lobby = lobby.Body
            }, JsonOptions),
            null);
    }

    private async Task<AdminCommandExecutionResult> ExecuteRoomControlAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord action,
        CancellationToken cancellationToken)
    {
        if (!action.ExpectedStateSequence.HasValue)
        {
            return Failure(
                false,
                "MissingExpectedStateSequence",
                "Room command has no expected state sequence.");
        }
        var result = await SendAsync(
            admin.Lobby.BaseUrl,
            $"/internal/admin/rooms/{Uri.EscapeDataString(command.TargetId)}/controls",
            admin.Management.LobbyCommandToken,
            command.OutboxId,
            command.TraceId,
            new
            {
                ActionType = command.ActionType.ToString(),
                ExpectedStateSequence = action.ExpectedStateSequence.Value,
                action.Reason,
                action.TraceId
            },
            cancellationToken);
        return result.Succeeded
            ? new AdminCommandExecutionResult(true, false, result.Body, null)
            : Failure(
                result.Retryable,
                "LobbyRoomCommandFailed",
                result.Error,
                result.Body);
    }

    private async Task<AdminCommandExecutionResult> ExecuteCaseCreationAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord action,
        AdminCaseType caseType,
        CancellationToken cancellationToken)
    {
        if (action.Approval is null)
        {
            return Failure(
                false,
                "MissingCaseApproval",
                "An approved action is required to create a management case.");
        }
        try
        {
            var result = await caseStore.CreateAsync(
                command.OutboxId,
                caseType,
                action,
                action.Approval.ApprovedAtUtc,
                cancellationToken);
            return new AdminCommandExecutionResult(
                true,
                false,
                JsonSerializer.SerializeToElement(result, JsonOptions),
                null);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(
                false,
                "CaseCommandConflict",
                exception.Message);
        }
    }

    private async Task<AdminCommandExecutionResult> ExecuteAssetOperationAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord action,
        PlayerAssetOperationType operationType,
        CancellationToken cancellationToken)
    {
        if (action.Approval is null)
        {
            return Failure(
                false,
                "MissingAssetOperationApproval",
                "An approved action is required to queue an asset operation.");
        }
        if (!action.Parameters.HasValue
            || action.Parameters.Value.ValueKind != JsonValueKind.Object
            || !action.Parameters.Value.TryGetProperty(
                "caseId",
                out var caseIdElement)
            || caseIdElement.ValueKind != JsonValueKind.String)
        {
            return Failure(
                false,
                "MissingCompensationCase",
                "An approved compensation review case id is required.");
        }
        var compensationCase = await caseStore.GetAsync(
            caseIdElement.GetString()!,
            cancellationToken);
        if (compensationCase is null)
        {
            return Failure(
                false,
                "CompensationCaseNotFound",
                "The referenced compensation review case does not exist.");
        }
        try
        {
            var result = await assetOperationStore.CreateAsync(
                command.OutboxId,
                operationType,
                action,
                compensationCase,
                action.Approval.ApprovedAtUtc,
                cancellationToken);
            if (result.Operation.Status == "WalletCompleted")
            {
                return new AdminCommandExecutionResult(
                    true,
                    false,
                    JsonSerializer.SerializeToElement(result, JsonOptions),
                    null);
            }
            if (result.Operation.Status == "WalletRejected")
            {
                return Failure(
                    false,
                    "WalletOperationRejected",
                    "The wallet previously rejected this operation.");
            }
            if (!admin.Wallet.Enabled)
            {
                return Failure(
                    false,
                    "WalletAdapterDisabled",
                    "The authoritative wallet adapter is disabled.");
            }
            var wallet = await SendAsync(
                admin.Wallet.BaseUrl,
                "/internal/admin/wallet-operations",
                admin.Wallet.CommandToken,
                command.OutboxId,
                command.TraceId,
                new
                {
                    OperationType = operationType.ToString(),
                    PlayerId = result.Operation.PlayerId,
                    CaseId = result.Operation.CaseId,
                    result.Operation.AssetCode,
                    result.Operation.Amount,
                    result.Operation.RewardGrantId,
                    result.Operation.RequestedBy,
                    result.Operation.ApprovedBy,
                    result.Operation.Reason,
                    result.Operation.TicketId,
                    result.Operation.TraceId,
                    ApprovedAtUtc = action.Approval.ApprovedAtUtc
                },
                cancellationToken);
            if (!wallet.Succeeded)
            {
                if (!wallet.Retryable)
                {
                    _ = await assetOperationStore.SetStatusAsync(
                        command.OutboxId,
                        "WalletRejected",
                        cancellationToken);
                }
                return Failure(
                    wallet.Retryable,
                    "WalletCommandFailed",
                    wallet.Error,
                    wallet.Body);
            }
            var completed =
                await assetOperationStore.SetStatusAsync(
                    command.OutboxId,
                    "WalletCompleted",
                    cancellationToken);
            return new AdminCommandExecutionResult(
                true,
                false,
                JsonSerializer.SerializeToElement(new
                {
                    operation = completed,
                    wallet = wallet.Body
                }, JsonOptions),
                null);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(
                false,
                "AssetOperationConflict",
                exception.Message);
        }
    }

    private async Task<AdminCommandExecutionResult> ExecutePlayerControlAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord action,
        CancellationToken cancellationToken)
    {
        var expectedVersion = GetInt64(action.BeforeState, "controlVersion");
        var approval = action.Approval;
        if (!expectedVersion.HasValue || approval is null)
        {
            return Failure(
                false,
                "InvalidPlayerControlSnapshot",
                "Player control command is missing its state version or approval.");
        }
        var effectiveAtUtc = approval.ApprovedAtUtc;
        var expiresAtUtc = command.ActionType switch
        {
            AdminManagementActionType.TemporaryFreezePlayer =>
                effectiveAtUtc.AddHours(
                    admin.Management.TemporaryFreezeHours),
            AdminManagementActionType.MutePlayer =>
                effectiveAtUtc.AddHours(admin.Management.MuteHours),
            AdminManagementActionType.MarkRiskAccount =>
                effectiveAtUtc.AddDays(admin.Management.RiskLabelTtlDays),
            _ => (DateTimeOffset?)null
        };
        var body = new
        {
            ActionType = command.ActionType.ToString(),
            ExpectedVersion = expectedVersion.Value,
            action.Reason,
            action.TraceId,
            action.TicketId,
            action.RequestedBy,
            approval.ApprovedBy,
            EffectiveAtUtc = effectiveAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            OriginalCommandId = GetString(
                action.Parameters,
                "originalCommandId"),
            RiskLabel = command.ActionType ==
                AdminManagementActionType.MarkRiskAccount
                    ? "manual-review"
                    : null
        };
        var auth = await SendAsync(
            admin.Auth.BaseUrl,
            $"/internal/admin/players/{Uri.EscapeDataString(command.TargetId)}/controls",
            admin.Management.AuthCommandToken,
            command.OutboxId,
            command.TraceId,
            body,
            cancellationToken);
        if (!auth.Succeeded)
            return Failure(
                auth.Retryable,
                "AuthPlayerControlFailed",
                auth.Error,
                auth.Body);

        if (command.ActionType is not (
            AdminManagementActionType.TemporaryFreezePlayer
            or AdminManagementActionType.PermanentBanPlayer))
        {
            return new AdminCommandExecutionResult(
                true,
                false,
                auth.Body,
                null);
        }
        var lobby = await SendAsync(
            admin.Lobby.BaseUrl,
            $"/internal/admin/players/{Uri.EscapeDataString(command.TargetId)}/disconnect",
            admin.Management.LobbyCommandToken,
            command.OutboxId,
            command.TraceId,
            new
            {
                action.Reason,
                action.TraceId,
                EffectiveAtUtc = effectiveAtUtc
            },
            cancellationToken);
        if (!lobby.Succeeded)
        {
            return new AdminCommandExecutionResult(
                false,
                lobby.Retryable,
                JsonSerializer.SerializeToElement(new
                {
                    status = "LobbyDisconnectFailed",
                    auth = auth.Body,
                    lobby = lobby.Body
                }, JsonOptions),
                lobby.Error);
        }
        return new AdminCommandExecutionResult(
            true,
            false,
            JsonSerializer.SerializeToElement(new
            {
                status = "PlayerRestrictedAndDisconnected",
                auth = auth.Body,
                lobby = lobby.Body
            }, JsonOptions),
            null);
    }

    private async Task<AdminCommandExecutionResult> ExecuteInstanceTerminationAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord action,
        CancellationToken cancellationToken)
    {
        var before = action.BeforeState;
        var expectedState = GetString(before, "state");
        var clusterId = GetString(before, "clusterId");
        var nodeId = GetString(before, "nodeId");
        if (expectedState is null || clusterId is null || nodeId is null)
        {
            return Failure(
                false,
                "InvalidInstanceSnapshot",
                "Instance command snapshot is missing state or location.");
        }
        var sources = admin.Allocators.Where(candidate =>
                candidate.Enabled
                && candidate.ClusterId == clusterId
                && candidate.NodeId == nodeId)
            .ToArray();
        if (sources.Length != 1)
        {
            return Failure(
                false,
                "AllocatorSourceNotFound",
                $"Exactly one allocator source is required for {clusterId}/{nodeId}.");
        }
        var source = sources[0];
        var result = await SendAsync(
            source.BaseUrl,
            $"/internal/admin/instances/{Uri.EscapeDataString(command.TargetId)}/terminate",
            source.ManagementCommandToken,
            command.OutboxId,
            command.TraceId,
            new
            {
                ExpectedState = expectedState,
                action.Reason,
                action.TraceId
            },
            cancellationToken);
        return result.Succeeded
            ? new AdminCommandExecutionResult(true, false, result.Body, null)
            : Failure(
                result.Retryable,
                "AllocatorCommandFailed",
                result.Error,
                result.Body);
    }

    private async Task<CommandCallResult> SendAsync(
        string baseUrl,
        string path,
        string token,
        string idempotencyKey,
        string traceId,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Add("X-Trace-Id", traceId);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            admin.Management.CommandTimeoutSeconds));
        try
        {
            using var response = await httpClientFactory
                .CreateClient(nameof(HttpAdminCommandExecutor))
                .SendAsync(request, timeout.Token);
            var responseBody = await ReadBodyAsync(response, timeout.Token);
            if (response.IsSuccessStatusCode)
                return new CommandCallResult(true, false, responseBody, null);
            var retryable = response.StatusCode is
                HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout
                || (int)response.StatusCode >= 500;
            return new CommandCallResult(
                false,
                retryable,
                responseBody,
                $"Command endpoint returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommandCallResult(
                false,
                true,
                JsonSerializer.SerializeToElement(
                    new { status = "Timeout" }, JsonOptions),
                "Command endpoint timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new CommandCallResult(
                false,
                true,
                JsonSerializer.SerializeToElement(
                    new { status = "TransportFailure" }, JsonOptions),
                exception.Message);
        }
    }

    private static async Task<JsonElement> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
            return JsonSerializer.SerializeToElement(new
            {
                statusCode = (int)response.StatusCode
            }, JsonOptions);
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new
            {
                statusCode = (int)response.StatusCode,
                body = "Non-JSON response"
            }, JsonOptions);
        }
    }

    private static AdminCommandExecutionResult Failure(
        bool retryable,
        string status,
        string? error,
        JsonElement? body = null) =>
        new(
            false,
            retryable,
            JsonSerializer.SerializeToElement(new
            {
                status,
                response = body
            }, JsonOptions),
            error);

    private static string? GetString(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    private static string? GetString(JsonElement? value, string name) =>
        value.HasValue ? GetString(value.Value, name) : null;

    private static long? GetInt64(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.TryGetInt64(out var result))
                return result;
        return null;
    }

    private static bool IsPlayerControlAction(
        AdminManagementActionType actionType) =>
        actionType is
            AdminManagementActionType.TemporaryFreezePlayer
            or AdminManagementActionType.PermanentBanPlayer
            or AdminManagementActionType.LiftPlayerBan
            or AdminManagementActionType.MutePlayer
            or AdminManagementActionType.UnmutePlayer
            or AdminManagementActionType.MarkRiskAccount;

    private static bool TryGetCaseType(
        AdminManagementActionType actionType,
        out AdminCaseType caseType)
    {
        caseType = actionType switch
        {
            AdminManagementActionType.StartDisputeInvestigation =>
                AdminCaseType.DisputeInvestigation,
            AdminManagementActionType.CreatePlayerSupportTicket =>
                AdminCaseType.PlayerSupport,
            AdminManagementActionType.TriggerCompensation =>
                AdminCaseType.CompensationReview,
            AdminManagementActionType.ExportRoomLogs =>
                AdminCaseType.RoomLogExport,
            AdminManagementActionType.ViewReplay =>
                AdminCaseType.ReplayReview,
            AdminManagementActionType.ViewPlayerReplay =>
                AdminCaseType.ReplayReview,
            _ => default
        };
        return actionType is
            AdminManagementActionType.StartDisputeInvestigation
            or AdminManagementActionType.CreatePlayerSupportTicket
            or AdminManagementActionType.TriggerCompensation
            or AdminManagementActionType.ExportRoomLogs
            or AdminManagementActionType.ViewReplay
            or AdminManagementActionType.ViewPlayerReplay;
    }

    private static bool TryGetAssetOperationType(
        AdminManagementActionType actionType,
        out PlayerAssetOperationType operationType)
    {
        operationType = actionType switch
        {
            AdminManagementActionType.GrantPlayerCompensation =>
                PlayerAssetOperationType.GrantCompensation,
            AdminManagementActionType.RevokeErroneousReward =>
                PlayerAssetOperationType.RevokeReward,
            _ => default
        };
        return actionType is
            AdminManagementActionType.GrantPlayerCompensation
            or AdminManagementActionType.RevokeErroneousReward;
    }

    private sealed record CommandCallResult(
        bool Succeeded,
        bool Retryable,
        JsonElement Body,
        string? Error);
}
