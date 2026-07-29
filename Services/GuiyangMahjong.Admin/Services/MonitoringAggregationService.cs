using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>
/// 聚合 Lobby 与 Allocator 的只读房间监控投影；任一来源失败时返回部分成功数据和来源健康信息。
/// </summary>
public sealed class MonitoringAggregationService(
    ILobbyMonitoringClient lobby,
    IAllocatorMonitoringClient allocator,
    IPlayerDataMonitoringClient playerData,
    MonitoringSourceReliabilityService reliability,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider)
{
    private readonly AdminOptions adminOptions = options.Value;

    /// <summary>
    /// 获取房间总览。Lobby 与 Allocator 并行且各自受独立硬超时约束，单个来源故障不会使总览失败。
    /// </summary>
    public async Task<MonitoringOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var loadedTask = LoadAsync(cancellationToken);
        var playerDataTask = ExecutePlayerDataAsync(cancellationToken);
        await Task.WhenAll(loadedTask, playerDataTask);
        var loaded = await loadedTask;
        var playerDataResult = await playerDataTask;
        var rooms = loaded.Rooms.Value;
        var instances = loaded.Instances.Value;
        var authHealth = reliability.GetHealth("Auth", adminOptions.Auth.Enabled);
        var instanceById = CreateInstanceIndex(instances);
        var active = rooms.Count(room =>
            room.Lifecycle is "Allocating" or "Waiting" or "Playing" or "Settling");
        var abnormal = rooms.Count(room =>
                room.Lifecycle == "Failed" || room.MarkedAbnormal)
            + instances.Count(item => item.Instance.State == "Failed");
        return new MonitoringOverview(
            timeProvider.GetUtcNow(),
            rooms.Count,
            active,
            abnormal,
            rooms.Where(room => room.Lifecycle is not "Closed" and not "Failed")
                .Sum(room => room.PlayerIds.Length),
            instances.Count,
            Group(rooms.Select(GetGameMode)),
            Group(rooms.Select(room => room.Lifecycle)),
            Group(rooms.Select(room =>
            {
                var id = GetInstanceId(room);
                return id is not null && instanceById.TryGetValue(id, out var instance)
                    ? instance.ClusterId
                    : "Unassigned";
            })),
            BuildMetadata(
                IsLiveOrDisabled(loaded.Rooms, adminOptions.Lobby.Enabled)
                && IsLiveOrDisabled(loaded.Instances, AllocatorEnabled)
                && (!authHealth.Enabled || authHealth.Status == "Healthy")
                && (!adminOptions.PlayerData.Enabled || playerDataResult.IsLive),
                loaded.Rooms.Health,
                loaded.Instances.Health,
                authHealth,
                playerDataResult.Health));
    }

    /// <summary>
    /// 查询房间列表；缺失 Allocator 时仍返回 Lobby 房间，只将集群、节点等来源字段保留为空。
    /// </summary>
    public Task<CursorPage<RoomListItem>> ListRoomsAsync(
        string? lifecycle,
        string? gameMode,
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        ListRoomsAsync(
            null,
            null,
            null,
            null,
            lifecycle,
            gameMode,
            search,
            cursor,
            pageSize,
            cancellationToken);

    public async Task<CursorPage<RoomListItem>> ListRoomsAsync(
        string? regionId,
        string? clusterId,
        string? lobbyId,
        string? nodeId,
        string? lifecycle,
        string? gameMode,
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var safePageSize = Math.Clamp(
            pageSize,
            1,
            adminOptions.RealtimeCapacity.MaximumPageSize);
        var filterIdentity = string.Join(
            '|',
            "rooms",
            regionId?.Trim().ToUpperInvariant() ?? string.Empty,
            clusterId?.Trim().ToUpperInvariant() ?? string.Empty,
            lobbyId?.Trim().ToUpperInvariant() ?? string.Empty,
            nodeId?.Trim().ToUpperInvariant() ?? string.Empty,
            lifecycle?.Trim().ToUpperInvariant() ?? string.Empty,
            gameMode?.Trim().ToUpperInvariant() ?? string.Empty,
            search?.Trim().ToUpperInvariant() ?? string.Empty);
        var upstreamCursor = MonitoringCursorPagination.UnwrapOpaqueCursor(
            cursor,
            filterIdentity);
        var roomsTask = ExecuteLobbyAsync(
            "rooms-page",
            token => lobby.ListRoomsTopologyPageAsync(
                regionId,
                clusterId,
                lobbyId,
                nodeId,
                lifecycle,
                gameMode,
                search,
                upstreamCursor,
                safePageSize,
                token),
            () => new CursorPage<RoomMonitorSnapshot>(
                [],
                null,
                false,
                safePageSize),
            false,
            cancellationToken);
        var instancesTask = ExecuteAllocatorAsync(cancellationToken);
        await Task.WhenAll(roomsTask, instancesTask);
        var roomPage = (await roomsTask).Value;
        var instances = (await instancesTask).Value;
        var instanceById = CreateInstanceIndex(instances);
        // Lobby 已在数据库分页前完成授权范围内筛选；Admin 只映射当前页并保留不透明游标。
        var items = roomPage.Items
            .Select(room => MapRoom(room, instanceById))
            .ToArray();
        return new CursorPage<RoomListItem>(
            items,
            roomPage.NextCursor is null
                ? null
                : MonitoringCursorPagination.WrapOpaqueCursor(
                    roomPage.NextCursor,
                    filterIdentity),
            roomPage.HasMore,
            roomPage.PageSize);
    }

    /// <summary>
    /// 为单例实时发布器生成容量受控的已脱敏房间快照；浏览器订阅不会各自触发该扫描。
    /// </summary>
    public async Task<IReadOnlyList<RoomListItem>> ListRoomsForRealtimeAsync(
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        var instanceById = CreateInstanceIndex(loaded.Instances.Value);
        return loaded.Rooms.Value
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenBy(item => item.RoomId, StringComparer.Ordinal)
            .Take(adminOptions.RealtimeCapacity.MaximumRooms)
            .Select(item => MapRoom(item, instanceById))
            .ToArray();
    }

    /// <summary>
    /// 获取房间详情；运行遥测与事件流独立降级，任一子调用失败不会遮蔽已有房间基础信息。
    /// </summary>
    public async Task<RoomDetail?> GetRoomAsync(
        string roomId,
        CancellationToken cancellationToken) =>
        await GetRoomCoreAsync(roomId, false, cancellationToken);

    /// <summary>
    /// 为高危管理工作流重新读取实时房间状态；任何相关来源为缓存或不可用时均拒绝继续。
    /// </summary>
    public async Task<RoomDetail?> GetRoomForActionAsync(
        string roomId,
        CancellationToken cancellationToken) =>
        await GetRoomCoreAsync(roomId, true, cancellationToken);

    private async Task<RoomDetail?> GetRoomCoreAsync(
        string roomId,
        bool requireLive,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        if (requireLive
            && (!IsLiveOrDisabled(loaded.Rooms, adminOptions.Lobby.Enabled)
                || !IsLiveOrDisabled(loaded.Instances, AllocatorEnabled)))
        {
            throw new MonitoringFreshDataRequiredException();
        }
        var rooms = loaded.Rooms.Value;
        var instances = loaded.Instances.Value;
        var room = rooms.FirstOrDefault(item =>
            item.RoomId.Equals(roomId, StringComparison.Ordinal)
            || item.RoomCode.Equals(roomId, StringComparison.Ordinal));
        if (room is null) return null;
        var instanceById = CreateInstanceIndex(instances);
        var serverId = GetInstanceId(room);
        var server = serverId is not null && instanceById.TryGetValue(serverId, out var found)
            ? found
            : null;
        var runtimeTask = ExecuteLobbyAsync(
            $"room-runtime:{room.RoomId}",
            token => lobby.GetRuntimeAsync(room.RoomId, token),
            () => null,
            true,
            cancellationToken);
        var timelineTask = ExecuteLobbyAsync(
            $"room-events:{room.RoomId}",
            token => lobby.ListEventsAsync(room.RoomId, token),
            () => [],
            true,
            cancellationToken);
        await Task.WhenAll(runtimeTask, timelineTask);
        var runtimeResult = await runtimeTask;
        var timelineResult = await timelineTask;
        if (requireLive && (!runtimeResult.IsLive || !timelineResult.IsLive))
            throw new MonitoringFreshDataRequiredException();
        var runtime = runtimeResult.Value;
        return new RoomDetail(
            MapRoom(room, instanceById),
            room.RuleSnapshot,
            room.OwnerPlayerId,
            room.PlayerIds,
            room.PublicRoom,
            room.AutoStart,
            room.NewPlayersProhibited,
            room.MaintenanceMode,
            room.MarkedAbnormal,
            server,
            runtime,
            timelineResult.Value,
            runtime is null
                ? runtimeResult.IsLive ? "AwaitingHeartbeat" : "Unavailable"
                : runtimeResult.IsLive ? "Realtime" : "Stale",
            BuildMetadata(
                loaded.Rooms.IsLive
                && loaded.Instances.IsLive
                && runtimeResult.IsLive
                && timelineResult.IsLive,
                loaded.Rooms.Health,
                loaded.Instances.Health,
                runtimeResult.Health,
                timelineResult.Health));
    }

    /// <summary>
    /// 获取 Dedicated Server 实例；Allocator 故障时返回未过期快照或安全空集合。
    /// </summary>
    public async Task<CursorPage<MonitoredInstance>> ListInstancesAsync(
        string? regionId,
        string? clusterId,
        string? nodeId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var instances = (await ListInstancesCoreAsync(false, cancellationToken))
            .Where(item =>
                Matches(item.RegionId, regionId)
                && Matches(item.ClusterId, clusterId)
                && Matches(item.NodeId, nodeId));
        var filterIdentity =
            $"instances:{regionId}:{clusterId}:{nodeId}";
        return MonitoringCursorPagination.CreatePage(
            instances,
            pageSize,
            adminOptions.RealtimeCapacity.MaximumPageSize,
            filterIdentity,
            item => item.Instance.StartedAtUtc,
            item => $"{item.ClusterId}/{item.NodeId}/{item.Instance.ServerInstanceId}",
            cursor);
    }

    /// <summary>为实时发布器返回实例快照，并强制执行跨集群实例容量上限。</summary>
    public async Task<IReadOnlyList<MonitoredInstance>>
        ListInstancesForRealtimeAsync(CancellationToken cancellationToken) =>
        (await ListInstancesCoreAsync(false, cancellationToken))
        .OrderByDescending(item => item.Instance.StartedAtUtc)
        .ThenBy(item => item.Instance.ServerInstanceId, StringComparer.Ordinal)
        .Take(adminOptions.RealtimeCapacity.MaximumInstances)
        .ToArray();

    /// <summary>
    /// 兼容旧调用方的无拓扑筛选实例分页入口。
    /// </summary>
    public Task<CursorPage<MonitoredInstance>> ListInstancesAsync(
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        ListInstancesAsync(null, null, null, cursor, pageSize, cancellationToken);

    /// <summary>
    /// 为高危服务器管理操作读取实时实例列表，缓存命中或熔断状态均视为冲突。
    /// </summary>
    public Task<IReadOnlyList<MonitoredInstance>> ListInstancesForActionAsync(
        CancellationToken cancellationToken) =>
        ListInstancesCoreAsync(true, cancellationToken);

    /// <summary>
    /// 返回四个监控来源最近状态，供管理台健康卡片独立刷新；该方法本身不触发新网络请求。
    /// </summary>
    public MonitoringReliabilityMetadata GetReliabilityMetadata()
    {
        var health = new[]
        {
            reliability.GetHealth("Lobby", adminOptions.Lobby.Enabled),
            reliability.GetHealth("Allocator", AllocatorEnabled),
            reliability.GetHealth("Auth", adminOptions.Auth.Enabled),
            reliability.GetHealth("PlayerData", adminOptions.PlayerData.Enabled)
        };
        var safe = health
            .Where(item => item.Enabled)
            .All(item => item.Status == "Healthy");
        return BuildMetadata(safe, health);
    }

    private async Task<IReadOnlyList<MonitoredInstance>> ListInstancesCoreAsync(
        bool requireLive,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAllocatorAsync(cancellationToken);
        if (requireLive && !IsLiveOrDisabled(result, AllocatorEnabled))
            throw new MonitoringFreshDataRequiredException();
        return result.Value;
    }

    private async Task<LoadedMonitoringSources> LoadAsync(
        CancellationToken cancellationToken)
    {
        var roomsTask = ExecuteLobbyAsync(
            "rooms",
            lobby.ListRoomsAsync,
            () => Array.Empty<RoomMonitorSnapshot>(),
            true,
            cancellationToken);
        var instancesTask = ExecuteAllocatorAsync(cancellationToken);
        await Task.WhenAll(roomsTask, instancesTask);
        return new LoadedMonitoringSources(await roomsTask, await instancesTask);
    }

    private Task<MonitoringSourceResult<T>> ExecuteLobbyAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> operationFactory,
        Func<T> emptyFactory,
        bool cacheSnapshot,
        CancellationToken cancellationToken) =>
        reliability.ExecuteAsync(
            "Lobby",
            operation,
            adminOptions.Lobby.Enabled,
            TimeSpan.FromSeconds(adminOptions.Lobby.TimeoutSeconds),
            operationFactory,
            emptyFactory,
            cacheSnapshot,
            cancellationToken);

    private Task<MonitoringSourceResult<IReadOnlyList<MonitoredInstance>>>
        ExecuteAllocatorAsync(CancellationToken cancellationToken)
    {
        var timeoutSeconds = adminOptions.Allocators
            .Where(item => item.Enabled)
            .Select(item => item.TimeoutSeconds)
            .DefaultIfEmpty(5)
            .Max();
        return reliability.ExecuteAsync(
            "Allocator",
            "instances",
            AllocatorEnabled,
            TimeSpan.FromSeconds(timeoutSeconds),
            allocator.ListInstancesAsync,
            () => Array.Empty<MonitoredInstance>(),
            true,
            cancellationToken);
    }

    private Task<MonitoringSourceResult<bool>> ExecutePlayerDataAsync(
        CancellationToken cancellationToken) =>
        reliability.ExecuteAsync(
            "PlayerData",
            "readiness",
            adminOptions.PlayerData.Enabled,
            TimeSpan.FromSeconds(adminOptions.PlayerData.TimeoutSeconds),
            playerData.CheckReadyAsync,
            () => false,
            false,
            cancellationToken);

    private bool AllocatorEnabled =>
        adminOptions.Allocators.Any(item => item.Enabled);

    private static bool IsLiveOrDisabled<T>(
        MonitoringSourceResult<T> result,
        bool enabled) =>
        !enabled || result.IsLive;

    private MonitoringReliabilityMetadata BuildMetadata(
        bool safeForHighRiskActions,
        params MonitoringSourceHealth[] health)
    {
        var merged = health
            .GroupBy(item => item.Source, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => HealthSeverity(item.Status))
                .ThenByDescending(item => item.ObservedAtUtc)
                .First())
            .OrderBy(item => item.Source, StringComparer.Ordinal)
            .ToArray();
        return new MonitoringReliabilityMetadata(
            timeProvider.GetUtcNow(),
            merged.Any(item => item.Enabled && item.Status != "Healthy"),
            safeForHighRiskActions,
            merged);
    }

    private static int HealthSeverity(string status) => status switch
    {
        "Unavailable" => 4,
        "Stale" => 3,
        "Degraded" => 2,
        _ => 1
    };

    private sealed record LoadedMonitoringSources(
        MonitoringSourceResult<IReadOnlyList<RoomMonitorSnapshot>> Rooms,
        MonitoringSourceResult<IReadOnlyList<MonitoredInstance>> Instances);

    private static CountGroup[] Group(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CountGroup(group.Key, group.Count()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 构造跨集群实例索引；旧版实例标识冲突时按来源标识稳定选取，避免聚合进程异常并保证分页结果可重复。
    /// 注册中心已负责隔离同一路由冲突，此处仍保留防御性去重以兼容遗留静态来源。
    /// </summary>
    private static IReadOnlyDictionary<string, MonitoredInstance>
        CreateInstanceIndex(IEnumerable<MonitoredInstance> instances) =>
        instances
            .GroupBy(
                item => item.Instance.ServerInstanceId,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(
                        item => item.SourceId ?? string.Empty,
                        StringComparer.Ordinal)
                    .ThenBy(item => item.ClusterId, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);

    private static RoomListItem MapRoom(
        RoomMonitorSnapshot room,
        IReadOnlyDictionary<string, MonitoredInstance> instances)
    {
        var serverId = GetInstanceId(room);
        instances.TryGetValue(serverId ?? string.Empty, out var server);
        return new RoomListItem(
            room.RoomId,
            room.RoomCode,
            room.MatchId,
            GetGameMode(room),
            room.Lifecycle,
            room.PlayerIds.Length,
            room.MaximumPlayers,
            GetInt32(room.RuleSnapshot, "currentRound"),
            room.RoundCount,
            server?.ClusterId,
            server?.NodeId,
            serverId,
            room.CreatedAtUtc,
            room.UpdatedAtUtc,
            room.StateSequence,
            room.RegionId,
            room.LobbyId,
            room.SourceId);
    }

    private static string? GetInstanceId(RoomMonitorSnapshot room) =>
        room.Route?.ServerInstanceId ?? room.PendingServerInstanceId ?? room.LastServerInstanceId;

    private static string GetGameMode(RoomMonitorSnapshot room)
    {
        foreach (var key in new[] { "gameMode", "playMode", "variant" })
        {
            if (room.RuleSnapshot.TryGetValue(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "Standard";
            }
        }
        return "Standard";
    }

    private static int GetInt32(
        IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static bool Matches(string actual, string? requested) =>
        string.IsNullOrWhiteSpace(requested)
        || actual.Equals(requested.Trim(), StringComparison.OrdinalIgnoreCase);
}
