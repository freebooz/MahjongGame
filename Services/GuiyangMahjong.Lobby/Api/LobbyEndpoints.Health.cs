using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;

namespace GuiyangMahjong.Lobby.Api;

/// <summary>
/// Lobby 存活与就绪探针分区；存活只反映进程，就绪同时验证持久化和 Allocator。
/// </summary>
public static partial class LobbyEndpoints
{
    /// <summary>注册 Kubernetes、Compose 和负载均衡器使用的健康探针。</summary>
    private static void MapHealthEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapGet("/health/ready", async (
            ILobbyStore store,
            IAllocatorClient allocator,
            CancellationToken cancellationToken) =>
        {
            var persistenceReady =
                await store.CheckHealthAsync(cancellationToken);
            var allocatorReady =
                await allocator.CheckReadinessAsync(cancellationToken);
            return persistenceReady && allocatorReady
                ? Results.Ok(new
                {
                    status = "ready",
                    persistence = "ready",
                    allocator = "ready"
                })
                : Results.Json(
                    new
                    {
                        status = "not-ready",
                        persistence = persistenceReady
                            ? "ready"
                            : "unavailable",
                        allocator = allocatorReady
                            ? "ready"
                            : "unavailable"
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }
}
