using System.Text;
using System.Text.Json;
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
            IAdminCaseStore caseStore,
            IPlayerAssetOperationStore assetOperationStore,
            IPlayerEvidenceStore evidenceStore,
            IAuditArchiveOutboxStore auditArchiveStore,
            CancellationToken cancellationToken) =>
            await store.CheckHealthAsync(cancellationToken)
            && await caseStore.CheckHealthAsync(cancellationToken)
            && await assetOperationStore.CheckHealthAsync(cancellationToken)
            && await evidenceStore.CheckHealthAsync(cancellationToken)
            && await auditArchiveStore.CheckHealthAsync(cancellationToken)
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
            string? ticketId,
            HttpContext context,
            PlayerMonitoringService monitoring,
            IAdminActionStore auditStore,
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
                GetIdempotencyKey(context),
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
        api.MapGet("/cases", async (
            HttpContext context,
            IAdminCaseStore caseStore,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            var cases = await caseStore.ListAsync(
                Math.Clamp(limit ?? 200, 1, 500),
                cancellationToken);
            var permitted = cases.Where(item => item.CaseType switch
            {
                AdminCaseType.DisputeInvestigation =>
                    principal.HasRole(AdminRoles.RoomOperator)
                    || principal.HasRole(AdminRoles.RoomApprover)
                    || principal.HasRole(AdminRoles.AuditViewer),
                AdminCaseType.PlayerSupport =>
                    principal.HasRole(AdminRoles.SupportOperator)
                    || principal.HasRole(AdminRoles.PlayerApprover)
                    || principal.HasRole(AdminRoles.AuditViewer),
                AdminCaseType.CompensationReview =>
                    principal.HasRole(AdminRoles.CompensationOperator)
                    || principal.HasRole(AdminRoles.RoomApprover)
                    || principal.HasRole(AdminRoles.AuditViewer),
                AdminCaseType.ReplayReview when item.TargetType == "Room" =>
                    principal.HasRole(AdminRoles.RoomOperator)
                    || principal.HasRole(AdminRoles.RoomApprover)
                    || principal.HasRole(AdminRoles.AuditViewer),
                AdminCaseType.ReplayReview =>
                    principal.HasRole(AdminRoles.SupportOperator)
                    || principal.HasRole(AdminRoles.RiskAnalyst)
                    || principal.HasRole(AdminRoles.PlayerApprover)
                    || principal.HasRole(AdminRoles.AuditViewer),
                AdminCaseType.RoomLogExport =>
                    principal.HasRole(AdminRoles.RoomOperator)
                    || principal.HasRole(AdminRoles.RoomApprover)
                    || principal.HasRole(AdminRoles.AuditViewer),
                _ => false
            }).ToArray();
            if (permitted.Length == 0
                && !principal.Roles.Any(role => role is
                    AdminRoles.RoomOperator
                    or AdminRoles.RoomApprover
                    or AdminRoles.SupportOperator
                    or AdminRoles.RiskAnalyst
                    or AdminRoles.PlayerApprover
                    or AdminRoles.CompensationOperator
                    or AdminRoles.AuditViewer))
            {
                throw AdminOperationException.Forbidden(
                    "The current role cannot view management cases.");
            }
            return Results.Ok(permitted);
        });
        api.MapGet("/rooms/{roomId}/log-exports/{caseId}", async (
            string roomId,
            string caseId,
            HttpContext context,
            IAdminCaseStore caseStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            ValidateSafeIdentifier(roomId, "roomId");
            ValidateSafeIdentifier(caseId, "caseId");
            var principal = AdminPrincipalContext.Get(context);
            var review = await RequireOpenRoomCaseAsync(
                caseStore,
                principal,
                roomId,
                caseId,
                AdminCaseType.RoomLogExport,
                cancellationToken);
            var exportedAtUtc = timeProvider.GetUtcNow();
            var artifact = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                artifactType = "RoomLogExport",
                watermark = new
                {
                    exportedBy = principal.OperatorId,
                    exportedAtUtc,
                    review.TicketId,
                    review.TraceId,
                    review.CaseId
                },
                roomId,
                approvedSnapshot = review.BeforeState
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            });
            await AppendCaseReadAuditAsync(
                auditStore,
                principal,
                review,
                "RoomLogsExported",
                exportedAtUtc,
                new
                {
                    contentType = "application/json",
                    byteLength = artifact.Length,
                    snapshotOnly = true
                },
                GetTraceId(context),
                cancellationToken);
            return Results.File(
                artifact,
                "application/json",
                $"room-{roomId}-logs-{caseId}.json");
        });
        api.MapGet("/rooms/{roomId}/replays", async (
            string roomId,
            string caseId,
            HttpContext context,
            IAdminCaseStore caseStore,
            IPlayerEvidenceStore evidenceStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            ValidateSafeIdentifier(roomId, "roomId");
            ValidateSafeIdentifier(caseId, "caseId");
            var principal = AdminPrincipalContext.Get(context);
            var review = await RequireOpenRoomCaseAsync(
                caseStore,
                principal,
                roomId,
                caseId,
                AdminCaseType.ReplayReview,
                cancellationToken);
            var playerIds = GetStringArray(
                review.BeforeState,
                "playerIds");
            var records = new List<PlayerEvidenceRecord>();
            foreach (var playerId in playerIds.Take(16))
            {
                records.AddRange(await evidenceStore.ListAsync(
                    playerId,
                    PlayerEvidenceType.Replay,
                    200,
                    cancellationToken));
            }
            var roomRecords = records
                .Where(item =>
                    TryGetString(item.Data, "roomId", out var evidenceRoomId)
                    && evidenceRoomId == roomId)
                .DistinctBy(item => item.EventId)
                .OrderByDescending(item => item.OccurredAtUtc)
                .Take(200)
                .ToArray();
            await AppendCaseReadAuditAsync(
                auditStore,
                principal,
                review,
                "RoomReplayMetadataViewed",
                timeProvider.GetUtcNow(),
                new
                {
                    count = roomRecords.Length,
                    eventIds = roomRecords.Select(item => item.EventId)
                },
                GetTraceId(context),
                cancellationToken);
            return Results.Ok(roomRecords);
        });
        api.MapGet("/player-asset-operations", async (
            HttpContext context,
            IPlayerAssetOperationStore assetOperationStore,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            if (!principal.HasRole(AdminRoles.CompensationOperator)
                && !principal.HasRole(AdminRoles.PlayerApprover)
                && !principal.HasRole(AdminRoles.AuditViewer))
            {
                throw AdminOperationException.Forbidden(
                    "The current role cannot view player asset operations.");
            }
            return Results.Ok(await assetOperationStore.ListAsync(
                Math.Clamp(limit ?? 200, 1, 500),
                cancellationToken));
        });
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

    private static async Task<AdminCaseRecord> RequireOpenRoomCaseAsync(
        IAdminCaseStore caseStore,
        AdminPrincipal principal,
        string roomId,
        string caseId,
        AdminCaseType expectedType,
        CancellationToken cancellationToken)
    {
        var review = await caseStore.GetAsync(caseId, cancellationToken);
        if (review is null
            || review.CaseType != expectedType
            || review.TargetType != "Room"
            || review.TargetId != roomId
            || review.Status != "Open")
        {
            throw AdminOperationException.Forbidden(
                "An open, separately approved room case is required.");
        }
        var hasRole = principal.HasRole(AdminRoles.RoomOperator)
            || principal.HasRole(AdminRoles.RoomApprover)
            || principal.HasRole(AdminRoles.AuditViewer);
        var linkedOperator = principal.OperatorId == review.RequestedBy
            || principal.OperatorId == review.ApprovedBy;
        if (!hasRole || (!linkedOperator
            && !principal.HasRole(AdminRoles.AuditViewer)))
        {
            throw AdminOperationException.Forbidden(
                "The administrator is not linked to this approved room case.");
        }
        return review;
    }

    private static Task AppendCaseReadAuditAsync(
        IAdminActionStore auditStore,
        AdminPrincipal principal,
        AdminCaseRecord review,
        string operation,
        DateTimeOffset occurredAtUtc,
        object result,
        string traceId,
        CancellationToken cancellationToken) =>
        auditStore.AppendAuditAsync(
            new AdminAuditDraft(
                occurredAtUtc,
                principal.OperatorId,
                operation,
                "Room",
                review.TargetId,
                review.Reason,
                null,
                JsonSerializer.SerializeToElement(result),
                JsonSerializer.SerializeToElement(new
                {
                    review.CaseId,
                    review.RequestedBy,
                    review.ApprovedBy
                }),
                traceId,
                review.TicketId),
            cancellationToken);

    private static string[] GetStringArray(
        JsonElement element,
        string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void ValidateSafeIdentifier(
        string value,
        string name)
    {
        if (!IsSafeIdentifier(value))
            throw AdminOperationException.Invalid(
                $"{name} contains invalid characters or length.");
    }

    private static string GetTraceId(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Trace-Id"].ToString().Trim();
        if (supplied.Length == 0) return context.TraceIdentifier;
        if (supplied.Length > 64
            || supplied.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '_' or ':' or '-')))
        {
            throw AdminOperationException.Invalid(
                "X-Trace-Id contains invalid characters or length.");
        }
        return supplied;
    }

    private static string GetIdempotencyKey(HttpContext context)
    {
        var value =
            context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (value.Length is < 16 or > 128
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '_' or ':' or '-')))
        {
            throw AdminOperationException.Invalid(
                "Idempotency-Key must contain 16 to 128 safe characters.");
        }
        return value;
    }

    private static bool IsSafeIdentifier(string value) =>
        value.Length is >= 3 and <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-');
}
