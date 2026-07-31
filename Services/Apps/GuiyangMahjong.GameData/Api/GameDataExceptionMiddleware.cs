using System.Diagnostics;
using GuiyangMahjong.GameData.Settlement;

namespace GuiyangMahjong.GameData.Api;

/// <summary>将稳定领域错误转换为 Problem Details；日志只记录错误码和 TraceId，不记录凭据或私有牌。</summary>
public sealed class GameDataExceptionMiddleware(
    RequestDelegate next,
    ILogger<GameDataExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (GameDataException exception)
        {
            logger.LogWarning(
                "GameData 请求被拒绝 Code={Code} Status={Status} TraceId={TraceId}",
                exception.Code, exception.StatusCode, context.TraceIdentifier);
            context.Response.StatusCode = exception.StatusCode;
            await Results.Problem(
                statusCode: exception.StatusCode,
                title: exception.Code,
                detail: exception.Message,
                extensions: new Dictionary<string, object?>
                {
                    ["trace_id"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
                }).ExecuteAsync(context);
        }
    }
}
