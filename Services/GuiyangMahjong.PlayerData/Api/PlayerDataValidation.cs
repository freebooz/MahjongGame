using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuiyangMahjong.PlayerData.Domain;
using GuiyangMahjong.PlayerData.Storage;

namespace GuiyangMahjong.PlayerData.Api;

public static partial class PlayerDataValidation
{
    private static readonly IReadOnlySet<string> ForbiddenDataKeys =
        new HashSet<string>(
            [
                "authorization", "password", "passwd", "token",
                "accesstoken", "refreshtoken", "cookie", "secret",
                "privatekey", "fullip", "phone", "mobile", "name",
                "idcard", "bankcard", "cardnumber", "cvv", "email",
                "address"
            ],
            StringComparer.OrdinalIgnoreCase);

    public static bool HasBearer(HttpContext context, string expectedToken)
    {
        if (expectedToken.Length < 32) return false;
        var authorization =
            context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith(
            "Bearer ",
            StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(authorization[7..].Trim())
            : [];
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        var valid = supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
        CryptographicOperations.ZeroMemory(supplied);
        CryptographicOperations.ZeroMemory(expected);
        return valid;
    }

    public static string RequireIdempotencyKey(HttpContext context)
    {
        var value =
            context.Request.Headers["Idempotency-Key"].ToString().Trim();
        if (!Guid.TryParse(value, out var parsed))
            throw PlayerDataOperationException.Invalid(
                "Idempotency-Key must be a UUID.");
        return parsed.ToString();
    }

    public static void ValidateEvidence(
        RecordEvidenceRequest request,
        PlayerEvidenceType expectedType,
        DateTimeOffset now)
    {
        if (!Guid.TryParse(request.EventId, out _))
            throw PlayerDataOperationException.Invalid(
                "eventId must be a UUID.");
        ValidateIdentifier(request.PlayerId, "playerId");
        ValidateIdentifier(request.SourceReference, "sourceReference");
        if (request.EvidenceType != expectedType)
            throw PlayerDataOperationException.Invalid(
                $"evidenceType must be {expectedType}.");
        var requiredSensitivity = expectedType is
            PlayerEvidenceType.PaymentOrder
            ? PlayerEvidenceSensitivity.Financial
            : PlayerEvidenceSensitivity.Restricted;
        if (request.Sensitivity != requiredSensitivity)
            throw PlayerDataOperationException.Invalid(
                $"{expectedType} must be classified as {requiredSensitivity}.");
        if (request.OccurredAtUtc < now.AddYears(-5)
            || request.OccurredAtUtc > now.AddMinutes(5))
            throw PlayerDataOperationException.Invalid(
                "occurredAtUtc is outside the accepted window.");
        if (request.Data.ValueKind != JsonValueKind.Object
            || Encoding.UTF8.GetByteCount(request.Data.GetRawText())
                > 16 * 1024)
            throw PlayerDataOperationException.Invalid(
                "data must be a JSON object no larger than 16 KiB.");
        RejectForbiddenData(request.Data);
    }

    public static void ValidateReward(
        RewardClaimRequest request,
        DateTimeOffset now)
    {
        if (!Guid.TryParse(request.EventId, out _))
            throw PlayerDataOperationException.Invalid(
                "eventId must be a UUID.");
        ValidateIdentifier(request.RewardGrantId, "rewardGrantId");
        ValidateIdentifier(request.PlayerId, "playerId");
        ValidateIdentifier(request.SourceReference, "sourceReference");
        ValidateIdentifier(request.AssetCode, "assetCode", 2, 32);
        ValidateTraceId(request.TraceId);
        if (request.Amount is < 1 or > 1_000_000_000)
            throw PlayerDataOperationException.Invalid(
                "Reward amount is outside the allowed range.");
        if (request.OccurredAtUtc < now.AddYears(-5)
            || request.OccurredAtUtc > now.AddMinutes(5))
            throw PlayerDataOperationException.Invalid(
                "occurredAtUtc is outside the accepted window.");
    }

    public static void ValidateWalletOperation(
        AdminWalletOperationRequest request,
        DateTimeOffset now)
    {
        ValidateIdentifier(request.PlayerId, "playerId");
        if (!Guid.TryParse(request.CaseId, out _))
            throw PlayerDataOperationException.Invalid(
                "caseId must be a UUID.");
        ValidateIdentifier(request.RequestedBy, "requestedBy");
        ValidateIdentifier(request.ApprovedBy, "approvedBy");
        ValidateIdentifier(request.TicketId, "ticketId");
        ValidateTraceId(request.TraceId);
        if (request.RequestedBy == request.ApprovedBy)
            throw PlayerDataOperationException.Invalid(
                "requestedBy and approvedBy must be different.");
        if (request.Reason.Trim().Length is < 10 or > 1000)
            throw PlayerDataOperationException.Invalid(
                "reason must contain 10 to 1000 characters.");
        if (request.ApprovedAtUtc < now.AddDays(-7)
            || request.ApprovedAtUtc > now.AddMinutes(1))
            throw PlayerDataOperationException.Invalid(
                "approvedAtUtc is outside the accepted command window.");
        if (request.OperationType == "GrantCompensation")
        {
            ValidateIdentifier(
                request.AssetCode,
                "assetCode",
                2,
                32);
            if (request.Amount is < 1 or > 1_000_000_000
                || request.RewardGrantId is not null)
                throw PlayerDataOperationException.Invalid(
                    "Compensation payload is invalid.");
        }
        else if (request.OperationType == "RevokeReward")
        {
            ValidateIdentifier(
                request.RewardGrantId,
                "rewardGrantId");
            if (request.AssetCode is not null || request.Amount is not null)
                throw PlayerDataOperationException.Invalid(
                    "Reward reversal payload is invalid.");
        }
        else
        {
            throw PlayerDataOperationException.Invalid(
                "operationType is invalid.");
        }
    }

    public static void ValidateChatAuthorization(
        AuthorizeChatMessageRequest request,
        DateTimeOffset now)
    {
        if (!Guid.TryParse(request.MessageId, out _))
            throw PlayerDataOperationException.Invalid(
                "messageId must be a UUID.");
        ValidateIdentifier(request.PlayerId, "playerId");
        ValidateIdentifier(request.RoomId, "roomId");
        if (request.RequestedAtUtc < now.AddMinutes(-5)
            || request.RequestedAtUtc > now.AddMinutes(1))
            throw PlayerDataOperationException.Invalid(
                "requestedAtUtc is outside the accepted window.");
    }

    public static void ValidateIdentifier(
        string? value,
        string name,
        int minimumLength = 3,
        int maximumLength = 128)
    {
        if (value is null
            || value.Length < minimumLength
            || value.Length > maximumLength
            || !SafeIdentifierPattern().IsMatch(value))
            throw PlayerDataOperationException.Invalid(
                $"{name} contains invalid characters or length.");
    }

    private static void ValidateTraceId(string? value)
    {
        if (value is null
            || value.Length is < 8 or > 64
            || !SafeIdentifierPattern().IsMatch(value))
            throw PlayerDataOperationException.Invalid(
                "traceId contains invalid characters or length.");
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
                    || normalized.EndsWith(
                        "name",
                        StringComparison.OrdinalIgnoreCase))
                    throw PlayerDataOperationException.Invalid(
                        $"Sensitive field '{property.Name}' is not accepted.");
                RejectForbiddenData(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectForbiddenData(item);
        }
    }

    [GeneratedRegex(
        "^[A-Za-z0-9._:-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();

    [GeneratedRegex("[^A-Za-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericPattern();
}
