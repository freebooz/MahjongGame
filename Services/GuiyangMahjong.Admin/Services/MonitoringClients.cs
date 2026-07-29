using System.Net.Http.Headers;
using System.Net.Http.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

public interface ILobbyMonitoringClient
{
    Task<IReadOnlyList<RoomMonitorSnapshot>> ListRoomsAsync(CancellationToken cancellationToken);
    /// <summary>按 Lobby 原生键集游标读取一页房间，筛选在数据库分页前完成。</summary>
    Task<CursorPage<RoomMonitorSnapshot>> ListRoomsPageAsync(
        string? lifecycle,
        string? gameMode,
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);
    /// <summary>
    /// 按拓扑范围读取房间；默认实现保持旧客户端兼容，HTTP 实现会在访问来源前完成筛选。
    /// </summary>
    Task<CursorPage<RoomMonitorSnapshot>> ListRoomsTopologyPageAsync(
        string? regionId,
        string? clusterId,
        string? lobbyId,
        string? nodeId,
        string? lifecycle,
        string? gameMode,
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken) =>
        ListRoomsPageAsync(
            lifecycle,
            gameMode,
            search,
            cursor,
            pageSize,
            cancellationToken);
    Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken);
    Task<RoomTimelineEvent[]> ListEventsAsync(
        string roomId, CancellationToken cancellationToken);
    Task<PlayerPresenceSnapshot[]> GetPlayerPresenceAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken);
    /// <summary>读取 Lobby 持久玩家房间历史，不从当前活动房间反推。</summary>
    Task<PlayerHistoryPage<PlayerRoomHistoryRecord>> ListPlayerRoomHistoryAsync(
        string playerId,
        int pageSize,
        DateTimeOffset? beforeAtUtc,
        string? beforeRoomId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PlayerHistoryPage<PlayerRoomHistoryRecord>(
            [], null, null));
    /// <summary>读取玩家连接状态权威事件，供掉线、恢复和托管调查使用。</summary>
    Task<PlayerHistoryPage<PlayerConnectionHistoryRecord>>
        ListPlayerConnectionHistoryAsync(
            string playerId,
            int pageSize,
            DateTimeOffset? beforeAtUtc,
            string? beforeEventId,
            CancellationToken cancellationToken) =>
        Task.FromResult(new PlayerHistoryPage<PlayerConnectionHistoryRecord>(
            [], null, null));
}

