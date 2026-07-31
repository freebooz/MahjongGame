using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// Angular 管理台的 BFF 会话入口。它只交换已完成 MFA 的管理员身份并管理不透明 Cookie，
/// 不代理游戏 UDP、不访问业务数据库，也不把企业 Access Token 持久化到浏览器。
/// </summary>
public static class AdminBffEndpoints
{
    /// <summary>注册会话创建、读取和注销端点；全部响应禁止缓存。</summary>
    public static void MapAdminBffEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/bff/v1");
        group.MapPost("/session", CreateSessionAsync);
        group.MapGet("/session", GetSession);
        group.MapDelete("/session", DeleteSessionAsync);
    }

    /// <summary>
    /// 将当前已验证 Bearer 身份交换为短期 Cookie 会话。输入设备标识只参与不可逆摘要，
    /// 返回的 CSRF 值由 Angular 保存在内存，刷新页面后需重新建立会话。
    /// </summary>
    private static async Task<IResult> CreateSessionAsync(
        HttpContext context,
        AdminBrowserSessionService sessions,
        IOptions<AdminOptions> options,
        CancellationToken cancellationToken)
    {
        DisableCaching(context.Response);
        var security = options.Value.WebSecurity;
        if (!security.BrowserSessionEnabled)
            return Results.NotFound();
        var deviceId = context.Request.Headers["X-Admin-Device-Id"].ToString().Trim();
        if (security.BindDevice && (deviceId.Length is < 8 or > 128
            || deviceId.Any(character => char.IsControl(character))))
        {
            await sessions.RecordRejectionAsync(
                context,
                AdminPrincipalContext.Get(context).OperatorId,
                "ADMIN_DEVICE_REQUIRED",
                cancellationToken);
            return Results.BadRequest(new
            {
                code = "ADMIN_DEVICE_REQUIRED",
                message = "管理员会话需要有效的设备摘要标识。",
                traceId = context.TraceIdentifier
            });
        }
        var principal = AdminPrincipalContext.Get(context);
        var created = await sessions.CreateAsync(principal, context, cancellationToken);
        context.Response.Cookies.Append(
            security.SessionCookieName,
            created.SessionToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = security.RequireHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
                MaxAge = created.Record.ExpiresAtUtc - created.Record.CreatedAtUtc
            });
        return Results.Ok(new
        {
            csrfToken = created.CsrfToken,
            expiresAtUtc = created.Record.ExpiresAtUtc,
            operatorId = principal.OperatorId,
            roles = principal.Roles
        });
    }

    /// <summary>返回当前 Cookie 会话的非敏感身份摘要；认证与绑定校验已由中间件完成。</summary>
    private static IResult GetSession(HttpContext context)
    {
        DisableCaching(context.Response);
        return Results.Ok(new
        {
            operatorId = AdminPrincipalContext.Get(context).OperatorId,
            roles = AdminPrincipalContext.Get(context).Roles,
            authenticated = true
        });
    }

    /// <summary>原子撤销当前会话并删除浏览器 Cookie；CSRF 校验由认证中间件先行执行。</summary>
    private static async Task<IResult> DeleteSessionAsync(
        HttpContext context,
        AdminBrowserSessionService sessions,
        IOptions<AdminOptions> options,
        CancellationToken cancellationToken)
    {
        DisableCaching(context.Response);
        var security = options.Value.WebSecurity;
        if (context.Request.Cookies.TryGetValue(security.SessionCookieName, out var token))
            await sessions.RevokeAsync(token, cancellationToken);
        context.Response.Cookies.Delete(security.SessionCookieName, new CookieOptions
        {
            Secure = security.RequireHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
        return Results.NoContent();
    }

    /// <summary>禁止代理或浏览器缓存身份、角色和 CSRF 响应，避免共享终端从历史记录恢复管理凭据。</summary>
    private static void DisableCaching(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
