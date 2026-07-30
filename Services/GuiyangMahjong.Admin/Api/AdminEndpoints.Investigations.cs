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
/// 承载调查案件、证据包、房间日志导出和回放查询，确保读取也可审计。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 注册案件与证据端点；关闭案件前必须验证独立审批和证据包哈希。
    /// </summary>
    private static void MapInvestigationEndpoints(RouteGroupBuilder api)
    {
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
}

