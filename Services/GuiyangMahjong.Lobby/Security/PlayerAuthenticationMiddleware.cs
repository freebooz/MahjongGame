using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Services;

namespace GuiyangMahjong.Lobby.Security;

/// <summary>
/// Lobby 玩家 API 的认证边界。
/// 只保护 /v1 路由，验证 Auth HMAC 令牌和管理员撤销水位，
/// 成功后写入最小 PlayerIdentity 并刷新在线状态。
/// </summary>
public sealed class PlayerAuthenticationMiddleware(RequestDelegate next)
{
    /// <summary>HttpContext.Items 中保存已验证玩家身份的稳定键。</summary>
    public const string PlayerItemKey = "GuiyangLobby.Player";

    /// <summary>
    /// 验证 Bearer 令牌、签发时间与撤销状态；任一步失败返回统一 401 且不调用后续管道。
    /// 原始令牌不写日志或请求上下文。
    /// </summary>
    public async Task InvokeAsync(
        HttpContext context,
        IPlayerTokenValidator tokenValidator,
        IOnlinePresenceService presence,
        IPlayerAccessRevocationStore revocations)
    {
        if (!context.Request.Path.StartsWithSegments("/v1"))
        {
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorized(context, "缺少玩家登录凭据");
            return;
        }

        var result = tokenValidator.Validate(authorization["Bearer ".Length..].Trim());
        if (!result.IsValid || result.Player is null)
        {
            await WriteUnauthorized(context, result.ChineseReason);
            return;
        }
        if (await revocations.IsRevokedAsync(
                result.Player.PlayerId,
                result.IssuedAtUtc,
                context.RequestAborted))
        {
            await WriteUnauthorized(context, "登录会话已由管理员终止，请重新登录");
            return;
        }

        var clientBuild = context.Request.Headers["X-Client-Version"].ToString().Trim();
        var protocolVersion = context.Request.Headers["X-Protocol-Version"].ToString().Trim();
        // 旧直连调用只保留到网关迁移结束；其票据会携带 legacy/0，并被严格托管 DS 拒绝。
        if (clientBuild.Length == 0) clientBuild = "legacy";
        if (protocolVersion.Length == 0) protocolVersion = "0";
        if (clientBuild.Length is < 1 or > 80 || protocolVersion.Length is < 1 or > 32
            || !clientBuild.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '-' or '_' or '+')
            || !protocolVersion.All(char.IsAsciiDigit))
        {
            await WriteUnauthorized(context, "客户端版本上下文无效");
            return;
        }
        // 版本头已由 EdgeGateway 清洗和校验；它们只参与兼容绑定，绝不构成玩家身份。
        context.Items[PlayerItemKey] = result.Player with
        {
            ClientBuild = clientBuild,
            ProtocolVersion = protocolVersion
        };
        await presence.TouchAsync(result.Player.PlayerId, context.RequestAborted);
        await next(context);
    }

    /// <summary>取得当前请求已验证玩家；中间件未执行时抛出异常而不是创建匿名身份。</summary>
    public static PlayerIdentity GetPlayer(HttpContext context) =>
        context.Items[PlayerItemKey] as PlayerIdentity
        ?? throw new InvalidOperationException("玩家身份中间件尚未执行");

    private static async Task WriteUnauthorized(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/problem+json";
        var requestId = RequestIdMiddleware.GetRequestId(context);
        await context.Response.WriteAsJsonAsync(
            new ApiError(requestId, "SESSION_EXPIRED", message),
            cancellationToken: context.RequestAborted);
    }
}
