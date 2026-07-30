using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 玩家证据端点共享的身份校验、输入约束、角色决策和 TraceId 规则。
/// 这些规则集中维护，保证所有证据分区采用相同的安全边界。
/// </summary>
public static partial class PlayerEvidenceEndpoints
{
    /// <summary>单个证据投影 JSON 数据允许的最大 UTF-8 字节数。</summary>
    private const int MaxEvidenceBytes = 16 * 1024;

    /// <summary>投影数据中禁止出现的凭据、直接身份和支付敏感字段规范化名称。</summary>
    private static readonly IReadOnlySet<string> ForbiddenDataKeys =
        new HashSet<string>(
            [
                "authorization", "password", "passwd", "token", "accesstoken",
                "refreshtoken", "cookie", "secret", "privatekey", "fullip",
                "phone", "mobile", "name", "idcard", "bankcard", "cardnumber",
                "cvv"
            ],
            StringComparer.OrdinalIgnoreCase);

    /// <summary>聊天授权可审批的最小字段范围白名单。</summary>
    private static readonly IReadOnlySet<string> ChatScopes =
        new HashSet<string>(
            ["metadata", "message-content", "attachments"],
            StringComparer.Ordinal);

    /// <summary>
    /// 使用固定时间比较验证内部投影凭据；未配置凭据时关闭入口而不是降级为匿名访问。
    /// </summary>
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

    /// <summary>要求 HTTP 幂等键与事件或授权主键完全一致，避免同一载荷被换键重复写入。</summary>
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

    /// <summary>
    /// 校验证据类型、敏感等级、时间窗口、大小和禁止字段；失败时不产生任何存储副作用。
    /// </summary>
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

    /// <summary>
    /// 校验聊天授权的双人审批、时间窗口、范围白名单和企业身份回退规则。
    /// </summary>
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

    /// <summary>递归拒绝证据 JSON 中的凭据和直接身份字段，数组内容同样检查。</summary>
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

    /// <summary>要求当前 Admin 主体至少具有一个允许角色，否则以领域禁止错误终止请求。</summary>
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

    /// <summary>返回查看指定玩家管理操作所需的最小业务角色。</summary>
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

    /// <summary>校验路由和工单标识只包含安全字符且长度位于受控范围。</summary>
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

    /// <summary>读取并校验调用方 TraceId；缺失时使用 ASP.NET 请求 TraceIdentifier。</summary>
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

    /// <summary>匹配允许出现在玩家、案件、工单和来源引用中的标识字符。</summary>
    [GeneratedRegex(
        "^[A-Za-z0-9._:-]+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();

    /// <summary>规范化证据字段名，以便大小写和分隔符变化不能绕过禁止字段检查。</summary>
    [GeneratedRegex("[^A-Za-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericPattern();
}
