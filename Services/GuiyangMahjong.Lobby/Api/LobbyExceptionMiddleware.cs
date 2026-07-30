using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Security;
using GuiyangMahjong.Lobby.Services;

namespace GuiyangMahjong.Lobby.Api;

/// <summary>
/// Lobby HTTP 异常边界。
/// 领域异常保持稳定错误码和重试建议，解析/未知异常返回脱敏响应并记录 RequestId；
/// 响应已开始时重新抛出，避免写出损坏的双重响应。
/// </summary>
public sealed class LobbyExceptionMiddleware(
    RequestDelegate next,
    ILogger<LobbyExceptionMiddleware> logger)
{
    /// <summary>执行后续管道并统一转换异常；未知异常不会向客户端泄漏堆栈和内部配置。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (LobbyOperationException exception)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = exception.StatusCode;
            context.Response.ContentType = "application/problem+json";
            if (exception.RetryAfterMilliseconds is { } retry)
            {
                context.Response.Headers.RetryAfter = Math.Max(1, retry / 1000).ToString();
            }
            await context.Response.WriteAsJsonAsync(
                new ApiError(
                    RequestIdMiddleware.GetRequestId(context),
                    exception.StableCode,
                    exception.Message,
                    exception.RetryAfterMilliseconds),
                cancellationToken: context.RequestAborted);
        }
        catch (BadHttpRequestException exception)
        {
            if (context.Response.HasStarted) throw;
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(
                new ApiError(
                    RequestIdMiddleware.GetRequestId(context),
                    "INVALID_REQUEST",
                    "请求内容无法解析"),
                cancellationToken: context.RequestAborted);
            logger.LogWarning(exception, "大厅请求内容解析失败 RequestId={RequestId}",
                RequestIdMiddleware.GetRequestId(context));
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted) throw;
            logger.LogError(exception, "大厅请求发生未处理异常 RequestId={RequestId}",
                RequestIdMiddleware.GetRequestId(context));
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(
                new ApiError(
                    RequestIdMiddleware.GetRequestId(context),
                    "INTERNAL_ERROR",
                    "大厅服务暂时不可用"),
                cancellationToken: context.RequestAborted);
        }
    }
}
