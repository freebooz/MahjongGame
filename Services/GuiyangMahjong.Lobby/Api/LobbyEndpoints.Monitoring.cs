using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Services;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Api;

/// <summary>
/// Admin 聚合器使用的 Lobby 只读监控分区。
/// 所有端点要求独立监控凭据，并限制分页、筛选和批量玩家数量。
/// </summary>
public static partial class LobbyEndpoints
{
    /// <summary>注册房间、事件、玩家历史和在线状态监控查询端点。</summary>
    private static void MapMonitoringEndpoints(WebApplication app)
    {
        app.MapGet("/internal/monitoring/rooms", async (
            HttpContext context,
            ILobbyStore store,
            IOptions<LobbyOptions> options,
            string? cursor,
            int? pageSize,
            int? limit,
            string? lifecycle,
            string? gameMode,
            string? search,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32
                || !HasInternalCredential(context, monitoringToken))
            {
                return Results.Unauthorized();
            }
            var filterFingerprint = CreateMonitoringFilterFingerprint(
                lifecycle,
                gameMode,
                search);
            if (!TryReadMonitoringCursor(
                    cursor,
                    filterFingerprint,
                    out var afterCreatedAtUtc,
                    out var afterRoomId))
            {
                return Results.BadRequest(new
                {
                    code = "INVALID_CURSOR",
                    message = "Room cursor is invalid."
                });
            }
            var safePageSize = Math.Clamp(pageSize ?? limit ?? 100, 1, 200);
            if (limit.HasValue)
                context.Response.Headers["Deprecation"] = "true";
            var loaded = await store.ListRoomsForMonitoringAsync(
                safePageSize + 1,
                afterCreatedAtUtc,
                afterRoomId,
                lifecycle,
                gameMode,
                search,
                cancellationToken);
            var items = loaded.Take(safePageSize).ToArray();
            var nextCursor = loaded.Count > safePageSize && items.Length > 0
                ? WriteMonitoringCursor(
                    items[^1].CreatedAtUtc,
                    items[^1].RoomId,
                    filterFingerprint)
                : null;
            // 滚动升级窗口为旧 Admin 保留数组形状，同时强制缩小巨页；新客户端使用 pageSize/cursor。
            if (limit.HasValue
                && pageSize is null
                && string.IsNullOrWhiteSpace(cursor))
                return Results.Ok(items);
            return Results.Ok(new
            {
                items,
                nextCursor,
                hasMore = nextCursor is not null,
                pageSize = safePageSize
            });
        });

        app.MapGet("/internal/monitoring/rooms/{roomId}/runtime", async (
            string roomId,
            HttpContext context,
            IRoomMonitoringStore monitoring,
            IOptions<LobbyOptions> options,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32
                || !HasInternalCredential(context, monitoringToken))
                return Results.Unauthorized();
            var runtime =
                await monitoring.GetRuntimeAsync(roomId, cancellationToken);
            return runtime is null
                ? Results.NotFound()
                : Results.Ok(runtime);
        });

        app.MapGet("/internal/monitoring/rooms/{roomId}/events", async (
            string roomId,
            HttpContext context,
            IRoomMonitoringStore monitoring,
            IOptions<LobbyOptions> options,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32
                || !HasInternalCredential(context, monitoringToken))
                return Results.Unauthorized();
            return Results.Ok(await monitoring.ListEventsAsync(
                roomId,
                Math.Clamp(limit ?? 200, 1, 500),
                cancellationToken));
        });

        app.MapGet(
            "/internal/monitoring/players/{playerId}/room-history",
            async (
                string playerId,
                HttpContext context,
                IPlayerHistoryStore historyStore,
                IOptions<LobbyOptions> options,
                int? pageSize,
                DateTimeOffset? beforeAtUtc,
                string? beforeRoomId,
                CancellationToken cancellationToken) =>
            {
                if (!HasInternalCredential(
                        context,
                        options.Value.MonitoringReadOnlyToken))
                    return Results.Unauthorized();
                if (playerId.Length is < 1 or > 80)
                    return Results.BadRequest();
                return Results.Ok(await historyStore.ListRoomsAsync(
                    playerId,
                    Math.Clamp(pageSize ?? 100, 1, 200),
                    beforeAtUtc,
                    beforeRoomId,
                    cancellationToken));
            });

        app.MapGet(
            "/internal/monitoring/players/{playerId}/connection-history",
            async (
                string playerId,
                HttpContext context,
                IPlayerHistoryStore historyStore,
                IOptions<LobbyOptions> options,
                int? pageSize,
                DateTimeOffset? beforeAtUtc,
                string? beforeEventId,
                CancellationToken cancellationToken) =>
            {
                if (!HasInternalCredential(
                        context,
                        options.Value.MonitoringReadOnlyToken))
                    return Results.Unauthorized();
                if (playerId.Length is < 1 or > 80
                    || (beforeEventId is not null
                        && !Guid.TryParse(beforeEventId, out _)))
                    return Results.BadRequest();
                return Results.Ok(await historyStore.ListConnectionsAsync(
                    playerId,
                    Math.Clamp(pageSize ?? 100, 1, 200),
                    beforeAtUtc,
                    beforeEventId,
                    cancellationToken));
            });

        app.MapGet("/internal/monitoring/player-presence", async (
            HttpContext context,
            IOnlinePresenceService presence,
            ILobbyStore store,
            IOptions<LobbyOptions> options,
            string? playerIds,
            CancellationToken cancellationToken) =>
        {
            var monitoringToken = options.Value.MonitoringReadOnlyToken;
            if (monitoringToken.Length < 32
                || !HasInternalCredential(context, monitoringToken))
                return Results.Unauthorized();
            var ids = (playerIds ?? string.Empty)
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(500)
                .ToArray();
            if (ids.Any(playerId => playerId.Length is < 1 or > 80))
                return Results.BadRequest();
            var snapshots =
                await presence.GetPlayersAsync(ids, cancellationToken);
            var activeRooms = await store.GetActiveRoomsByPlayersAsync(
                ids,
                cancellationToken);
            // 将活动房间上下文并入同一个批量响应，使 Admin 无需为一页玩家扫描全部房间。
            return Results.Ok(snapshots.Select(snapshot =>
            {
                var room = activeRooms.GetValueOrDefault(snapshot.PlayerId);
                return snapshot with
                {
                    RoomId = room?.RoomId,
                    RoomCode = room?.RoomCode,
                    ServerInstanceId = room?.Route?.ServerInstanceId
                        ?? room?.PendingServerInstanceId
                        ?? room?.LastServerInstanceId
                };
            }).ToArray());
        });
    }
}
