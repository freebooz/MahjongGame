// Allocator HTTP API：提供实例分配、注册、心跳、回收、故障和监控查询入口。
// 内部写接口必须验证服务身份并保持幂等；监控接口只暴露脱敏运行状态。
using GuiyangMahjong.Allocator.Domain;
using GuiyangMahjong.Allocator.Services;
using GuiyangMahjong.Allocator.Options;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

namespace GuiyangMahjong.Allocator.Api;

/// <summary>
/// Allocator 最小 API 路由模块。
/// 健康探针、服务端注册/心跳、Admin 终止和监控读取在同一处声明，
/// 身份认证由前置中间件按路径和方法隔离。
/// </summary>
public static class AllocatorEndpoints
{
    /// <summary>
    /// 注册 Allocator 全部 HTTP 路由。
    /// 写端点把 RequestId 传入领域层保证幂等；就绪探针验证恢复状态、端口和启动后端，
    /// 监控响应不包含注册/心跳凭据。
    /// </summary>
    public static void MapAllocatorEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (
            GameServerInstanceManager manager,
            GameServerProcessLauncher launcher,
            PortLeasePool portLeasePool,
            IAgonesAllocationClient agones,
            IOptions<AllocatorOptions> options,
            CancellationToken cancellationToken) =>
        {
            var agonesMode = options.Value.Backend == AllocatorBackendMode.Agones;
            var executableReady = false;
            var workingDirectoryReady = false;
            if (!agonesMode) try
            {
                executableReady = GameServerProcessLauncher.IsExecutable(launcher.GetResolvedExecutablePath());
                workingDirectoryReady = Directory.Exists(launcher.GetResolvedWorkingDirectory());
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidOperationException)
            {
                // Readiness reports the condition without leaking server paths to callers.
            }

            var stateDirectoryReady = IsWritableDirectory(GetParentDirectory(options.Value.StateFilePath));
            var outboxDirectoryReady = IsWritableDirectory(
                MatchResultOutboxPaths.GetDirectory(options.Value));
            var gamePortReady = agonesMode || HasBindableUdpPort(portLeasePool);
            var orchestratorReady = !agonesMode || await agones.CheckReadyAsync(cancellationToken);
            var ready = manager.IsInitialized
                        && (agonesMode || executableReady)
                        && (agonesMode || workingDirectoryReady)
                        && stateDirectoryReady
                        && outboxDirectoryReady
                        && gamePortReady
                        && orchestratorReady;
            return ready
                ? Results.Ok(new
                {
                    status = "ready",
                    stateReconciled = true,
                    backend = options.Value.Backend.ToString(),
                    orchestrator = orchestratorReady ? "ready" : "unavailable",
                    gameServerExecutable = agonesMode ? "not-applicable" : "ready",
                    gameServerWorkingDirectory = agonesMode ? "not-applicable" : "ready",
                    allocatorState = "writable",
                    matchResultOutbox = "writable",
                    gamePortCapacity = "available"
                })
                : Results.Json(new
                {
                    status = "not-ready",
                    stateReconciled = manager.IsInitialized,
                    backend = options.Value.Backend.ToString(),
                    orchestrator = orchestratorReady ? "ready" : "unavailable",
                    gameServerExecutable = agonesMode ? "not-applicable" : executableReady ? "ready" : "unavailable",
                    gameServerWorkingDirectory = agonesMode ? "not-applicable" : workingDirectoryReady ? "ready" : "unavailable",
                    allocatorState = stateDirectoryReady ? "writable" : "unavailable",
                    matchResultOutbox = outboxDirectoryReady ? "writable" : "unavailable",
                    gamePortCapacity = gamePortReady ? "available" : "exhausted-or-occupied"
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        app.MapGet("/openapi/v1.yaml", async (HttpContext context) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "OpenAPI", "allocator-v1.openapi.yaml");
            context.Response.ContentType = "application/yaml; charset=utf-8";
            await context.Response.SendFileAsync(path, context.RequestAborted);
        });

        var internalApi = app.MapGroup("/internal");
        internalApi.MapPost("/allocations", async (
            HttpContext context,
            AllocationRequest request,
            GameServerInstanceManager manager,
            CancellationToken cancellationToken) => Results.Accepted(value: await manager.AllocateAsync(
                GetRequestId(context), request, cancellationToken)));

        internalApi.MapGet("/instances", (GameServerInstanceManager manager) => Results.Ok(manager.List()));
        internalApi.MapGet("/instances/{serverInstanceId}", (
            string serverInstanceId,
            GameServerInstanceManager manager) => manager.Get(serverInstanceId) is { } instance
                ? Results.Ok(instance)
                : Results.NotFound());

        internalApi.MapPost("/instances/{serverInstanceId}/register", async (
            string serverInstanceId,
            HttpContext context,
            ConfirmRegistrationRequest request,
            GameServerInstanceManager manager,
            CancellationToken cancellationToken) => Results.Ok(await manager.ConfirmRegistrationAsync(
                GetRequestId(context), serverInstanceId, request, cancellationToken)));

        internalApi.MapPost("/instances/{serverInstanceId}/heartbeat", async (
            string serverInstanceId,
            InstanceHeartbeatRequest request,
            GameServerInstanceManager manager,
            CancellationToken cancellationToken) =>
        {
            await manager.RecordHeartbeatAsync(serverInstanceId, request, cancellationToken);
            return Results.NoContent();
        });

        internalApi.MapPost("/instances/{serverInstanceId}/drain", async (
            string serverInstanceId,
            GameServerInstanceManager manager,
            CancellationToken cancellationToken) => Results.Ok(await manager.DrainAsync(
                serverInstanceId, cancellationToken)));

        internalApi.MapPost("/admin/instances/{serverInstanceId}/terminate", async (
            string serverInstanceId,
            AdminTerminateInstanceRequest request,
            HttpContext context,
            GameServerInstanceManager manager,
            CancellationToken cancellationToken) =>
        {
            var commandId =
                context.Request.Headers["Idempotency-Key"].ToString().Trim();
            if (commandId.Length is < 16 or > 128
                || serverInstanceId.Length is < 1 or > 128
                || !Enum.TryParse<GameServerInstanceState>(
                    request.ExpectedState,
                    out var expectedState)
                || (request.Reason ?? string.Empty).Trim().Length is < 5 or > 500
                || (request.TraceId ?? string.Empty).Trim().Length is < 8 or > 64)
            {
                return Results.BadRequest();
            }
            var result = await manager.TerminateAbnormalAsync(
                serverInstanceId,
                expectedState,
                cancellationToken);
            return Results.Ok(new AdminTerminateInstanceResult(
                commandId,
                result.Instance,
                result.AlreadyStopped));
        });
    }

    private static string GetParentDirectory(string configuredPath)
    {
        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
        return Path.GetDirectoryName(Path.GetFullPath(path))
               ?? throw new InvalidOperationException("Allocator state path has no parent directory.");
    }

    private static bool IsWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".readiness-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return !File.Exists(probe);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasBindableUdpPort(PortLeasePool portLeasePool)
    {
        foreach (var port in portLeasePool.GetAvailablePorts())
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    ExclusiveAddressUse = true
                };
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return true;
            }
            catch (SocketException)
            {
                // Try the next logically available port; another OS process may own this one.
            }
        }

        return false;
    }

    private static string GetRequestId(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Request-Id"].ToString();
        return Guid.TryParse(supplied, out var id) ? id.ToString() : Guid.NewGuid().ToString();
    }
}
