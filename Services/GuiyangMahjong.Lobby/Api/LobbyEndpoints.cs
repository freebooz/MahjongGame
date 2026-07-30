namespace GuiyangMahjong.Lobby.Api;

/// <summary>
/// 组合 Lobby 健康检查、内部控制、监控查询和玩家公开 API。
/// 入口只负责稳定注册顺序，具体授权、存储和响应语义由对应 partial 分区维护。
/// </summary>
public static partial class LobbyEndpoints
{
    /// <summary>
    /// 将完整 Lobby HTTP/WebSocket 契约映射到应用。
    /// 调用方必须先注册身份、请求追踪、存储和实时事件依赖。
    /// </summary>
    public static void MapLobbyEndpoints(this WebApplication app)
    {
        MapHealthEndpoints(app);
        MapInternalEndpoints(app);
        MapMonitoringEndpoints(app);
        MapPublicEndpoints(app);
    }
}
