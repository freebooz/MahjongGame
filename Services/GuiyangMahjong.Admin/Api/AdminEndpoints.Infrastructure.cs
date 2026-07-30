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
/// 承载存活、就绪与内部拓扑注册端点，集中隔离生产身份校验和依赖健康检查。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 注册基础设施端点；拓扑写入凭据使用固定时间比较，失败时不泄露令牌信息。
    /// </summary>
    private static void MapInfrastructureEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapPost("/internal/topology/registrations", (
            HttpContext context,
            MonitoringSourceRegistration registration,
            TopologyRegistry registry,
            IOptions<AdminOptions> options) =>
        {
            var discovery = options.Value.TopologyDiscovery;
            if (!discovery.Enabled
                || !HasTopologyRegistrationCredential(
                    context,
                    discovery.RegistrationToken))
            {
                return Results.Unauthorized();
            }
            return Results.Ok(registry.Register(registration));
        });
        app.MapGet("/health/ready", async (
            IAdminActionStore store,
            IAdminCaseStore caseStore,
            IPlayerAssetOperationStore assetOperationStore,
            IPlayerEvidenceStore evidenceStore,
            IAuditArchiveOutboxStore auditArchiveStore,
            CancellationToken cancellationToken) =>
            await store.CheckHealthAsync(cancellationToken)
            && await caseStore.CheckHealthAsync(cancellationToken)
            && await assetOperationStore.CheckHealthAsync(cancellationToken)
            && await evidenceStore.CheckHealthAsync(cancellationToken)
            && await auditArchiveStore.CheckHealthAsync(cancellationToken)
                ? Results.Ok(new { status = "ready", mode = "monitored-management" })
                : Results.Json(
                    new { status = "not-ready", managementStore = "unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable));

    }

    /// <summary>以固定时间比较注册凭据，防止通过响应时序探测拓扑写入令牌。</summary>
    private static bool HasTopologyRegistrationCredential(
        HttpContext context,
        string expectedToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith(
            "Bearer ",
            StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(authorization[7..].Trim())
            : [];
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        var valid = expected.Length >= 32
            && supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
        CryptographicOperations.ZeroMemory(supplied);
        CryptographicOperations.ZeroMemory(expected);
        return valid;
    }
}

