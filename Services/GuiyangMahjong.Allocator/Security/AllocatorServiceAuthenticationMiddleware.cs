// Allocator 服务身份中间件：保护内部实例管理接口并使用固定时间比较验证 Bearer 凭据。
// 缺失或长度不足的生产凭据必须关闭接口，认证失败不得在日志中记录原始令牌。
using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Allocator.Security;

/// <summary>
/// Allocator 内部接口的服务身份认证中间件。
/// 管理写入、一般内部调用和监控只读使用不同凭据范围；
/// 凭据不足最低长度时对应能力关闭，不允许匿名降级。
/// </summary>
public sealed class AllocatorServiceAuthenticationMiddleware(
    RequestDelegate next,
    IOptions<AllocatorOptions> options)
{
    // 三组字节数组在中间件生命周期内只读，分别隔离服务、监控和高风险管理权限。
    private readonly byte[] expected = Encoding.UTF8.GetBytes(options.Value.ServiceToken);
    private readonly byte[] monitoringExpected =
        Encoding.UTF8.GetBytes(options.Value.MonitoringReadOnlyToken);
    private readonly byte[] managementExpected =
        Encoding.UTF8.GetBytes(options.Value.ManagementCommandToken);

    /// <summary>
    /// 验证 /internal 路由的 Bearer 凭据并按方法/路径选择最小权限范围；
    /// 临时输入缓冲区在比较后清零，认证失败立即终止请求。
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/internal"))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(header[7..].Trim())
            : [];
        var isManagementRequest = HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/internal/admin/instances");
        var authenticated = isManagementRequest
            ? managementExpected.Length >= 32
              && supplied.Length == managementExpected.Length
              && CryptographicOperations.FixedTimeEquals(supplied, managementExpected)
            : supplied.Length == expected.Length
              && CryptographicOperations.FixedTimeEquals(supplied, expected);
        var isReadOnlyInstancesRequest = HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/internal/instances");
        if (!authenticated && isReadOnlyInstancesRequest && monitoringExpected.Length >= 32)
        {
            authenticated = supplied.Length == monitoringExpected.Length
                && CryptographicOperations.FixedTimeEquals(supplied, monitoringExpected);
        }
        CryptographicOperations.ZeroMemory(supplied);
        if (!authenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                requestId = GetRequestId(context),
                code = "UNAUTHORIZED",
                message = "A valid allocator service credential is required."
            });
            return;
        }

        await next(context);
    }

    private static string GetRequestId(HttpContext context) =>
        context.Request.Headers["X-Request-Id"].FirstOrDefault() ?? context.TraceIdentifier;
}
