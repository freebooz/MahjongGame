using System.Collections.Frozen;
using GuiyangMahjong.Lobby.Domain;

namespace GuiyangMahjong.Lobby.Security;

/// <summary>
/// Lobby 请求关联标识中间件。
/// 业务路由强制客户端提供 UUID，健康/OpenAPI 路由可由服务端生成；
/// 规范化标识同时写入请求上下文和响应头。
/// </summary>
public sealed class RequestIdMiddleware(RequestDelegate next)
{
    private const string ItemKey = "GuiyangLobby.RequestId";
    private static readonly FrozenSet<string> ExemptPaths =
        new[] { "/health/live", "/health/ready", "/openapi/v1.yaml" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>校验或生成 RequestId；业务标识损坏时返回 400 且不进入后续管道。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Request-Id"].ToString();
        if (!Guid.TryParse(supplied, out var requestId))
        {
            if (!ExemptPaths.Contains(context.Request.Path.Value ?? string.Empty))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(
                    new ApiError(Guid.NewGuid().ToString(), "INVALID_REQUEST", "X-Request-Id 必须为有效 UUID"),
                    cancellationToken: context.RequestAborted);
                return;
            }
            requestId = Guid.NewGuid();
        }

        var normalized = requestId.ToString();
        context.Items[ItemKey] = normalized;
        context.Response.Headers["X-Request-Id"] = normalized;
        await next(context);
    }

    /// <summary>取得规范 RequestId；极早期异常尚未设置时回退 ASP.NET TraceIdentifier。</summary>
    public static string GetRequestId(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string requestId
            ? requestId
            : context.TraceIdentifier;
}
