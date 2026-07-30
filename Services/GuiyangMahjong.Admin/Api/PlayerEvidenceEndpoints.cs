namespace GuiyangMahjong.Admin.Api;

/// <summary>
/// 注册玩家调查证据、回放、聊天合规和 GM 操作历史接口。
/// 所有敏感读取均由对应分区完成授权、工单绑定和审计，入口只负责组合路由。
/// </summary>
public static partial class PlayerEvidenceEndpoints
{
    /// <summary>
    /// 将玩家证据相关端点映射到 Admin 应用。
    /// 调用方必须已注册身份中间件、证据存储、审计存储以及受控归档客户端。
    /// </summary>
    public static void MapPlayerEvidenceEndpoints(this WebApplication app)
    {
        MapProjectionIngestionEndpoints(app);

        var adminApi = app.MapGroup("/admin/v1/players/{playerId}");
        MapEvidenceQueryEndpoints(adminApi);
        MapReplayEndpoints(adminApi);
        MapChatComplianceEndpoints(adminApi);
        MapGmOperationEndpoints(adminApi);
    }
}