public interface IAllocatorMonitoringClient
{
    Task<IReadOnlyList<MonitoredInstance>> ListInstancesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// PlayerData 只读健康探测客户端；仅验证监控依赖可达性，不读取资产、支付或聊天证据。
/// </summary>
public interface IPlayerDataMonitoringClient
{
    /// <summary>
    /// 调用 PlayerData 就绪端点；失败由统一可靠性边界转换为受控来源状态。
    /// </summary>
    Task<bool> CheckReadyAsync(CancellationToken cancellationToken);
}

public sealed class HttpLobbyMonitoringClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options,
    TopologyRegistry topologyRegistry) : ILobbyMonitoringClient
{
    public async Task<IReadOnlyList<RoomMonitorSnapshot>> ListRoomsAsync(
        CancellationToken cancellationToken)
    {
        var sources = GetLobbySources();
        if (sources.Length == 0) return [];
        var capacity = options.Value.RealtimeCapacity;
        var result = new List<RoomMonitorSnapshot>(
            Math.Min(capacity.MaximumRooms, 1024));
        foreach (var source in sources)
        {
            var remaining = capacity.MaximumRooms - result.Count;
            if (remaining <= 0) break;
            result.AddRange(await ListSourceRoomsAsync(
                source,
                null,
                null,
                null,
                remaining,
                cancellationToken));
        }
        return result
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenBy(item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.RoomId, StringComparer.Ordinal)
            .Take(capacity.MaximumRooms)
            .ToArray();
    }

    /// <summary>遍历单一 Lobby 的原生游标；单地域失败由上层来源可靠性边界隔离。</summary>
    private async Task<IReadOnlyList<RoomMonitorSnapshot>> ListSourceRoomsAsync(
        LobbyMonitoringOptions source,
        string? lifecycle,
        string? gameMode,
        string? search,
        int maximumItems,
        CancellationToken cancellationToken)
    {
        var capacity = options.Value.RealtimeCapacity;
        var result = new List<RoomMonitorSnapshot>(
            Math.Min(maximumItems, 1024));
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var query = new List<string>
            {
                $"pageSize={capacity.MaximumPageSize}"
            };
            if (cursor is not null)
                query.Add($"cursor={Uri.EscapeDataString(cursor)}");
            if (!string.IsNullOrWhiteSpace(lifecycle))
                query.Add($"lifecycle={Uri.EscapeDataString(lifecycle)}");
            if (!string.IsNullOrWhiteSpace(gameMode))
                query.Add($"gameMode={Uri.EscapeDataString(gameMode)}");
            if (!string.IsNullOrWhiteSpace(search))
                query.Add($"search={Uri.EscapeDataString(search)}");
            using var response = await GetAsync(
                "/internal/monitoring/rooms?" + string.Join('&', query),
                source,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = await response.Content
                .ReadFromJsonAsync<CursorPage<RoomMonitorSnapshot>>(
                    cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException(
                    "Lobby room page response is empty.");
            result.AddRange(page.Items
                .Select(item => WithSource(item, source))
                .Take(maximumItems - result.Count));
            cursor = page.NextCursor;
            if (result.Count >= maximumItems) break;
            if (cursor is not null && !seenCursors.Add(cursor))
                throw new InvalidOperationException(
                    "Lobby returned a repeated room cursor.");
        }
        while (cursor is not null);
        return result;
    }

    /// <summary>
    /// 将筛选和不透明游标透传给 Lobby；Admin 仅映射当前页，避免每个浏览器请求扫描一万房间。
    /// </summary>
    public async Task<CursorPage<RoomMonitorSnapshot>> ListRoomsPageAsync(
        string? lifecycle,
        string? gameMode,
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
        => await ListRoomsTopologyPageAsync(
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

    public async Task<CursorPage<RoomMonitorSnapshot>> ListRoomsTopologyPageAsync(
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
        var sources = GetLobbySources()
            .Where(source =>
                Matches(source.RegionId, regionId)
                && Matches(source.ClusterId, clusterId)
                && Matches(source.LobbyId, lobbyId)
                && Matches(source.NodeId, nodeId))
            .ToArray();
        var safePageSize = Math.Clamp(
            pageSize,
            1,
            options.Value.RealtimeCapacity.MaximumPageSize);
        if (sources.Length == 0)
            return new CursorPage<RoomMonitorSnapshot>(
                [],
                null,
                false,
                safePageSize);
        if (sources.Length > 1)
        {
            var topologyIdentity = string.Join(
                ',',
                sources.Select(item => item.SourceId));
            var all = new List<RoomMonitorSnapshot>();
            foreach (var source in sources)
            {
                all.AddRange(await ListSourceRoomsAsync(
                    source,
                    lifecycle,
                    gameMode,
                    search,
                    options.Value.RealtimeCapacity.MaximumRooms,
                    cancellationToken));
            }
            return MonitoringCursorPagination.CreatePage(
                all,
                safePageSize,
                options.Value.RealtimeCapacity.MaximumPageSize,
                $"rooms:{topologyIdentity}:{regionId}:{clusterId}:{lobbyId}:{nodeId}:{lifecycle}:{gameMode}:{search}",
                item => item.CreatedAtUtc,
                item => $"{item.SourceId}/{item.RoomId}",
                cursor);
        }
        var singleSource = sources[0];
        var query = new List<string>
        {
            $"pageSize={safePageSize}"
        };
        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (!string.IsNullOrWhiteSpace(lifecycle))
            query.Add($"lifecycle={Uri.EscapeDataString(lifecycle.Trim())}");
        if (!string.IsNullOrWhiteSpace(gameMode))
            query.Add($"gameMode={Uri.EscapeDataString(gameMode.Trim())}");
        if (!string.IsNullOrWhiteSpace(search))
            query.Add($"search={Uri.EscapeDataString(search.Trim())}");
        using var response = await GetAsync(
            "/internal/monitoring/rooms?" + string.Join('&', query),
            singleSource,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var page = await response.Content
            .ReadFromJsonAsync<CursorPage<RoomMonitorSnapshot>>(
                cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Lobby room page response is empty.");
        return page with
        {
            Items = page.Items.Select(item => WithSource(item, singleSource)).ToArray()
        };
    }

    public async Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken)
    {
        foreach (var source in GetLobbySources())
        {
            using var response = await GetAsync(
                $"/internal/monitoring/rooms/{Uri.EscapeDataString(roomId)}/runtime",
                source,
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<RoomRuntimeTelemetry>(
                cancellationToken: cancellationToken);
        }
        return null;
    }

    public async Task<RoomTimelineEvent[]> ListEventsAsync(
        string roomId, CancellationToken cancellationToken)
    {
        foreach (var source in GetLobbySources())
        {
            using var response = await GetAsync(
                $"/internal/monitoring/rooms/{Uri.EscapeDataString(roomId)}/events?limit=200",
                source,
                cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            var events = await response.Content.ReadFromJsonAsync<RoomTimelineEvent[]>(
                cancellationToken: cancellationToken) ?? [];
            if (events.Length > 0) return events;
        }
        return [];
    }

    public async Task<PlayerPresenceSnapshot[]> GetPlayerPresenceAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken)
    {
        var sources = GetLobbySources();
        if (sources.Length == 0 || playerIds.Count == 0) return [];
        var joined = Uri.EscapeDataString(string.Join(',', playerIds.Take(500)));
        var requests = sources.Select(async source =>
        {
            using var response = await GetAsync(
                $"/internal/monitoring/player-presence?playerIds={joined}",
                source,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content
                .ReadFromJsonAsync<PlayerPresenceSnapshot[]>(
                    cancellationToken: cancellationToken) ?? [];
        });
        var values = (await Task.WhenAll(requests)).SelectMany(item => item);
        // 同一玩家短暂出现在多个 Lobby 时，最新在线观测优先，随后按 LobbyId 确定性裁决。
        return values
            .GroupBy(item => item.PlayerId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Online)
                .ThenByDescending(item => item.LastSeenAtUtc)
                .ThenBy(item => item.LobbyId, StringComparer.Ordinal)
                .First())
            .ToArray();
    }

    public Task<PlayerHistoryPage<PlayerRoomHistoryRecord>>
        ListPlayerRoomHistoryAsync(
            string playerId,
            int pageSize,
            DateTimeOffset? beforeAtUtc,
            string? beforeRoomId,
            CancellationToken cancellationToken) =>
        GetPlayerHistoryAsync<PlayerRoomHistoryRecord>(
            $"/internal/monitoring/players/{Uri.EscapeDataString(playerId)}/room-history",
            pageSize,
            beforeAtUtc,
            beforeRoomId,
            "beforeRoomId",
            cancellationToken);

    public Task<PlayerHistoryPage<PlayerConnectionHistoryRecord>>
        ListPlayerConnectionHistoryAsync(
            string playerId,
            int pageSize,
            DateTimeOffset? beforeAtUtc,
            string? beforeEventId,
            CancellationToken cancellationToken) =>
        GetPlayerHistoryAsync<PlayerConnectionHistoryRecord>(
            $"/internal/monitoring/players/{Uri.EscapeDataString(playerId)}/connection-history",
            pageSize,
            beforeAtUtc,
            beforeEventId,
            "beforeEventId",
            cancellationToken);

    /// <summary>
    /// 透传稳定历史边界；边界标识只作为查询参数，不写入日志或业务遥测。
    /// </summary>
    private async Task<PlayerHistoryPage<T>> GetPlayerHistoryAsync<T>(
        string path,
        int pageSize,
        DateTimeOffset? beforeAtUtc,
        string? beforeId,
        string beforeIdName,
        CancellationToken cancellationToken)
    {
        var sources = GetLobbySources();
        if (sources.Length == 0)
            return new PlayerHistoryPage<T>([], null, null);
        var query = new List<string>
        {
            $"pageSize={Math.Clamp(pageSize, 1, 200)}"
        };
        if (beforeAtUtc.HasValue)
            query.Add($"beforeAtUtc={Uri.EscapeDataString(beforeAtUtc.Value.ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(beforeId))
            query.Add($"{beforeIdName}={Uri.EscapeDataString(beforeId)}");
        foreach (var source in sources)
        {
            using var response = await GetAsync(
                path + "?" + string.Join('&', query),
                source,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = await response.Content
                .ReadFromJsonAsync<PlayerHistoryPage<T>>(
                    cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException(
                    "Lobby player history response is empty.");
            if (page.Items.Length > 0) return page;
        }
        return new PlayerHistoryPage<T>([], null, null);
    }

    private async Task<HttpResponseMessage> GetAsync(
        string path,
        LobbyMonitoringOptions source,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{source.BaseUrl.TrimEnd('/')}{path}");
        AddHeaders(request, source.MonitoringToken);
        return await httpClientFactory.CreateClient(nameof(HttpLobbyMonitoringClient))
            .SendAsync(request, cancellationToken);
    }

    private LobbyMonitoringOptions[] GetLobbySources()
    {
        var sources = new List<LobbyMonitoringOptions>();
        if (options.Value.Lobby.Enabled) sources.Add(options.Value.Lobby);
        var discovery = options.Value.TopologyDiscovery;
        if (discovery.Enabled)
        {
            sources.AddRange(topologyRegistry
                .ListActive(MonitoringSourceKind.Lobby)
                .Select(lease =>
                {
                    var source = lease.Registration;
                    return new LobbyMonitoringOptions
                    {
                        Enabled = true,
                        SourceId = source.SourceId,
                        RegionId = source.RegionId,
                        ClusterId = source.ClusterId,
                        LobbyId = source.LobbyId,
                        NodeId = source.NodeId,
                        BaseUrl = source.BaseUrl,
                        MonitoringToken = discovery.LobbyMonitoringToken,
                        TimeoutSeconds = discovery.TimeoutSeconds
                    };
                }));
        }
        return sources
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(item => item.RegionId, StringComparer.Ordinal)
            .ThenBy(item => item.ClusterId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static RoomMonitorSnapshot WithSource(
        RoomMonitorSnapshot room,
        LobbyMonitoringOptions source) =>
        room with
        {
            RegionId = source.RegionId,
            ClusterId = source.ClusterId,
            LobbyId = source.LobbyId,
            NodeId = source.NodeId,
            SourceId = source.SourceId
        };

    private static bool Matches(string actual, string? requested) =>
        string.IsNullOrWhiteSpace(requested)
        || actual.Equals(requested.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void AddHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        request.Headers.Add(
            "X-Trace-Id",
            MahjongTelemetry.CurrentBusinessTraceId);
    }
}

public sealed class HttpAllocatorMonitoringClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options,
    TopologyRegistry topologyRegistry) : IAllocatorMonitoringClient
{
    public async Task<IReadOnlyList<MonitoredInstance>> ListInstancesAsync(
        CancellationToken cancellationToken)
    {
        var sources = options.Value.Allocators
            .Where(source => source.Enabled)
            .Concat(GetDynamicSources())
            .GroupBy(source => source.SourceId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        var tasks = sources.Select(source =>
            ListSourceAsync(source, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.SelectMany(item => item).ToArray();
    }

    private async Task<IReadOnlyList<MonitoredInstance>> ListSourceAsync(
        AllocatorMonitoringOptions source,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{source.BaseUrl.TrimEnd('/')}/internal/instances");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", source.MonitoringToken);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        request.Headers.Add(
            "X-Trace-Id",
            MahjongTelemetry.CurrentBusinessTraceId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds));
        using var response = await httpClientFactory.CreateClient(nameof(HttpAllocatorMonitoringClient))
            .SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var instances = await response.Content.ReadFromJsonAsync<GameServerInstanceSnapshot[]>(
            cancellationToken: timeout.Token) ?? [];
        return instances.Select(instance =>
            new MonitoredInstance(
                source.ClusterId,
                source.NodeId,
                instance,
                source.RegionId,
                source.SourceId)).ToArray();
    }

    /// <summary>
    /// 动态来源只使用 Admin 预配置的只读凭据；注册请求不能注入凭据或管理命令地址。
    /// </summary>
    private IEnumerable<AllocatorMonitoringOptions> GetDynamicSources()
    {
        var discovery = options.Value.TopologyDiscovery;
        if (!discovery.Enabled) yield break;
        foreach (var lease in topologyRegistry.ListActive(
                     MonitoringSourceKind.Allocator))
        {
            var source = lease.Registration;
            yield return new AllocatorMonitoringOptions
            {
                Enabled = true,
                SourceId = source.SourceId,
                RegionId = source.RegionId,
                ClusterId = source.ClusterId,
                NodeId = source.NodeId,
                BaseUrl = source.BaseUrl,
                MonitoringToken = discovery.AllocatorMonitoringToken,
                TimeoutSeconds = discovery.TimeoutSeconds
            };
        }
    }
}

/// <summary>
/// 使用独立监控配置探测 PlayerData，不复用具有写权限的 Wallet 命令凭据。
/// </summary>
public sealed class HttpPlayerDataMonitoringClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : IPlayerDataMonitoringClient
{
    /// <summary>
    /// 检查 PlayerData 就绪状态；完整请求由外层可靠性服务施加硬超时与取消传播。
    /// </summary>
    public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken)
    {
        var source = options.Value.PlayerData;
        if (!source.Enabled) return false;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{source.BaseUrl.TrimEnd('/')}/internal/monitoring/health");
        if (!string.IsNullOrWhiteSpace(source.MonitoringToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", source.MonitoringToken);
        }
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        request.Headers.Add(
            "X-Trace-Id",
            MahjongTelemetry.CurrentBusinessTraceId);
        using var response = await httpClientFactory
            .CreateClient(nameof(HttpPlayerDataMonitoringClient))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return true;
    }
}
