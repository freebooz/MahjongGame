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
/// 承载服务器、房间与拓扑只读监控，以及带断点续传和逐事件 RBAC 的 SSE 推送。
/// </summary>
public static partial class AdminEndpoints
{
    /// <summary>
    /// 注册监控查询与实时事件端点；区域 ABAC 和分页容量限制仍由原服务执行。
    /// </summary>
    private static void MapMonitoringEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/overview", async (
            HttpContext context,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            return Results.Ok(await monitoring.GetOverviewAsync(cancellationToken));
        });
        api.MapGet("/source-health", (
            HttpContext context,
            MonitoringAggregationService monitoring) =>
        {
            // 来源健康可能揭示系统拓扑是否降级，仅向房间或玩家监控查看者开放。
            RequireAnyMonitoringRole(context);
            return Results.Ok(monitoring.GetReliabilityMetadata());
        });
        api.MapGet("/topology", (
            string? regionId,
            HttpContext context,
            TopologyRegistry registry,
            AdminAbacPolicyService abacPolicy) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            abacPolicy.RequireRegion(context, regionId);
            var leases = registry.ListAll();
            return Results.Ok(string.IsNullOrWhiteSpace(regionId)
                ? leases
                : leases.Where(item =>
                    item.Registration.RegionId.Equals(
                        regionId.Trim(),
                        StringComparison.OrdinalIgnoreCase)));
        });
        api.MapGet("/events", StreamRealtimeEventsAsync);
        api.MapGet("/rooms", async (
            string? regionId,
            string? clusterId,
            string? lobbyId,
            string? nodeId,
            string? lifecycle,
            string? gameMode,
            string? search,
            string? cursor,
            int? pageSize,
            HttpContext context,
            IOptions<AdminOptions> options,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            context.RequestServices
                .GetRequiredService<AdminAbacPolicyService>()
                .RequireRegion(context, regionId);
            return Results.Ok(await monitoring.ListRoomsAsync(
                regionId,
                clusterId,
                lobbyId,
                nodeId,
                lifecycle,
                gameMode,
                search,
                cursor,
                pageSize ?? options.Value.RealtimeCapacity.DefaultPageSize,
                cancellationToken));
        });
        api.MapGet("/rooms/{roomId}", async (
            string roomId,
            HttpContext context,
            MonitoringAggregationService monitoring,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            var room = await monitoring.GetRoomAsync(roomId, cancellationToken);
            if (room is not null)
            {
                context.RequestServices
                    .GetRequiredService<AdminAbacPolicyService>()
                    .RequireRegion(context, room.Summary.RegionId);
            }
            return room is null ? Results.NotFound() : Results.Ok(room);
        });
        api.MapGet("/instances", async (
            string? regionId,
            string? clusterId,
            string? nodeId,
            string? cursor,
            int? pageSize,
            HttpContext context,
            MonitoringAggregationService monitoring,
            IOptions<AdminOptions> options,
            CancellationToken cancellationToken) =>
        {
            RequireRole(context, AdminRoles.RoomViewer);
            context.RequestServices
                .GetRequiredService<AdminAbacPolicyService>()
                .RequireRegion(context, regionId);
            return Results.Ok(await monitoring.ListInstancesAsync(
                regionId,
                clusterId,
                nodeId,
                cursor,
                pageSize ?? options.Value.RealtimeCapacity.DefaultPageSize,
                cancellationToken));
        });
    }

    /// <summary>
    /// 证据包只允许案件双方和审计角色访问；目标与工单范围来自案件本身，客户端不能扩大。
    /// </summary>
    private static void RequireRole(HttpContext context, string role)
    {
        if (!AdminPrincipalContext.Get(context).HasRole(role))
            throw AdminOperationException.Forbidden($"缺少角色：{role}");
    }

    /// <summary>
    /// 来源健康同时服务房间与玩家监控；任一只读监控角色均可查看，但匿名或纯管理角色不可访问。
    /// </summary>
    private static void RequireAnyMonitoringRole(HttpContext context)
    {
        var principal = AdminPrincipalContext.Get(context);
        if (!principal.HasRole(AdminRoles.RoomViewer)
            && !principal.HasRole(AdminRoles.PlayerViewer))
        {
            throw AdminOperationException.Forbidden(
                $"缺少角色：{AdminRoles.RoomViewer} 或 {AdminRoles.PlayerViewer}");
        }
    }

    /// <summary>
    /// 建立带序列断点的 SSE 流；权限在每个事件发送前复核，断点超窗时要求客户端全量重同步。
    /// </summary>
    private static async Task StreamRealtimeEventsAsync(
        HttpContext context,
        AdminRealtimeEventHub hub,
        IOptions<AdminOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.RealtimeCapacity.SseEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var rawAfter = context.Request.Headers["Last-Event-ID"].ToString();
        if (string.IsNullOrWhiteSpace(rawAfter))
            rawAfter = context.Request.Query["after"].ToString();
        var hasAfter = !string.IsNullOrWhiteSpace(rawAfter);
        // 先初始化序列号，避免短路表达式跳过解析时留下未赋值的局部变量。
        var parsedSequence = 0L;
        var parsedCurrentInstance = hasAfter
            && hub.TryParseEventId(rawAfter, out parsedSequence);
        var afterSequence = parsedCurrentInstance
            ? parsedSequence
            : (long?)null;
        var principal = AdminPrincipalContext.Get(context);
        // 来自其他 Admin 副本的事件 ID 不可直接比较；立即要求重同步，避免负载均衡重连静默丢事件。
        var subscription = hub.Subscribe(
            afterSequence,
            hasAfter && !parsedCurrentInstance);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(cancellationToken);
        try
        {
            if (subscription.RequiresResync)
            {
                await WriteSseEventAsync(
                    context.Response,
                    hub.FormatEventId(subscription.CurrentSequence),
                    "resync",
                    new
                    {
                        reason = "EVENT_WINDOW_EXCEEDED",
                        currentSequence = subscription.CurrentSequence
                    },
                    cancellationToken);
            }
            else
            {
                foreach (var item in subscription.Backlog)
                    await WritePermittedEventAsync(
                        context.Response,
                        principal,
                        hub,
                        item,
                        cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var waitForData = subscription.Reader
                    .WaitToReadAsync(cancellationToken).AsTask();
                var heartbeat = Task.Delay(
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                if (await Task.WhenAny(waitForData, heartbeat) == heartbeat)
                {
                    await context.Response.WriteAsync(
                        $": heartbeat {DateTimeOffset.UtcNow:O}\n\n",
                        cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                    continue;
                }
                if (!await waitForData) break;
                while (subscription.Reader.TryRead(out var item))
                    await WritePermittedEventAsync(
                        context.Response,
                        principal,
                        hub,
                        item,
                        cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 浏览器关闭或网络断开是正常生命周期，finally 会释放有界订阅队列。
        }
        finally
        {
            subscription.Dispose();
        }
    }

    /// <summary>按 RBAC 过滤实体类别，防止玩家增量被发送给仅有房间查看权限的操作员。</summary>
    private static async Task WritePermittedEventAsync(
        HttpResponse response,
        AdminPrincipal principal,
        AdminRealtimeEventHub hub,
        AdminRealtimeEvent item,
        CancellationToken cancellationToken)
    {
        var permitted = item.EventType.StartsWith(
                "player.",
                StringComparison.Ordinal)
            ? principal.HasRole(AdminRoles.PlayerViewer)
            : principal.HasRole(AdminRoles.RoomViewer);
        if (!permitted) return;
        await WriteSseEventAsync(
            response,
            hub.FormatEventId(item.Sequence),
            item.EventType,
            new
            {
                entityKey = item.EntityKey,
                payload = item.Payload,
                item.OccurredAtUtc
            },
            cancellationToken);
    }

    /// <summary>写入单个 SSE 帧并刷新，确保客户端记录的 Last-Event-ID 对应已收到的数据。</summary>
    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventId,
        string eventType,
        object payload,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync(
            $"id: {eventId}\nevent: {eventType}\ndata: "
            + JsonSerializer.Serialize(payload)
            + "\n\n",
            cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}

