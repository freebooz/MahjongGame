using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Api;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (
            IAdminActionStore store,
            CancellationToken cancellationToken) =>
            await store.CheckHealthAsync(cancellationToken)
                ? Results.Ok(new { status = "ready", mode = "monitored-management" })
                : Results.Json(
                    new { status = "not-ready", managementStore = "unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable));

        var api = app.MapGroup("/admin/v1");
        api.MapGet("/me", (
            HttpContext context,
            IOptions<AdminOptions> options) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            return Results.Ok(new
            {
                principal.OperatorId,
                roles = principal.Roles.Order(StringComparer.Ordinal).ToArray(),
                managementEnabled = options.Value.Management.Enabled
            });
        });

        api.MapGet("/overview", async (
            HttpContext context,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            return Results.Ok(await monitoring.GetOverviewAsync(cancellationToken));
        });
        api.MapGet("/rooms", async (
            string? lifecycle,
            string? gameMode,
            string? search,
            HttpContext context,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            return Results.Ok(await monitoring.ListRoomsAsync(
                lifecycle, gameMode, search, cancellationToken));
        });
        api.MapGet("/rooms/{roomId}", async (
            string roomId,
            HttpContext context,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            var room = await monitoring.GetRoomAsync(roomId, cancellationToken);
            return room is null ? Results.NotFound() : Results.Ok(room);
        });
        api.MapGet("/instances", async (
            HttpContext context,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            return Results.Ok(await monitoring.ListInstancesAsync(cancellationToken));
        });
        api.MapGet("/players", async (
            string? search,
            HttpContext context,
            PlayerMonitoringService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.PlayerViewer);
            return Results.Ok(await monitoring.ListPlayersAsync(search, cancellationToken));
        });
        api.MapGet("/players/{playerId}", async (
            string playerId,
            HttpContext context,
            PlayerMonitoringService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.PlayerViewer);
            var player = await monitoring.GetPlayerAsync(playerId, cancellationToken);
            if (player is null) return Results.NotFound();
            var principal = AdminPrincipalContext.Get(context);
            var canViewControlHistory =
                principal.HasRole(AdminRoles.SanctionOperator)
                || principal.HasRole(AdminRoles.RiskAnalyst)
                || principal.HasRole(AdminRoles.PlayerApprover)
                || principal.HasRole(AdminRoles.AuditViewer);
            return Results.Ok(canViewControlHistory
                ? player
                : player with
                {
                    ControlHistory = [],
                    DataScope = "ReadOnlyMaskedControlHistoryRedacted"
                });
        });

        api.MapGet("/action-requests", async (
            HttpContext context,
            AdminActionWorkflow workflow,
            int? limit,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ListAsync(
                AdminPrincipalContext.Get(context),
                Math.Clamp(limit ?? 100, 1, 500),
                cancellationToken)));
        api.MapPost("/action-requests", async (
            HttpContext context,
            CreateAdminActionRequest request,
            AdminActionWorkflow workflow,
            CancellationToken cancellationToken) =>
            Results.Accepted(value: await workflow.CreateAsync(
                AdminPrincipalContext.Get(context),
                request,
                GetTraceId(context),
                cancellationToken)));
        api.MapPost("/action-requests/{actionRequestId}/confirm", async (
            string actionRequestId,
            HttpContext context,
            ConfirmAdminActionRequest request,
            AdminActionWorkflow workflow,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ConfirmAsync(
                AdminPrincipalContext.Get(context),
                actionRequestId,
                request,
                cancellationToken)));
        api.MapPost("/action-requests/{actionRequestId}/approvals", async (
            string actionRequestId,
            HttpContext context,
            ApproveAdminActionRequest request,
            AdminActionWorkflow workflow,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ApproveAsync(
                AdminPrincipalContext.Get(context),
                actionRequestId,
                request,
                cancellationToken)));
        api.MapGet("/audit", async (
            HttpContext context,
            AdminActionWorkflow workflow,
            int? limit,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ListAuditAsync(
                AdminPrincipalContext.Get(context),
                Math.Clamp(limit ?? 200, 1, 1000),
                cancellationToken)));
        api.MapGet("/command-outbox", async (
            HttpContext context,
            AdminActionWorkflow workflow,
            int? limit,
            CancellationToken cancellationToken) =>
            Results.Ok(await workflow.ListOutboxAsync(
                AdminPrincipalContext.Get(context),
                Math.Clamp(limit ?? 100, 1, 500),
                cancellationToken)));
    }

    private static void RequireRole(HttpContext context, string role)
    {
        if (!AdminPrincipalContext.Get(context).HasRole(role))
            throw AdminOperationException.Forbidden($"缺少角色：{role}");
    }

    private static string GetTraceId(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Trace-Id"].ToString().Trim();
        return supplied.Length > 0 ? supplied : context.TraceIdentifier;
    }
}
