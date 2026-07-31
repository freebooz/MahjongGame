using System.Diagnostics;
using System.Text.Json.Serialization;

namespace GuiyangMahjong.Contracts.Common;

/// <summary>
/// 跨 HTTP、gRPC 和异步事件传递的非敏感调用上下文。
/// 不包含 Access Token、Join Ticket、权限明细或私有业务数据。
/// </summary>
public sealed record CallContextContract(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("correlation_id")] CorrelationId CorrelationId,
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("caller_service")] string CallerService,
    [property: JsonPropertyName("client_version")] BuildVersion? ClientVersion,
    [property: JsonPropertyName("protocol_version")] string ProtocolVersion,
    [property: JsonPropertyName("service_version")] BuildVersion ServiceVersion,
    [property: JsonPropertyName("deadline")] DateTimeOffset? Deadline)
{
    /// <summary>在进入业务处理前校验必填字段和截止时间；过期调用必须由入口拒绝。</summary>
    public void Validate(DateTimeOffset now)
    {
        if (!StrongValueValidation.IsOperationKey(RequestId))
            throw new FormatException("request_id 格式无效。");
        if (!IsW3CTraceId(TraceId)
            || !StrongValueValidation.IsIdentifier(CallerService)
            || !StrongValueValidation.IsIdentifier(ProtocolVersion))
        {
            throw new FormatException("调用上下文包含无效标识。");
        }
        if (Deadline <= now)
            throw new TimeoutException("调用截止时间已经过期。");
    }

    /// <summary>
    /// 将 Deadline 转为可传播的取消令牌；返回的令牌源必须由调用方释放。
    /// 没有 Deadline 时只链接上游取消令牌。
    /// </summary>
    public CancellationTokenSource CreateCancellationSource(
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        if (Deadline is { } deadline)
        {
            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                source.Cancel();
            else
                source.CancelAfter(remaining);
        }
        return source;
    }

    /// <summary>验证 16 字节非零 W3C Trace ID；异常输入不能进入 Activity 传播。</summary>
    private static bool IsW3CTraceId(string value)
    {
        if (value.Length != 32
            || value.All(character => character == '0'))
            return false;
        try
        {
            _ = ActivityTraceId.CreateFromString(value.AsSpan());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
