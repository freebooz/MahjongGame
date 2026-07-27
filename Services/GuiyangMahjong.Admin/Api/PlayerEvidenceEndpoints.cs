using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Api;

public static partial class PlayerEvidenceEndpoints
{
    private const int MaxEvidenceBytes = 16 * 1024;
    private static readonly IReadOnlySet<string> ForbiddenDataKeys =
        new HashSet<string>(
            [
                "authorization", "password", "passwd", "token", "accesstoken",
                "refreshtoken", "cookie", "secret", "privatekey", "fullip",
                "phone", "mobile", "name", "idcard", "bankcard", "cardnumber",
                "cvv"
            ],
            StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> ChatScopes =
        new HashSet<string>(
            ["metadata", "message-content", "attachments"],
            StringComparer.Ordinal);

    public static void MapPlayerEvidenceEndpoints(this WebApplication app)
    {
        var internalApi = app.MapGroup("/internal/projections");
        internalApi.MapPost("/player-evidence", async (
            HttpContext context,
            IngestPlayerEvidenceRequest request,
            IOptions<AdminOptions> options,
            IPlayerEvidenceStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var authenticationError =
                AuthenticateIngestion(context, options.Value);
            if (authenticationError is not null) return authenticationError;
            ValidateIdempotencyKey(context, request.EventId);
            ValidateEvidence(request, timeProvider.GetUtcNow());
            try
            {
                var result = await store.IngestAsync(
                    request,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return result.Duplicate
                    ? Results.Ok(result)
                    : Results.Json(
                        result,
                        statusCode: StatusCodes.Status201Created);
            }
            catch (InvalidOperationException exception)
            {
                throw AdminOperationException.Conflict(exception.Message);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(24 * 1024));
        internalApi.MapPost("/player-chat-access-grants", async (
            HttpContext context,
            IngestPlayerChatAccessGrantRequest request,
            IOptions<AdminOptions> options,
            IPlayerEvidenceStore store,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var authenticationError =
                AuthenticateIngestion(context, options.Value);
            if (authenticationError is not null) return authenticationError;
            ValidateIdempotencyKey(context, request.GrantId);
            ValidateChatGrant(request, options.Value, timeProvider.GetUtcNow());
            try
            {
                var result = await store.IngestChatGrantAsync(
                    request,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return result.Duplicate
                    ? Results.Ok(result)
                    : Results.Json(
                        result,
                        statusCode: StatusCodes.Status201Created);
            }
            catch (InvalidOperationException exception)
            {
                throw AdminOperationException.Conflict(exception.Message);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(8 * 1024));

        var adminApi = app.MapGroup("/admin/v1/players/{playerId}");
        MapEvidenceQuery(
            adminApi,
            "/reports",
            PlayerEvidenceType.Report,
            [
                AdminRoles.RiskAnalyst,
                AdminRoles.SanctionOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer
            ]);
        MapEvidenceQuery(
            adminApi,
            "/asset-changes",
            PlayerEvidenceType.AssetChange,
            [
                AdminRoles.CompensationOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer
            ]);
        MapEvidenceQuery(
            adminApi,
            "/reward-claims",
            PlayerEvidenceType.RewardClaim,
            [
                AdminRoles.CompensationOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer
            ]);
        MapEvidenceQuery(
            adminApi,
            "/payment-orders",
            PlayerEvidenceType.PaymentOrder,
            [
                AdminRoles.CompensationOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer
            ]);
        adminApi.MapGet("/replays", async (
            string playerId,
            string caseId,
            HttpContext context,
            IAdminCaseStore caseStore,
            IPlayerEvidenceStore evidenceStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(
                context,
                AdminRoles.SupportOperator,
                AdminRoles.RiskAnalyst,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(caseId, "caseId");
            var review = await caseStore.GetAsync(caseId, cancellationToken);
            if (review is null
                || review.CaseType != AdminCaseType.ReplayReview
                || review.TargetType != "Player"
                || review.TargetId != playerId
                || review.Status != "Open")
            {
                throw AdminOperationException.Forbidden(
                    "An open, separately approved replay review case is required.");
            }
            var records = await evidenceStore.ListAsync(
                playerId,
                PlayerEvidenceType.Replay,
                Math.Clamp(limit ?? 100, 1, 200),
                cancellationToken);
            await AppendReadAuditAsync(
                auditStore,
                context,
                timeProvider.GetUtcNow(),
                playerId,
                "PlayerReplayMetadataViewed",
                review.Reason,
                review.TicketId,
                PlayerEvidenceType.Replay,
                records,
                cancellationToken);
            return Results.Ok(records);
        });
        adminApi.MapGet("/chat-permission", async (
            string playerId,
            string ticketId,
            HttpContext context,
            IPlayerEvidenceStore evidenceStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(
                context,
                AdminRoles.ChatCompliance,
                AdminRoles.AuditViewer);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(ticketId, "ticketId");
            var now = timeProvider.GetUtcNow();
            var principal = AdminPrincipalContext.Get(context);
            var grant = await evidenceStore.GetActiveChatGrantAsync(
                playerId,
                ticketId,
                principal.OperatorId,
                now,
                cancellationToken);
            var result = grant is null
                ? new PlayerChatPermissionResult(
                    false,
                    playerId,
                    ticketId,
                    [],
                    null,
                    null,
                    null,
                    "No active separately approved chat access grant exists.")
                : new PlayerChatPermissionResult(
                    true,
                    playerId,
                    ticketId,
                    grant.Scopes,
                    grant.WindowStartsAtUtc,
                    grant.WindowEndsAtUtc,
                    grant.ExpiresAtUtc,
                    "Access is limited to the approved time window and scopes.");
            await auditStore.AppendAuditAsync(
                new AdminAuditDraft(
                    now,
                    principal.OperatorId,
                    "PlayerChatPermissionChecked",
                    "Player",
                    playerId,
                    result.Reason,
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        result.Allowed,
                        result.TicketId,
                        result.Scopes,
                        result.WindowStartsAtUtc,
                        result.WindowEndsAtUtc,
                        result.ExpiresAtUtc
                    }),
                    grant is null
                        ? null
                        : JsonSerializer.SerializeToElement(new
                        {
                            grant.GrantId,
                            grant.ApprovedBy,
                            grant.CreatedAtUtc
                        }),
                    GetTraceId(context),
                    ticketId),
                cancellationToken);
            return Results.Ok(result);
        });
        adminApi.MapGet("/gm-operations", async (
            string playerId,
            string ticketId,
            HttpContext context,
            IAdminActionStore actionStore,
            TimeProvider timeProvider,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(
                context,
                AdminRoles.PlayerOperator,
                AdminRoles.SanctionOperator,
                AdminRoles.RiskAnalyst,
                AdminRoles.SupportOperator,
                AdminRoles.CompensationOperator,
                AdminRoles.PlayerApprover,
                AdminRoles.AuditViewer);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(ticketId, "ticketId");
            var principal = AdminPrincipalContext.Get(context);
            var records = (await actionStore.ListAsync(
                    500,
                    cancellationToken))
                .Where(item =>
                    item.TargetType == "Player"
                    && item.TargetId == playerId
                    && (principal.HasRole(AdminRoles.AuditViewer)
                        || principal.HasRole(AdminRoles.PlayerApprover)
                        || item.RequestedBy == principal.OperatorId
                        || principal.HasRole(
                            RequiredOperationRole(item.ActionType))))
                .OrderByDescending(item => item.RequestedAtUtc)
                .Take(Math.Clamp(limit ?? 100, 1, 200))
                .ToArray();
            await actionStore.AppendAuditAsync(
                new AdminAuditDraft(
                    timeProvider.GetUtcNow(),
                    principal.OperatorId,
                    "PlayerGmOperationsViewed",
                    "Player",
                    playerId,
                    "Authorized player GM operation history read.",
                    null,
                    JsonSerializer.SerializeToElement(new
                    {
                        count = records.Length,
                        actionRequestIds = records
                            .Select(item => item.ActionRequestId)
                            .ToArray()
                    }),
                    null,
                    GetTraceId(context),
                    ticketId),
                cancellationToken);
            return Results.Ok(records);
        });
    }

    private static void MapEvidenceQuery(
        RouteGroupBuilder group,
        string pattern,
        PlayerEvidenceType evidenceType,
        string[] roles)
    {
        group.MapGet(pattern, async (
            string playerId,
            string ticketId,
            HttpContext context,
            IPlayerEvidenceStore evidenceStore,
            IAdminActionStore auditStore,
            TimeProvider timeProvider,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            RequireAnyRole(context, roles);
            ValidateIdentifier(playerId, "playerId");
            ValidateIdentifier(ticketId, "ticketId");
            var records = await evidenceStore.ListAsync(
                playerId,
                evidenceType,
                Math.Clamp(limit ?? 100, 1, 200),
                cancellationToken);
            await AppendReadAuditAsync(
                auditStore,
                context,
                timeProvider.GetUtcNow(),
                playerId,
                "SensitivePlayerEvidenceViewed",
                $"Authorized {evidenceType} projection read.",
                ticketId,
                evidenceType,
                records,
                cancellationToken);
            return Results.Ok(records);
        });
    }

    private static async Task AppendReadAuditAsync(
        IAdminActionStore auditStore,
        HttpContext context,
        DateTimeOffset now,
        string playerId,
        string operation,
        string reason,
        string ticketId,
        PlayerEvidenceType evidenceType,
        IReadOnlyCollection<PlayerEvidenceRecord> records,
        CancellationToken cancellationToken)
    {
        var principal = AdminPrincipalContext.Get(context);
        await auditStore.AppendAuditAsync(
            new AdminAuditDraft(
                now,
                principal.OperatorId,
                operation,
                "Player",
                playerId,
                reason,
                null,
                JsonSerializer.SerializeToElement(new
                {
                    evidenceType,
                    count = records.Count,
                    sourceReferences = records
                        .Select(item => item.SourceReference)
                        .Take(200)
                        .ToArray()
                }),
                null,
                GetTraceId(context),
                ticketId),
            cancellationToken);
    }

    private static IResult? AuthenticateIngestion(
        HttpContext context,
        AdminOptions options)
    {
        if (string.IsNullOrEmpty(options.EvidenceIngestionToken))
        {
            return Results.Json(
                new
                {
                    code = "EVIDENCE_INGESTION_DISABLED",
                    message = "Player evidence ingestion is not configured."
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        var authorization =
            context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith(
            "Bearer ",
            StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(authorization[7..].Trim())
            : [];
        var expected = Encoding.UTF8.GetBytes(options.EvidenceIngestionToken);
        var valid = supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
        CryptographicOperations.ZeroMemory(supplied);
        CryptographicOperations.ZeroMemory(expected);
        return valid
            ? null
            : Results.Json(
                new
                {
                    code = "EVIDENCE_INGESTION_UNAUTHORIZED",
                    message = "A valid projection ingestion credential is required."
                },
                statusCode: StatusCodes.Status401Unauthorized);
    }

    private static void ValidateIdempotencyKey(
        HttpContext context,
        string expectedId)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (!string.Equals(key, expectedId, StringComparison.Ordinal))
        {
            throw AdminOperationException.Invalid(
                "Idempotency-Key must exactly match the event or grant id.");
        }
    }

    private static void ValidateEvidence(
        IngestPlayerEvidenceRequest request,
        DateTimeOffset now)
    {
        if (!Guid.TryParse(request.EventId, out _))
            throw AdminOperationException.Invalid("eventId must be a UUID.");
        ValidateIdentifier(request.PlayerId, "playerId");
        ValidateIdentifier(request.SourceReference, "sourceReference");
        if (!Enum.IsDefined(request.EvidenceType)
            || !Enum.IsDefined(request.Sensitivity))
        {
            throw AdminOperationException.Invalid(
                "Evidence type or sensitivity is invalid.");
        }
        var requiredSensitivity = request.EvidenceType switch
        {
            PlayerEvidenceType.AssetChange or
            PlayerEvidenceType.RewardClaim or
            PlayerEvidenceType.PaymentOrder =>
                PlayerEvidenceSensitivity.Financial,
            _ => PlayerEvidenceSensitivity.Restricted
        };
        if (request.Sensitivity != requiredSensitivity)
        {
            throw AdminOperationException.Invalid(
                $"{request.EvidenceType} evidence must be classified as {requiredSensitivity}.");
        }
        if (request.OccurredAtUtc == default
            || request.OccurredAtUtc > now.AddMinutes(5)
            || request.OccurredAtUtc < now.AddYears(-5))
        {
            throw AdminOperationException.Invalid(
                "occurredAtUtc is outside the accepted retention window.");
        }
        if (request.Data.ValueKind != JsonValueKind.Object
            || Encoding.UTF8.GetByteCount(request.Data.GetRawText())
                > MaxEvidenceBytes)
        {
            throw AdminOperationException.Invalid(
                "data must be a JSON object no larger than 16 KiB.");
        }
        RejectForbiddenData(request.Data);
    }

    private static void ValidateChatGrant(
        IngestPlayerChatAccessGrantRequest request,
        AdminOptions options,
        DateTimeOffset now)
    {
        if (!Guid.TryParse(request.GrantId, out _))
            throw AdminOperationException.Invalid("grantId must be a UUID.");
        ValidateIdentifier(request.PlayerId, "playerId");
        ValidateIdentifier(request.TicketId, "ticketId");
        ValidateIdentifier(request.GrantedTo, "grantedTo");
        ValidateIdentifier(request.ApprovedBy, "approvedBy");
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < 10 or > 1000)
        {
            throw AdminOperationException.Invalid(
                "Chat grant reason must contain 10 to 1000 characters.");
        }
        if (request.TraceId is null
            || request.TraceId.Length is < 8 or > 64
            || request.TraceId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '_' or ':' or '-')))
        {
            throw AdminOperationException.Invalid(
                "Chat grant traceId contains invalid characters or length.");
        }
        if (request.GrantedTo == request.ApprovedBy)
        {
            throw AdminOperationException.Invalid(
                "The chat reader and approver must be different people.");
        }
        var reader = options.Principals.SingleOrDefault(
            item => item.OperatorId == request.GrantedTo);
        var approver = options.Principals.SingleOrDefault(
            item => item.OperatorId == request.ApprovedBy);
        if (!options.EnterpriseIdentity.Enabled
            && (reader is null
                || !reader.Roles.Any(role => role is
                    AdminRoles.ChatCompliance or AdminRoles.AuditViewer)
                || approver is null
                || !approver.Roles.Any(role => role is
                    AdminRoles.PlayerApprover or AdminRoles.AuditViewer)))
        {
            throw AdminOperationException.Invalid(
                "The reader or independent approver is not authorized.");
        }
        if (request.WindowStartsAtUtc >= request.WindowEndsAtUtc
            || request.WindowEndsAtUtc - request.WindowStartsAtUtc
                > TimeSpan.FromDays(31)
            || request.ExpiresAtUtc <= now
            || request.ExpiresAtUtc > now.AddHours(8))
        {
            throw AdminOperationException.Invalid(
                "The chat window or grant expiry exceeds policy limits.");
        }
        var scopes = request.Scopes ?? [];
        if (scopes.Length == 0
            || scopes.Length > ChatScopes.Count
            || scopes.Distinct(StringComparer.Ordinal).Count()
                != scopes.Length
            || scopes.Any(scope => !ChatScopes.Contains(scope)))
        {
            throw AdminOperationException.Invalid(
                "Chat scopes must be a non-empty subset of metadata, message-content, and attachments.");
        }
    }

    private static void RejectForbiddenData(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var normalized = NonAlphaNumericPattern()
                    .Replace(property.Name, string.Empty);
                if (ForbiddenDataKeys.Contains(normalized)
                    || normalized.Contains(
                        "password",
                        StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains(
                        "token",
                        StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains(
                        "secret",
                        StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains(
                        "cookie",
                        StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith(
                        "name",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw AdminOperationException.Invalid(
                        $"Sensitive field '{property.Name}' is not accepted in the admin projection.");
                }
                RejectForbiddenData(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectForbiddenData(item);
        }
    }

    private static void RequireAnyRole(
        HttpContext context,
        params string[] roles)
    {
        var principal = AdminPrincipalContext.Get(context);
        if (!roles.Any(principal.HasRole))
        {
            throw AdminOperationException.Forbidden(
                "The current role cannot access this player evidence.");
        }
    }

    private static string RequiredOperationRole(
        AdminManagementActionType actionType) =>
        actionType switch
        {
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
            AdminManagementActionType.GrantPlayerCompensation or
            AdminManagementActionType.RevokeErroneousReward =>
                AdminRoles.CompensationOperator,
            _ => AdminRoles.PlayerOperator
        };

    private static void ValidateIdentifier(string? value, string name)
    {
        if (value is null
            || value.Length is < 3 or > 128
            || !SafeIdentifierPattern().IsMatch(value))
        {
            throw AdminOperationException.Invalid(
                $"{name} contains invalid characters or length.");
        }
    }

    private static string GetTraceId(HttpContext context)
    {
        var supplied =
            context.Request.Headers["X-Trace-Id"].ToString().Trim();
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

    [GeneratedRegex(
        "^[A-Za-z0-9._:-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();

    [GeneratedRegex("[^A-Za-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericPattern();
}
