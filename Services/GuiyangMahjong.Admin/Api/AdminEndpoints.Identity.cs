using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Admin.Security;
using GuiyangMahjong.Admin.Services;
using GuiyangMahjong.Admin.Storage;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 承载当前管理人员身份与实时能力声明，供 Angular 控制台建立授权视图。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 注册当前主体信息端点；只返回已解析的角色、区域和容量能力，不暴露原始令牌。
    /// </summary>
    private static void MapIdentityEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/me", (
            HttpContext context,
            IOptions<AdminOptions> options,
            AdminRealtimeEventHub realtimeHub) =>
        {
            var principal = AdminPrincipalContext.Get(context);
            return Results.Ok(new
            {
                principal.OperatorId,
                roles = principal.Roles.Order(StringComparer.Ordinal).ToArray(),
                allowedRegions = principal.Regions
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                shiftId = principal.ShiftId,
                abacEnabled = options.Value.Abac.Enabled,
                managementEnabled = options.Value.Management.Enabled,
                realtime = new
                {
                    sseEnabled = options.Value.RealtimeCapacity.SseEnabled,
                    legacyPollingEnabled =
                        options.Value.RealtimeCapacity.LegacyPollingEnabled,
                    defaultPageSize =
                        options.Value.RealtimeCapacity.DefaultPageSize,
                    maximumPageSize =
                        options.Value.RealtimeCapacity.MaximumPageSize,
                    currentEventId = realtimeHub.CurrentEventId
                }
            });
        });
    }
}

