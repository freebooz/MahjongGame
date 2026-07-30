using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 承载玩家实时摘要和调查历史查询；敏感历史必须同时满足调查角色、工单和审计约束。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 注册玩家查询端点；默认返回脱敏摘要，只有合法调查上下文才展开历史数据。
    /// </summary>
    private static void MapPlayerEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/players", async (
            string? search,
            string? cursor,
            int? pageSize,
            HttpContext context,
            PlayerMonitoringService monitoring,
            IOptions<AdminOptions> options,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.PlayerViewer);
            return Results.Ok(await monitoring.ListPlayersAsync(
                search,
                cursor,
                pageSize ?? options.Value.RealtimeCapacity.DefaultPageSize,
                cancellationToken));
        });
        api.MapGet("/players/{playerId}", async (
            string playerId,
            string? ticketId,
            HttpContext context,
            PlayerMonitoringService monitoring,
            IAdminActionStore auditStore,
            IAdminCaseStore caseStore,
            AdminAbacPolicyService abacPolicy,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.PlayerViewer);
            var player = await monitoring.GetPlayerAsync(playerId, cancellationToken);
            if (player is null) return Results.NotFound();
            var principal = AdminPrincipalContext.Get(context);
            var hasIdentityHistoryRole =
                principal.HasRole(AdminRoles.PlayerOperator)
                || principal.HasRole(AdminRoles.SanctionOperator)
                || principal.HasRole(AdminRoles.RiskAnalyst)
                || principal.HasRole(AdminRoles.SupportOperator)
                || principal.HasRole(AdminRoles.PlayerApprover)
                || principal.HasRole(AdminRoles.AuditViewer);
            var hasControlHistoryRole =
                principal.HasRole(AdminRoles.SanctionOperator)
                || principal.HasRole(AdminRoles.RiskAnalyst)
                || principal.HasRole(AdminRoles.PlayerApprover)
                || principal.HasRole(AdminRoles.AuditViewer);
            var normalizedTicket = ticketId?.Trim() ?? string.Empty;
            if (normalizedTicket.Length > 0
                && !IsSafeIdentifier(normalizedTicket))
            {
                throw AdminOperationException.Invalid(
                    "ticketId contains invalid characters or length.");
            }
            var canViewIdentityHistory =
                hasIdentityHistoryRole && normalizedTicket.Length > 0;
            var canViewControlHistory =
                hasControlHistoryRole && normalizedTicket.Length > 0;
            if (canViewIdentityHistory || canViewControlHistory)
            {
                await RequireInvestigationHistoryAccessAsync(
                    context,
                    playerId,
                    normalizedTicket,
                    caseStore,
                    abacPolicy,
                    cancellationToken);
                await auditStore.AppendAuditAsync(
                    new AdminAuditDraft(
                        timeProvider.GetUtcNow(),
                        principal.OperatorId,
                        "SensitivePlayerIdentityHistoryViewed",
                        "Player",
                        playerId,
                        "Authorized player identity and control history read.",
                        null,
                        System.Text.Json.JsonSerializer.SerializeToElement(new
                        {
                            identityHistory = canViewIdentityHistory,
                            controlHistory = canViewControlHistory,
                            sessionCount = canViewIdentityHistory
                                ? player.Sessions.Length
                                : 0,
                            loginEventCount = canViewIdentityHistory
                                ? player.LoginHistory.Length
                                : 0,
                            controlEventCount = canViewControlHistory
                                ? player.ControlHistory.Length
                                : 0
                        }),
                        null,
                        GetTraceId(context),
                        normalizedTicket),
                    cancellationToken);
            }
            return Results.Ok(player with
            {
                Sessions = canViewIdentityHistory ? player.Sessions : [],
                LoginHistory =
                    canViewIdentityHistory ? player.LoginHistory : [],
                KnownDeviceIds =
                    canViewIdentityHistory ? player.KnownDeviceIds : [],
                RoomHistory =
                    canViewIdentityHistory ? player.RoomHistory : [],
                DisconnectHistory =
                    canViewIdentityHistory ? player.DisconnectHistory : [],
                ControlHistory =
                    canViewControlHistory ? player.ControlHistory : [],
                DataScope = canViewIdentityHistory
                    ? canViewControlHistory
                        ? player.DataScope
                        : "MaskedIdentityHistoryControlHistoryRedacted"
                    : "ReadOnlyMaskedIdentityAndControlHistoryRedacted"
            });
        });
        api.MapGet("/players/{playerId}/room-history", async (
            string playerId,
            string ticketId,
            int? pageSize,
            DateTimeOffset? beforeAtUtc,
            string? beforeRoomId,
            HttpContext context,
            ILobbyMonitoringClient lobby,
            IAdminActionStore auditStore,
            IAdminCaseStore caseStore,
            AdminAbacPolicyService abacPolicy,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            await RequireInvestigationHistoryAccessAsync(
                context,
                playerId,
                ticketId,
                caseStore,
                abacPolicy,
                cancellationToken);
            var page = await lobby.ListPlayerRoomHistoryAsync(
                playerId,
                Math.Clamp(pageSize ?? 100, 1, 200),
                beforeAtUtc,
                beforeRoomId,
                cancellationToken);
            await AppendHistoryReadAuditAsync(
                context,
                auditStore,
                timeProvider.GetUtcNow(),
                playerId,
                ticketId,
                "PlayerRoomHistoryViewed",
                page.Items.Length,
                cancellationToken);
            return Results.Ok(page);
        });
        api.MapGet("/players/{playerId}/connection-history", async (
            string playerId,
            string ticketId,
            int? pageSize,
            DateTimeOffset? beforeAtUtc,
            string? beforeEventId,
            HttpContext context,
            ILobbyMonitoringClient lobby,
            IAdminActionStore auditStore,
            IAdminCaseStore caseStore,
            AdminAbacPolicyService abacPolicy,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            await RequireInvestigationHistoryAccessAsync(
                context,
                playerId,
                ticketId,
                caseStore,
                abacPolicy,
                cancellationToken);
            if (beforeEventId is not null
                && !Guid.TryParse(beforeEventId, out _))
            {
                throw AdminOperationException.Invalid(
                    "beforeEventId must be a UUID.");
            }
            var page = await lobby.ListPlayerConnectionHistoryAsync(
                playerId,
                Math.Clamp(pageSize ?? 100, 1, 200),
                beforeAtUtc,
                beforeEventId,
                cancellationToken);
            await AppendHistoryReadAuditAsync(
                context,
                auditStore,
                timeProvider.GetUtcNow(),
                playerId,
                ticketId,
                "PlayerConnectionHistoryViewed",
                page.Items.Length,
                cancellationToken);
            return Results.Ok(page);
        });
    }

    /// <summary>
    /// 历史查询必须同时具备调查类角色和有效工单标识；普通监控查看者只能看到实时脱敏摘要。
    /// </summary>
    private static async Task RequireInvestigationHistoryAccessAsync(
        HttpContext context,
        string playerId,
        string ticketId,
        IAdminCaseStore caseStore,
        AdminAbacPolicyService abacPolicy,
        CancellationToken cancellationToken)
    {
        ValidateSafeIdentifier(playerId, "playerId");
        ValidateSafeIdentifier(ticketId, "ticketId");
        var principal = AdminPrincipalContext.Get(context);
        if (!principal.HasRole(AdminRoles.PlayerOperator)
            && !principal.HasRole(AdminRoles.SanctionOperator)
            && !principal.HasRole(AdminRoles.SupportOperator)
            && !principal.HasRole(AdminRoles.RiskAnalyst)
            && !principal.HasRole(AdminRoles.PlayerApprover)
            && !principal.HasRole(AdminRoles.AuditViewer))
        {
            throw AdminOperationException.Forbidden(
                "Player history requires an investigation role.");
        }
        // 非生产环境关闭 ABAC 时保留旧的工单标识兼容路径；生产配置验证保证不会走到此分支。
        if (!abacPolicy.Enabled) return;
        var investigation = await caseStore.GetAsync(
            ticketId,
            cancellationToken)
            ?? (await caseStore.ListAsync(500, cancellationToken))
                .FirstOrDefault(item =>
                    item.TicketId.Equals(
                        ticketId,
                        StringComparison.Ordinal));
        if (investigation is null
            || investigation.TargetType != "Player"
            || investigation.TargetId != playerId
            || investigation.Status != "Open"
            || investigation.CaseType is not (
                AdminCaseType.PlayerSupport
                or AdminCaseType.ReplayReview
                or AdminCaseType.DisputeInvestigation))
        {
            throw AdminOperationException.Forbidden(
                "An open player investigation case is required.");
        }
        abacPolicy.RequireCase(context, investigation);
    }

    /// <summary>
    /// 每一页敏感历史读取都独立记账，便于按操作者、工单和 TraceId 回放调查过程。
    /// </summary>
    private static Task AppendHistoryReadAuditAsync(
        HttpContext context,
        IAdminActionStore auditStore,
        DateTimeOffset occurredAtUtc,
        string playerId,
        string ticketId,
        string operation,
        int itemCount,
        CancellationToken cancellationToken)
    {
        var principal = AdminPrincipalContext.Get(context);
        return auditStore.AppendAuditAsync(
            new AdminAuditDraft(
                occurredAtUtc,
                principal.OperatorId,
                operation,
                "Player",
                playerId,
                "Authorized persistent player history page read.",
                null,
                JsonSerializer.SerializeToElement(new { itemCount }),
                null,
                GetTraceId(context),
                ticketId),
            cancellationToken);
    }
}

