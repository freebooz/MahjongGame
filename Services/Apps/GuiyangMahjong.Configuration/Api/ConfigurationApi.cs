using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.Configuration.Domain;
using GuiyangMahjong.Configuration.Infrastructure;
using GuiyangMahjong.Configuration.Options;
using GuiyangMahjong.Configuration.Services;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;

namespace GuiyangMahjong.Configuration.Api;

/// <summary>
/// Configuration Service 的 HTTP 边界。管理命令、服务拉取和公开客户端视图使用不同路由与凭据，
/// 防止客户端取得审批数据、完整白名单、Fleet 路由或配置签名材料。
/// </summary>
public static class ConfigurationApi
{
    /// <summary>注册健康检查、受控发布 API、服务拉取 API 和只读客户端求值 API。</summary>
    public static void MapConfigurationEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/startup", () => Results.Ok(new { status = "started" }));
        app.MapGet("/health/ready", async (IConfigurationStore store, CancellationToken token) =>
            await store.CheckHealthAsync(token)
                ? Results.Ok(new { status = "ready" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        var admin = app.MapGroup("/internal/admin/configurations")
            .AddEndpointFilter<ConfigurationTokenFilter>();
        admin.MapPost("/drafts", CreateDraftAsync);
        admin.MapGet("/drafts", async (PlatformConfigurationService service, CancellationToken token) =>
            Results.Ok(await service.ListDraftsAsync(token)));
        admin.MapGet("/drafts/{draftId}", async (string draftId, PlatformConfigurationService service, CancellationToken token) =>
            await service.GetDraftAsync(draftId, token) is { } draft ? Results.Ok(draft) : Results.NotFound());
        admin.MapPost("/drafts/{draftId}/validate", ValidateDraftAsync);
        admin.MapPost("/drafts/{draftId}/publish", PublishAsync);
        admin.MapGet("/{configKey}/current", async (string configKey, PlatformConfigurationService service, CancellationToken token) =>
            await service.GetCurrentAsync(configKey, token) is { } value ? Results.Ok(value) : Results.NotFound());
        admin.MapGet("/{configKey}/versions", async (string configKey, PlatformConfigurationService service, CancellationToken token) =>
            Results.Ok(await service.ListVersionsAsync(configKey, token)));
        admin.MapPost("/{configKey}/rollback", RollbackAsync);

        var serviceApi = app.MapGroup("/internal/configurations")
            .AddEndpointFilter<ConfigurationTokenFilter>();
        serviceApi.MapGet("/{configKey}/current", async (string configKey, PlatformConfigurationService service, CancellationToken token) =>
            await service.GetCurrentAsync(configKey, token) is { } value ? Results.Ok(value) : Results.NotFound());
        serviceApi.MapPost("/application-reports", async (ConfigurationApplicationReport report, PlatformConfigurationService service, CancellationToken token) =>
        {
            await service.RecordApplicationAsync(report, token);
            return Results.Accepted();
        });

        // 客户端只拿经过稳定分桶后的结果，绝不返回完整白名单、Fleet 或审批链。
        app.MapGet("/api/v1/config/client", EvaluateClientAsync);
    }

    private static async Task<IResult> CreateDraftAsync(
        HttpContext context, CreateConfigurationDraftRequest request,
        PlatformConfigurationService service, CancellationToken token)
    {
        var operatorId = RequiredHeader(context, "X-Operator-Id");
        var idempotencyKey = RequiredHeader(context, "Idempotency-Key");
        return Results.Created($"/internal/admin/configurations/drafts",
            await service.CreateDraftAsync(request, operatorId, context.TraceIdentifier, idempotencyKey, token));
    }

    private static async Task<IResult> ValidateDraftAsync(
        string draftId, HttpContext context, PlatformConfigurationService service, CancellationToken token) =>
        Results.Ok(await service.ValidateDraftAsync(draftId, RequiredHeader(context, "X-Operator-Id"), token));

    private static async Task<IResult> PublishAsync(
        string draftId, PublishConfigurationCommand command,
        PlatformConfigurationService service, CancellationToken token) =>
        Results.Ok(await service.PublishAsync(draftId, command, token));

    private static async Task<IResult> RollbackAsync(
        string configKey, RollbackConfigurationCommand command,
        PlatformConfigurationService service, CancellationToken token) =>
        Results.Ok(await service.RollbackAsync(configKey, command, token));

    private static async Task<IResult> EvaluateClientAsync(
        HttpContext context, PlatformConfigurationService service, CancellationToken token)
    {
        var current = await service.GetCurrentAsync(PlatformConfigurationService.PlatformConfigKey, token);
        if (current is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var subject = new RolloutSubject(
            context.User.Identity?.IsAuthenticated == true ? context.User.FindFirst("sub")?.Value : null,
            context.Request.Headers["X-Device-Digest"].ToString(),
            context.Request.Headers["X-Channel"].ToString(),
            context.Request.Headers["X-Client-Version"].ToString(),
            context.Request.Headers["X-Platform"].ToString(),
            context.Request.Headers["X-Region"].ToString(),
            string.Equals(context.Request.Headers["X-Test-Account"], "true", StringComparison.OrdinalIgnoreCase));
        return Results.Ok(service.EvaluateClient(current, subject));
    }

    private static string RequiredHeader(HttpContext context, string name) =>
        !string.IsNullOrWhiteSpace(context.Request.Headers[name])
            ? context.Request.Headers[name].ToString()
            : throw new ConfigurationOperationException("CONFIG_HEADER_REQUIRED", $"缺少请求头 {name}。", 400);
}

/// <summary>
/// 以固定时间比较静态启动凭据。管理路由与服务路由使用隔离 Token；Token 不写入日志、Trace 或响应。
/// </summary>
public sealed class ConfigurationTokenFilter(IOptions<ConfigurationOptions> options) : IEndpointFilter
{
    /// <summary>在模型绑定前验证 Bearer 凭据；失败统一返回 401，避免泄露配置中心内部状态。</summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var expected = request.Path.StartsWithSegments("/internal/admin", StringComparison.Ordinal)
            ? options.Value.AdminCommandToken
            : options.Value.ServiceReadToken;
        var supplied = request.Headers.Authorization.ToString();
        if (supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) supplied = supplied[7..].Trim();
        if (!FixedEquals(supplied, expected)) return Results.Unauthorized();
        return await next(context);
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

/// <summary>将领域错误转换为稳定 Problem Details；响应不包含配置正本和内部异常堆栈。</summary>
public sealed class ConfigurationExceptionMiddleware(
    RequestDelegate next,
    ILogger<ConfigurationExceptionMiddleware> logger)
{
    /// <summary>捕获预期校验/并发错误并保留 TraceId；未知异常失败关闭为 500。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (ConfigurationOperationException exception)
        {
            await WriteAsync(context, exception.StatusCode, exception.Code, exception.Message);
        }
        catch (ConfigurationConflictException exception)
        {
            await WriteAsync(context, 409, exception.Code, "配置状态发生并发冲突。");
        }
        catch (Exception exception)
        {
            var root = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
            logger.LogError(exception, "Configuration 请求处理失败。ErrorType={ErrorType} TraceId={TraceId}", root.GetType().FullName, context.TraceIdentifier);
            await WriteAsync(context, 500, "CONFIG_INTERNAL_ERROR", "配置服务暂时不可用。");
        }
    }

    private static Task WriteAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Extensions = { ["trace_id"] = context.TraceIdentifier }
        }, context.RequestAborted);
    }
}
