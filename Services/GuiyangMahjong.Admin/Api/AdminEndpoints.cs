using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
        app.MapPost("/internal/topology/registrations", (
            HttpContext context,
            MonitoringSourceRegistration registration,
            TopologyRegistry registry,
            IOptions<AdminOptions> options) =>
        {
            var discovery = options.Value.TopologyDiscovery;
            if (!discovery.Enabled
                || !HasTopologyRegistrationCredential(
                    context,
                    discovery.RegistrationToken))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(registry.Register(registration));
        });
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
            IOptions<AdminOptions> options,
            AdminRealtimeEventHub realtimeHub) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            return Results.Ok(new
            {
                principal.OperatorId,
                roles = principal.Roles.Order(StringComparer.Ordinal).ToArray(),
                allowedRegions = principal.Regions
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                shiftId = principal.ShiftId,
                abacEnabled = options.Value.Abac.Enabled,
                managementEnabled = options.Value.Management.Enabled,
                realtime = new
                {
                    sseEnabled = options.Value.RealtimeCapacity.SseEnabled,
                    legacyPollingEnabled =
                        options.Value.RealtimeCapacity.LegacyPollingEnabled,
                    defaultPageSize =
                        options.Value.RealtimeCapacity.DefaultPageSize,
                    maximumPageSize =
                        options.Value.RealtimeCapacity.MaximumPageSize,
                    currentEventId = realtimeHub.CurrentEventId
                }
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
        api.MapGet("/source-health", (
            HttpContext context,
            MonitoringAggregationService monitoring) =>
        {
            // 来源健康可能揭示系统拓扑是否降级，仅向房间或玩家监控查看者开放。
            RequireAnyMonitoringRole(context);
            return Results.Ok(monitoring.GetReliabilityMetadata());
        });
        api.MapGet("/topology", (
            string? regionId,
            HttpContext context,
            TopologyRegistry registry,
            AdminAbacPolicyService abacPolicy) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            abacPolicy.RequireRegion(context, regionId);
            var leases = registry.ListAll();
            return Results.Ok(string.IsNullOrWhiteSpace(regionId)
                ? leases
                : leases.Where(item =>
                    item.Registration.RegionId.Equals(
                        regionId.Trim(),
                        StringComparison.OrdinalIgnoreCase)));
        });
        api.MapGet("/events", StreamRealtimeEventsAsync);
        api.MapGet("/rooms", async (
            string? regionId,
            string? clusterId,
            string? lobbyId,
            string? nodeId,
            string? lifecycle,
            string? gameMode,
            string? search,
            string? cursor,
            int? pageSize,
            HttpContext context,
            IOptions<AdminOptions> options,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            context.RequestServices
                .GetRequiredService<AdminAbacPolicyService>()
                .RequireRegion(context, regionId);
            return Results.Ok(await monitoring.ListRoomsAsync(
                regionId,
                clusterId,
                lobbyId,
                nodeId,
                lifecycle,
                gameMode,
                search,
                cursor,
                pageSize ?? options.Value.RealtimeCapacity.DefaultPageSize,
                cancellationToken));
        });
        api.MapGet("/rooms/{roomId}", async (
            string roomId,
            HttpContext context,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            var room = await monitoring.GetRoomAsync(roomId, cancellationToken);
            if (room is not null)
            {
                context.RequestServices
                    .GetRequiredService<AdminAbacPolicyService>()
                    .RequireRegion(context, room.Summary.RegionId);
            }
            return room is null ? Results.NotFound() : Results.Ok(room);
        });
        api.MapGet("/instances", async (
            string? regionId,
            string? clusterId,
            string? nodeId,
            string? cursor,
            int? pageSize,
            HttpContext context,
            MonitoringAggregationService monitoring,
            IOptions<AdminOptions> options,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            context.RequestServices
                .GetRequiredService<AdminAbacPolicyService>()
                .RequireRegion(context, regionId);
            return Results.Ok(await monitoring.ListInstancesAsync(
                regionId,
                clusterId,
                nodeId,
                cursor,
                pageSize ?? options.Value.RealtimeCapacity.DefaultPageSize,
                cancellationToken));
        });
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
                context,
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
        api.MapGet("/cases/{caseId}/evidence-package", async (
            string caseId,
            HttpContext context,
            IAdminCaseStore caseStore,
            IAdminActionStore actionStore,
            IPlayerAssetOperationStore assetStore,
            IPlayerEvidenceStore evidenceStore,
            ILobbyMonitoringClient lobby,
            AdminAbacPolicyService abacPolicy,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            ValidateSafeIdentifier(caseId, "caseId");
            var principal = AdminPrincipalContext.Get(context);
            var investigation = await caseStore.GetAsync(
                caseId,
                cancellationToken)
                ?? throw AdminOperationException.NotFound(
                    "Investigation case was not found.");
            abacPolicy.RequireCase(context, investigation);

            var actions = (await actionStore.ListAsync(500, cancellationToken))
                .Where(item =>
                    item.TargetType == investigation.TargetType
                    && item.TargetId == investigation.TargetId
                    && item.TicketId == investigation.TicketId)
                .ToArray();
            var audits = (await actionStore.ListAuditAsync(1000, cancellationToken))
                .Where(item =>
                    item.TargetType == investigation.TargetType
                    && item.TargetId == investigation.TargetId
                    && item.TicketId == investigation.TicketId)
                .ToArray();
            var assets = investigation.TargetType == "Player"
                ? (await assetStore.ListAsync(500, cancellationToken))
                    .Where(item => item.PlayerId == investigation.TargetId
                        && item.CaseId == investigation.CaseId)
                    .ToArray()
                : [];
            var playerEvidence = new List<PlayerEvidenceRecord>();
            PlayerHistoryPage<PlayerRoomHistoryRecord>? roomHistory = null;
            PlayerHistoryPage<PlayerConnectionHistoryRecord>? connectionHistory = null;
            RoomTimelineEvent[] roomTimeline = [];
            if (investigation.TargetType == "Player")
            {
                foreach (var evidenceType in Enum.GetValues<PlayerEvidenceType>())
                {
                    playerEvidence.AddRange(await evidenceStore.ListAsync(
                        investigation.TargetId,
                        evidenceType,
                        200,
                        cancellationToken));
                }
                roomHistory = await lobby.ListPlayerRoomHistoryAsync(
                    investigation.TargetId, 200, null, null, cancellationToken);
                connectionHistory =
                    await lobby.ListPlayerConnectionHistoryAsync(
                        investigation.TargetId,
                        200,
                        null,
                        null,
                        cancellationToken);
            }
            else if (investigation.TargetType == "Room")
            {
                roomTimeline = await lobby.ListEventsAsync(
                    investigation.TargetId,
                    cancellationToken);
            }

            var rangeStartsAtUtc = investigation.CreatedAtUtc.AddDays(-7);
            var rangeEndsAtUtc = timeProvider.GetUtcNow();
            var evidence = JsonSerializer.SerializeToElement(new
            {
                caseSnapshot = investigation,
                actions,
                audits,
                assetOperations = assets,
                playerEvidence = playerEvidence
                    .Where(item => item.OccurredAtUtc >= rangeStartsAtUtc
                        && item.OccurredAtUtc <= rangeEndsAtUtc)
                    .ToArray(),
                playerRoomHistory = roomHistory?.Items ?? [],
                playerConnectionHistory = connectionHistory?.Items ?? [],
                roomTimeline = roomTimeline
                    .Where(item => item.OccurredAtUtc >= rangeStartsAtUtc
                        && item.OccurredAtUtc <= rangeEndsAtUtc)
                    .ToArray()
            });
            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
            var hash = Convert.ToHexString(
                    SHA256.HashData(canonicalBytes))
                .ToLowerInvariant();
            var packageId = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(
                        $"{investigation.CaseId}:{hash}")))
                .ToLowerInvariant()[..32];
            var package = new InvestigationEvidencePackage(
                packageId,
                investigation.CaseId,
                investigation.TargetType,
                investigation.TargetId,
                rangeEndsAtUtc,
                principal.OperatorId,
                rangeStartsAtUtc,
                rangeEndsAtUtc,
                GetTraceId(context),
                investigation.TicketId,
                hash,
                JsonSerializer.SerializeToElement(new
                {
                    schemaVersion = 1,
                    hashAlgorithm = "SHA-256",
                    evidenceByteLength = canonicalBytes.Length,
                    actionCount = actions.Length,
                    auditCount = audits.Length,
                    assetOperationCount = assets.Length,
                    playerEvidenceCount = playerEvidence.Count,
                    playerRoomHistoryCount = roomHistory?.Items.Length ?? 0,
                    playerConnectionHistoryCount =
                        connectionHistory?.Items.Length ?? 0,
                    roomTimelineCount = roomTimeline.Length,
                    scope = new
                    {
                        investigation.TargetType,
                        investigation.TargetId,
                        investigation.TicketId
                    }
                }),
                evidence);
            await actionStore.AppendAuditAsync(
                new AdminAuditDraft(
                    rangeEndsAtUtc,
                    principal.OperatorId,
                    "InvestigationEvidencePackageGenerated",
                    investigation.TargetType,
                    investigation.TargetId,
                    investigation.Reason,
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        packageId = package.PackageId,
                        canonicalPayloadHash = package.CanonicalPayloadHash,
                        rangeStartsAtUtc = package.RangeStartsAtUtc,
                        rangeEndsAtUtc = package.RangeEndsAtUtc
                    }),
                    null,
                    GetTraceId(context),
                    investigation.TicketId),
                cancellationToken);
            return Results.Ok(package);
        });
        api.MapPost("/cases/{caseId}/close", async (
            string caseId,
            CloseAdminCaseRequest request,
            HttpContext context,
            IAdminCaseStore caseStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            ValidateSafeIdentifier(caseId, "caseId");
            var resolution = request.Resolution?.Trim() ?? string.Empty;
            var evidenceHash = request.EvidencePackageHash?.Trim().ToLowerInvariant()
                ?? string.Empty;
            if (resolution.Length is < 10 or > 2000
                || evidenceHash.Length != 64
                || evidenceHash.Any(character =>
                    !char.IsAsciiHexDigit(character)))
            {
                throw AdminOperationException.Invalid(
                    "A 10-2000 character resolution and SHA-256 evidence hash are required.");
            }
            var principal = AdminPrincipalContext.Get(context);
            var existing = await caseStore.GetAsync(caseId, cancellationToken)
                ?? throw AdminOperationException.NotFound(
                    "Investigation case was not found.");
            if (principal.OperatorId == existing.RequestedBy
                || (principal.OperatorId != existing.ApprovedBy
                    && !principal.HasRole(AdminRoles.AuditViewer)))
            {
                throw AdminOperationException.Forbidden(
                    "The requester cannot close the case; an independent approver is required.");
            }
            var generatedPackageExists = (await auditStore.ListAuditAsync(
                    1000,
                    cancellationToken))
                .Any(item =>
                    item.Operation == "InvestigationEvidencePackageGenerated"
                    && item.OperatorId == principal.OperatorId
                    && item.TargetType == existing.TargetType
                    && item.TargetId == existing.TargetId
                    && item.TicketId == existing.TicketId
                    && item.AfterState.HasValue
                    && item.AfterState.Value.TryGetProperty(
                        "canonicalPayloadHash",
                        out var generatedHash)
                    && string.Equals(
                        generatedHash.GetString(),
                        evidenceHash,
                        StringComparison.Ordinal));
            if (!generatedPackageExists)
            {
                throw AdminOperationException.Conflict(
                    "The supplied evidence hash was not generated for this case by the current operator.");
            }
            var closedAtUtc = timeProvider.GetUtcNow();
            var closed = await caseStore.CloseAsync(
                caseId,
                principal.OperatorId,
                resolution,
                evidenceHash,
                closedAtUtc,
                cancellationToken);
            await auditStore.AppendAuditAsync(
                new AdminAuditDraft(
                    closedAtUtc,
                    principal.OperatorId,
                    "InvestigationCaseClosed",
                    existing.TargetType,
                    existing.TargetId,
                    resolution,
                    JsonSerializer.SerializeToElement(existing),
                    JsonSerializer.SerializeToElement(closed),
                    null,
                    GetTraceId(context),
                    existing.TicketId),
                cancellationToken);
            return Results.Ok(closed);
        });
        api.MapGet("/rooms/{roomId}/log-exports/{caseId}", async (
            string roomId,
            string caseId,
            HttpContext context,
            IAdminCaseStore caseStore,
            IAdminActionStore auditStore,
            ICentralLogQueryClient centralLogQueryClient,
            IOptions<AdminOptions> options,
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
            var centralLogOptions = options.Value.CentralLogs;
            // 集中日志仅在审批通过后由服务端代理查询；浏览器永远接触不到 Loki 地址和查询凭据。
            var centralizedLogs = centralLogOptions.Enabled
                ? await centralLogQueryClient.QueryRoomAsync(
                    roomId,
                    exportedAtUtc.AddHours(-centralLogOptions.LookbackHours),
                    exportedAtUtc,
                    cancellationToken)
                : [];
            var artifact = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                artifactType = "RoomLogExport",
                source = centralLogOptions.Enabled
                    ? "CentralizedLogs"
                    : "ApprovedSnapshotOnly",
                watermark = new
                {
                    exportedBy = principal.OperatorId,
                    exportedAtUtc,
                    review.TicketId,
                    review.TraceId,
                    review.CaseId
                },
                roomId,
                approvedSnapshot = review.BeforeState,
                centralizedLogs
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
                    snapshotOnly = !centralLogOptions.Enabled,
                    centralizedLogCount = centralizedLogs.Count
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

    /// <summary>
    /// 证据包只允许案件双方和审计角色访问；目标与工单范围来自案件本身，客户端不能扩大。
    /// </summary>
    private static void RequireRole(HttpContext context, string role)
    {
        if (!AdminPrincipalContext.Get(context).HasRole(role))
            throw AdminOperationException.Forbidden($"缺少角色：{role}");
    }

    /// <summary>
    /// 来源健康同时服务房间与玩家监控；任一只读监控角色均可查看，但匿名或纯管理角色不可访问。
    /// </summary>
    private static void RequireAnyMonitoringRole(HttpContext context)
    {
        var principal = AdminPrincipalContext.Get(context);
        if (!principal.HasRole(AdminRoles.RoomViewer)
            && !principal.HasRole(AdminRoles.PlayerViewer))
        {
            throw AdminOperationException.Forbidden(
                $"缺少角色：{AdminRoles.RoomViewer} 或 {AdminRoles.PlayerViewer}");
        }
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

    /// <summary>
    /// 建立带序列断点的 SSE 流；权限在每个事件发送前复核，断点超窗时要求客户端全量重同步。
    /// </summary>
    private static async Task StreamRealtimeEventsAsync(
        HttpContext context,
        AdminRealtimeEventHub hub,
        IOptions<AdminOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.RealtimeCapacity.SseEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var rawAfter = context.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrWhiteSpace(rawAfter))
            rawAfter = context.Request.Query["after"].ToString();
        var hasAfter = !string.IsNullOrWhiteSpace(rawAfter);
        // 先初始化序列号，避免短路表达式跳过解析时留下未赋值的局部变量。
        var parsedSequence = 0L;
        var parsedCurrentInstance = hasAfter
            && hub.TryParseEventId(rawAfter, out parsedSequence);
        var afterSequence = parsedCurrentInstance
            ? parsedSequence
            : (long?)null;
        var principal = AdminPrincipalContext.Get(context);
        // 来自其他 Admin 副本的事件 ID 不可直接比较；立即要求重同步，避免负载均衡重连静默丢事件。
        var subscription = hub.Subscribe(
            afterSequence,
            hasAfter && !parsedCurrentInstance);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(cancellationToken);
        try
        {
            if (subscription.RequiresResync)
            {
                await WriteSseEventAsync(
                    context.Response,
                    hub.FormatEventId(subscription.CurrentSequence),
                    "resync",
                    new
                    {
                        reason = "EVENT_WINDOW_EXCEEDED",
                        currentSequence = subscription.CurrentSequence
                    },
                    cancellationToken);
            }
            else
            {
                foreach (var item in subscription.Backlog)
                    await WritePermittedEventAsync(
                        context.Response,
                        principal,
                        hub,
                        item,
                        cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var waitForData = subscription.Reader
                    .WaitToReadAsync(cancellationToken).AsTask();
                var heartbeat = Task.Delay(
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                if (await Task.WhenAny(waitForData, heartbeat) == heartbeat)
                {
                    await context.Response.WriteAsync(
                        $": heartbeat {DateTimeOffset.UtcNow:O}\n\n",
                        cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                    continue;
                }
                if (!await waitForData) break;
                while (subscription.Reader.TryRead(out var item))
                    await WritePermittedEventAsync(
                        context.Response,
                        principal,
                        hub,
                        item,
                        cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 浏览器关闭或网络断开是正常生命周期，finally 会释放有界订阅队列。
        }
        finally
        {
            subscription.Dispose();
        }
    }

    /// <summary>按 RBAC 过滤实体类别，防止玩家增量被发送给仅有房间查看权限的操作员。</summary>
    private static async Task WritePermittedEventAsync(
        HttpResponse response,
        AdminPrincipal principal,
        AdminRealtimeEventHub hub,
        AdminRealtimeEvent item,
        CancellationToken cancellationToken)
    {
        var permitted = item.EventType.StartsWith(
                "player.",
                StringComparison.Ordinal)
            ? principal.HasRole(AdminRoles.PlayerViewer)
            : principal.HasRole(AdminRoles.RoomViewer);
        if (!permitted) return;
        await WriteSseEventAsync(
            response,
            hub.FormatEventId(item.Sequence),
            item.EventType,
            new
            {
                entityKey = item.EntityKey,
                payload = item.Payload,
                item.OccurredAtUtc
            },
            cancellationToken);
    }

    /// <summary>写入单个 SSE 帧并刷新，确保客户端记录的 Last-Event-ID 对应已收到的数据。</summary>
    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventId,
        string eventType,
        object payload,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync(
            $"id: {eventId}\nevent: {eventType}\ndata: "
            + JsonSerializer.Serialize(payload)
            + "\n\n",
            cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static bool IsSafeIdentifier(string value) =>
        value.Length is >= 3 and <= 128
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '-');

    /// <summary>以固定时间比较注册凭据，防止通过响应时序探测拓扑写入令牌。</summary>
    private static bool HasTopologyRegistrationCredential(
        HttpContext context,
        string expectedToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith(
            "Bearer ",
            StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(authorization[7..].Trim())
            : [];
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        var valid = expected.Length >= 32
            && supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
        CryptographicOperations.ZeroMemory(supplied);
        CryptographicOperations.ZeroMemory(expected);
        return valid;
    }
}
