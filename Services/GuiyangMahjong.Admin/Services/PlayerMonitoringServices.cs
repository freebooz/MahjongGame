using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using GuiyangMahjong.Admin.Options;
using GuiyangMahjong.Observability;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Admin.Services;

/// <summary>Admin 读取 Auth 脱敏玩家目录和详情的只读客户端边界。</summary>
public interface IPlayerDirectoryClient
{
    /// <summary>读取一页已脱敏玩家目录；游标由 Auth 生成并绑定搜索条件。</summary>
    Task<CursorPage<AuthPlayerDirectoryItem>> ListPlayersPageAsync(
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>读取玩家身份、会话、登录、设备和控制历史；不存在返回空。</summary>
    Task<AuthPlayerDirectoryDetail?> GetPlayerAsync(
        string playerId, CancellationToken cancellationToken);
}

/// <summary>
/// 使用 Auth 只读监控凭据的玩家目录 HTTP 客户端。
/// 透传绑定搜索条件的键集游标并施加硬超时，不接触刷新令牌哈希或管理写接口。
/// </summary>
public sealed class HttpPlayerDirectoryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AdminOptions> options) : IPlayerDirectoryClient
{
    /// <inheritdoc/>
    public async Task<CursorPage<AuthPlayerDirectoryItem>> ListPlayersPageAsync(
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var source = options.Value.Auth;
        if (!source.Enabled)
            return new CursorPage<AuthPlayerDirectoryItem>([], null, false, pageSize);
        var query = Uri.EscapeDataString(search?.Trim() ?? string.Empty);
        using var response = await SendAsync(
            $"/internal/monitoring/players?pageSize={pageSize}&search={query}"
            + (string.IsNullOrWhiteSpace(cursor)
                ? string.Empty
                : $"&cursor={Uri.EscapeDataString(cursor)}"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<CursorPage<AuthPlayerDirectoryItem>>(
                cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Auth player page response is empty.");
    }

    /// <inheritdoc/>
    public async Task<AuthPlayerDirectoryDetail?> GetPlayerAsync(
        string playerId, CancellationToken cancellationToken)
    {
        if (!options.Value.Auth.Enabled) return null;
        using var response = await SendAsync(
            $"/internal/monitoring/players/{Uri.EscapeDataString(playerId)}",
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthPlayerDirectoryDetail>(
            cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path, CancellationToken cancellationToken)
    {
        var source = options.Value.Auth;
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{source.BaseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", source.MonitoringToken);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString());
        request.Headers.Add(
            "X-Trace-Id",
            MahjongTelemetry.CurrentBusinessTraceId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(source.TimeoutSeconds));
        return await httpClientFactory.CreateClient(nameof(HttpPlayerDirectoryClient))
            .SendAsync(request, timeout.Token);
    }
}

/// <summary>
/// 聚合 Auth 玩家目录与 Lobby 在线/房间状态；来源失败时保留可用字段并附带可靠性元数据。
/// </summary>
public sealed class PlayerMonitoringService(
    IPlayerDirectoryClient players,
    ILobbyMonitoringClient lobby,
    MonitoringSourceReliabilityService reliability,
    IOptions<AdminOptions> options,
    TimeProvider timeProvider)
{
    private readonly AdminOptions adminOptions = options.Value;

    /// <summary>
    /// 查询玩家列表。Auth 与 Lobby 各自受独立超时控制，Lobby 故障不会隐藏 Auth 账号基础资料。
    /// </summary>
    public async Task<CursorPage<PlayerMonitorListItem>> ListPlayersAsync(
        string? search,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var normalizedSearch = search?.Trim() ?? string.Empty;
        var directoryTask = ExecuteAuthAsync(
            $"players:{HashKey(normalizedSearch)}",
            token => players.ListPlayersPageAsync(
                normalizedSearch,
                cursor,
                Math.Clamp(
                    pageSize,
                    1,
                    adminOptions.RealtimeCapacity.MaximumPageSize),
                token),
            () => new CursorPage<AuthPlayerDirectoryItem>(
                [],
                null,
                false,
                pageSize),
            true,
            cancellationToken);
        var directoryResult = await directoryTask;
        var directoryPage = directoryResult.Value;
        var directory = directoryPage.Items;
        var presenceTask = ExecuteLobbyAsync(
            $"player-presence:{HashKey(string.Join(
                '\n',
                directory.Select(item => item.PlayerId).Order(StringComparer.Ordinal)))}",
            token => lobby.GetPlayerPresenceAsync(
                directory.Select(item => item.PlayerId).ToArray(), token),
            () => [],
            true,
            cancellationToken);
        var presenceResult = await presenceTask;
        var presence = presenceResult.Value.ToDictionary(
            item => item.PlayerId, StringComparer.Ordinal);
        var mapped = directory.Select(player =>
            MapPlayer(player, presence.GetValueOrDefault(player.PlayerId),
                null, null))
            .ToArray();
        return new CursorPage<PlayerMonitorListItem>(
            mapped,
            directoryPage.NextCursor,
            directoryPage.HasMore,
            directoryPage.PageSize);
    }

    /// <summary>
    /// 为单例 SSE 发布器逐页读取已脱敏玩家目录；循环和容量上限防止异常游标造成无限扫描。
    /// </summary>
    public async Task<IReadOnlyList<PlayerMonitorListItem>>
        ListPlayersForRealtimeAsync(CancellationToken cancellationToken)
    {
        var result = new List<PlayerMonitorListItem>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await ListPlayersAsync(
                null,
                cursor,
                adminOptions.RealtimeCapacity.MaximumPageSize,
                cancellationToken);
            result.AddRange(page.Items.Take(
                adminOptions.RealtimeCapacity.MaximumPlayers - result.Count));
            cursor = page.NextCursor;
            if (result.Count >= adminOptions.RealtimeCapacity.MaximumPlayers)
                break;
            if (cursor is not null && !seenCursors.Add(cursor))
                throw new InvalidOperationException(
                    "Auth returned a repeated player cursor.");
        }
        while (cursor is not null);
        return result;
    }

    /// <summary>
    /// 获取玩家详情；只读页面允许 Lobby 子字段降级，Auth 主档不可用时返回明确的来源不可用错误。
    /// </summary>
    public async Task<PlayerMonitorDetail?> GetPlayerAsync(
        string playerId,
        CancellationToken cancellationToken) =>
        await GetPlayerCoreAsync(playerId, false, cancellationToken);

    /// <summary>
    /// 为高危玩家管理工作流重新读取实时 Auth 与 Lobby 状态；任何缓存或降级值都会阻止操作。
    /// </summary>
    public async Task<PlayerMonitorDetail?> GetPlayerForActionAsync(
        string playerId,
        CancellationToken cancellationToken) =>
        await GetPlayerCoreAsync(playerId, true, cancellationToken);

    private async Task<PlayerMonitorDetail?> GetPlayerCoreAsync(
        string playerId,
        bool requireLive,
        CancellationToken cancellationToken)
    {
        var directoryTask = ExecuteAuthAsync(
            $"player-detail:{HashKey(playerId)}",
            token => players.GetPlayerAsync(playerId, token),
            () => null,
            false,
            cancellationToken);
        var roomsTask = ExecuteLobbyAsync(
            "rooms",
            lobby.ListRoomsAsync,
            () => Array.Empty<RoomMonitorSnapshot>(),
            true,
            cancellationToken);
        var presenceTask = ExecuteLobbyAsync(
            $"player-presence:{HashKey(playerId)}",
            token => lobby.GetPlayerPresenceAsync([playerId], token),
            () => [],
            true,
            cancellationToken);
        await Task.WhenAll(directoryTask, roomsTask, presenceTask);
        var directoryResult = await directoryTask;
        var roomsResult = await roomsTask;
        var presenceResult = await presenceTask;
        if (!directoryResult.IsLive && directoryResult.Value is null)
            throw new MonitoringSourceUnavailableException("Auth");
        if (requireLive
            && (!directoryResult.IsLive || !roomsResult.IsLive || !presenceResult.IsLive))
        {
            throw new MonitoringFreshDataRequiredException();
        }
        var directory = directoryResult.Value;
        if (directory is null) return null;
        var rooms = roomsResult.Value
            .Where(room => room.PlayerIds.Contains(playerId, StringComparer.Ordinal))
            .OrderByDescending(room => room.UpdatedAtUtc)
            .ToArray();
        var currentRoom = FindCurrentRoom(rooms, playerId);
        var runtimeResult = currentRoom is null
            ? null
            : await ExecuteLobbyAsync(
                $"room-runtime:{currentRoom.RoomId}",
                token => lobby.GetRuntimeAsync(currentRoom.RoomId, token),
                () => null,
                true,
                cancellationToken);
        if (requireLive && runtimeResult is not null && !runtimeResult.IsLive)
            throw new MonitoringFreshDataRequiredException();
        var runtime = runtimeResult?.Value;
        var playerRuntime = runtime?.Players.FirstOrDefault(item => item.PlayerId == playerId);
        var presence = presenceResult.Value.FirstOrDefault();
        var history = rooms.Select(MapRoom).ToArray();
        var eventTasks = rooms.Take(20)
            .Select(room => ExecuteLobbyAsync(
                $"room-events:{room.RoomId}",
                token => lobby.ListEventsAsync(room.RoomId, token),
                () => [],
                true,
                cancellationToken))
            .ToArray();
        var timelines = await Task.WhenAll(eventTasks);
        if (requireLive && timelines.Any(result => !result.IsLive))
            throw new MonitoringFreshDataRequiredException();
        var disconnects = timelines.SelectMany(result => result.Value)
            .Where(item => item.EventType == "PlayerConnectionChanged"
                && EventPlayerMatches(item, playerId))
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(200)
            .ToArray();
        return new PlayerMonitorDetail(
            MapPlayer(directory.Player, presence, currentRoom, playerRuntime),
            directory.Sessions,
            directory.LoginHistory,
            directory.KnownDeviceIds,
            history,
            disconnects,
            directory.ControlHistory,
            "ReadOnlyMasked",
            BuildMetadata(
                directoryResult.IsLive
                && roomsResult.IsLive
                && presenceResult.IsLive
                && (runtimeResult?.IsLive ?? true)
                && timelines.All(result => result.IsLive),
                new[] { directoryResult.Health, roomsResult.Health, presenceResult.Health }
                    .Concat(runtimeResult is null
                        ? []
                        : [runtimeResult.Health])
                    .Concat(timelines.Select(result => result.Health))
                    .ToArray()));
    }

    private Task<MonitoringSourceResult<T>> ExecuteAuthAsync<T>(
        string operation,
        Func<CancellationToken, Task<T>> operationFactory,
        Func<T> emptyFactory,
        bool cacheSnapshot,
        CancellationToken cancellationToken) =>
        reliability.ExecuteAsync(
            "Auth",
            operation,
            adminOptions.Auth.Enabled,
            TimeSpan.FromSeconds(adminOptions.Auth.TimeoutSeconds),
            operationFactory,
            emptyFactory,
            cacheSnapshot,
            cancellationToken);

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

    private static string HashKey(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static PlayerMonitorListItem MapPlayer(
        AuthPlayerDirectoryItem player,
        PlayerPresenceSnapshot? presence,
        RoomMonitorSnapshot? room,
        PlayerRuntimeTelemetry? runtime) =>
        new(
            player.PlayerId,
            player.DisplayName,
            player.Provider,
            player.AccountStatus,
            presence?.Online == true || runtime?.ConnectionState == "Connected",
            player.CurrentDeviceId,
            player.CurrentMaskedIp,
            presence?.LobbyId,
            presence?.RoomId ?? room?.RoomId,
            presence?.RoomCode ?? room?.RoomCode,
            presence?.ServerInstanceId
                ?? (room is null ? null : GetInstanceId(room)),
            runtime?.LatencyMilliseconds,
            player.LastLoginAtUtc,
            presence?.LastSeenAtUtc,
            player.ActiveSessionCount,
            player.ControlVersion,
            player.FrozenUntilUtc,
            player.MutedUntilUtc,
            player.RiskLabels,
            runtime?.PacketLossPercent,
            runtime?.ReconnectCount,
            runtime?.Trustee,
            runtime?.IllegalActionCount,
            runtime?.ConnectionState,
            runtime?.DisconnectedAtUtc);

    private static RoomMonitorSnapshot? FindCurrentRoom(
        IEnumerable<RoomMonitorSnapshot> rooms, string playerId) =>
        rooms.Where(room => room.PlayerIds.Contains(playerId, StringComparer.Ordinal)
                && room.Lifecycle is "Allocating" or "Waiting" or "Playing" or "Settling")
            .MaxBy(room => room.UpdatedAtUtc);

    private static RoomListItem MapRoom(RoomMonitorSnapshot room) =>
        new(
            room.RoomId,
            room.RoomCode,
            room.MatchId,
            GetGameMode(room),
            room.Lifecycle,
            room.PlayerIds.Length,
            room.MaximumPlayers,
            GetInt32(room.RuleSnapshot, "currentRound"),
            room.RoundCount,
            null,
            null,
            GetInstanceId(room),
            room.CreatedAtUtc,
            room.UpdatedAtUtc,
            room.StateSequence);

    private static string? GetInstanceId(RoomMonitorSnapshot room) =>
        room.Route?.ServerInstanceId ?? room.PendingServerInstanceId ?? room.LastServerInstanceId;

    private static string GetGameMode(RoomMonitorSnapshot room)
    {
        foreach (var key in new[] { "gameMode", "playMode", "variant" })
        {
            if (room.RuleSnapshot.TryGetValue(key, out var value)
                && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "Standard";
        }
        return "Standard";
    }

    private static int GetInt32(
        IReadOnlyDictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static bool EventPlayerMatches(RoomTimelineEvent roomEvent, string playerId) =>
        roomEvent.Data.TryGetValue("playerId", out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == playerId;
}
