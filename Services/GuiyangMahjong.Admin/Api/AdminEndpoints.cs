namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 统一注册管理后台 HTTP 端点；具体领域路由由同名 partial 模块承载，保持路由顺序与公共入口稳定。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 注册健康检查、监控查询、玩家调查、审批、案件证据和审计路由；仅改变代码组织，不改变既有 API 契约。
    /// </summary>
    public static void MapAdminEndpoints(this WebApplication app)
    {
        MapInfrastructureEndpoints(app);
        MapOperationsGroup(app.MapGroup("/admin/v1"));
        // 新路径明确表达 BFF 后方的受控 Operations API；旧路径保留一个兼容周期，响应契约完全一致。
        MapOperationsGroup(app.MapGroup("/admin/operations/v1"));
    }

    /// <summary>把同一组受控运营能力映射到新旧前缀，避免复制处理器后产生授权策略漂移。</summary>
    private static void MapOperationsGroup(RouteGroupBuilder api)
    {
        MapIdentityEndpoints(api);
        MapMonitoringEndpoints(api);
        MapPlayerEndpoints(api);
        MapActionEndpoints(api);
        MapInvestigationEndpoints(api);
        MapAuditEndpoints(api);
        MapConfigurationEndpoints(api);
    }
}
