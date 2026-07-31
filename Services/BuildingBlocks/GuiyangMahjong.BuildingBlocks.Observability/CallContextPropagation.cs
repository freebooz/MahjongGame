using System.Diagnostics;
using GuiyangMahjong.BuildingBlocks.Security;
using GuiyangMahjong.Contracts.Common;

namespace GuiyangMahjong.BuildingBlocks.Observability;

/// <summary>
/// HTTP、gRPC 元数据和消息 Header 共用的调用上下文传播器。
/// 传播值仅包含非敏感标识；Token 和玩家原始身份必须由各传输认证层处理。
/// </summary>
public static class CallContextPropagation
{
    public const string RequestIdHeader = "x-request-id";
    public const string CorrelationIdHeader = "x-correlation-id";
    public const string TraceParentHeader = "traceparent";
    public const string CallerServiceHeader = "x-caller-service";
    public const string ClientVersionHeader = "x-client-version";
    public const string ProtocolVersionHeader = "x-protocol-version";
    public const string ServiceVersionHeader = "x-service-version";
    public const string DeadlineHeader = "x-deadline-utc";

    /// <summary>
    /// 把调用上下文写入新的 Header 字典并注入当前 W3C traceparent。
    /// 调用方不得把返回字典与外部请求头无条件合并，应覆盖同名内部头。
    /// </summary>
    public static IReadOnlyDictionary<string, string> CreateOutgoingHeaders(
        CallContextContract context)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RequestIdHeader] = context.RequestId,
            [CorrelationIdHeader] = context.CorrelationId.Value,
            [TraceParentHeader] = Activity.Current?.Id
                                  ?? $"00-{context.TraceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01",
            [CallerServiceHeader] = context.CallerService,
            [ProtocolVersionHeader] = context.ProtocolVersion,
            [ServiceVersionHeader] = context.ServiceVersion.Value
        };
        if (context.ClientVersion is { } clientVersion)
            headers[ClientVersionHeader] = clientVersion.Value;
        if (context.Deadline is { } deadline)
            headers[DeadlineHeader] = deadline.ToString("O");
        return headers;
    }

    /// <summary>
    /// 从已经过可信边界清洗的 Header 建立调用上下文。
    /// 缺失或非法字段立即失败，禁止静默生成新的 Correlation ID 破坏链路调查。
    /// </summary>
    public static CallContextContract ParseIncomingHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        string Required(string name) =>
            headers.TryGetValue(name, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new FormatException($"缺少调用上下文头：{name}。");

        var traceParent = Required(TraceParentHeader);
        if (!ActivityContext.TryParse(
                traceParent,
                headers.GetValueOrDefault("tracestate"),
                out var activityContext))
            throw new FormatException("traceparent 格式无效。");
        var context = new CallContextContract(
            Required(RequestIdHeader),
            CorrelationId.Parse(Required(CorrelationIdHeader)),
            activityContext.TraceId.ToHexString(),
            Required(CallerServiceHeader),
            headers.TryGetValue(ClientVersionHeader, out var clientVersion)
                ? BuildVersion.Parse(clientVersion)
                : null,
            Required(ProtocolVersionHeader),
            BuildVersion.Parse(Required(ServiceVersionHeader)),
            headers.TryGetValue(DeadlineHeader, out var deadline)
                ? DateTimeOffset.Parse(
                    deadline,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)
                : null);
        context.Validate(DateTimeOffset.UtcNow);
        return context;
    }

    /// <summary>建立服务端 Activity 并附加非敏感标签；返回值必须由调用方释放。</summary>
    public static Activity? StartServerActivity(
        ActivitySource source,
        string operationName,
        CallContextContract context,
        CallerSecurityContext caller,
        ActivityContext parentContext)
    {
        caller.Validate();
        var activity = source.StartActivity(
            operationName,
            ActivityKind.Server,
            parentContext);
        activity?.SetTag("mahjong.request_id", context.RequestId);
        activity?.SetTag(
            "mahjong.correlation_id",
            context.CorrelationId.Value);
        activity?.SetTag("mahjong.caller_kind", caller.Kind.ToString());
        activity?.SetTag("mahjong.caller_service", context.CallerService);
        return activity;
    }
}
