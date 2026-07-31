// Allocator 异常中间件：把已知领域失败映射为稳定错误码，同时隐藏内部异常和凭据。
// 未知异常必须保留 TraceId 并交由结构化日志记录，不得向客户端返回堆栈。
using GuiyangMahjong.Allocator.Domain;

namespace GuiyangMahjong.Allocator.Api;

/// <summary>
/// Allocator HTTP 异常边界。
/// 已知领域错误保持稳定状态码，基础设施和未知错误记录请求关联信息后返回脱敏消息；
/// 响应已开始时重新抛出以避免写出损坏的双重响应。
/// </summary>
public sealed class AllocatorExceptionMiddleware(
    RequestDelegate next,
    ILogger<AllocatorExceptionMiddleware> logger)
{
    /// <summary>执行后续管道并统一转换异常；未知异常不会向调用方暴露堆栈。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AllocatorOperationException exception)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = exception.StatusCode;
            await WriteProblemAsync(context, "ALLOCATOR_OPERATION_REJECTED", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            if (context.Response.HasStarted) throw;
            logger.LogWarning(exception, "Allocator request rejected RequestId={RequestId}", GetRequestId(context));
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteProblemAsync(context, "ALLOCATOR_UNAVAILABLE", exception.Message);
        }
        catch (TaskCanceledException exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted) throw;
            logger.LogWarning(exception, "Allocator upstream timed out RequestId={RequestId}", GetRequestId(context));
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await WriteProblemAsync(context, "PROVIDER_TIMEOUT", "GameServer provider timed out.");
        }
        catch (HttpRequestException exception)
        {
            if (context.Response.HasStarted) throw;
            logger.LogWarning(exception, "Allocator provider unavailable RequestId={RequestId}", GetRequestId(context));
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteProblemAsync(context, "PROVIDER_UNAVAILABLE", "GameServer provider is unavailable.");
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted) throw;
            logger.LogError(exception, "Allocator request failed RequestId={RequestId}", GetRequestId(context));
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteProblemAsync(context, "INTERNAL_ERROR", "Allocator is temporarily unavailable.");
        }
    }

    private static Task WriteProblemAsync(HttpContext context, string code, string message)
    {
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(new { requestId = GetRequestId(context), code, message });
    }

    private static string GetRequestId(HttpContext context) =>
        context.Request.Headers["X-Request-Id"].FirstOrDefault() ?? context.TraceIdentifier;
}
