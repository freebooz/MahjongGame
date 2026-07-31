using System.Text.Json;
using System.Text.Json.Serialization;
using GuiyangMahjong.Contracts.Common;

namespace GuiyangMahjong.Contracts.Api;

/// <summary>
/// 跨服务稳定错误码目录。
/// 业务服务可以映射到现有响应模型，但不得把异常文本、凭据或数据库信息作为错误详情。
/// </summary>
public static class PlatformErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string IdempotencyConflict = "IDEMPOTENCY_KEY_CONFLICT";
    public const string IdempotencyInProgress = "IDEMPOTENCY_IN_PROGRESS";
    public const string DeadlineExceeded = "DEADLINE_EXCEEDED";
    public const string RateLimited = "RATE_LIMITED";
    public const string DependencyUnavailable = "DEPENDENCY_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>
/// 可适配 RFC 9457 Problem Details 或项目现有错误模型的传输中立错误。
/// Extensions 只能包含经过白名单审查的非敏感诊断字段。
/// </summary>
public sealed record ApiProblem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("correlation_id")] CorrelationId CorrelationId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("extensions")] IReadOnlyDictionary<string, JsonElement>? Extensions = null);

/// <summary>API 响应携带的调用追踪元数据；不替代 HTTP 状态码或业务响应正文。</summary>
public sealed record ApiResponseContext(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("correlation_id")] CorrelationId CorrelationId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("service_version")] BuildVersion ServiceVersion);
