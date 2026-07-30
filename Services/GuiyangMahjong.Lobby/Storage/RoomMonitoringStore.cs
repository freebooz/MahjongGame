using System.Collections.Concurrent;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Storage;

/// <summary>
/// 房间运行遥测与事件时间线存储边界。
/// Runtime 是可覆盖的最新快照，事件按 EventId 幂等追加且用于调查；
/// 生产实现必须先持久化事件再更新易失缓存。
/// </summary>
public interface IRoomMonitoringStore
{
    /// <summary>读取指定房间最新运行快照；不存在返回空，调用方负责新鲜度判定。</summary>
    Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken);

    /// <summary>保存最新运行快照；ObservedAtUtc 不得倒退覆盖较新样本。</summary>
    Task SetRuntimeAsync(
        RoomRuntimeTelemetry runtime, CancellationToken cancellationToken);

    /// <summary>按 EventId 幂等追加调查事件；失败时不得声称事件已保存。</summary>
    Task AppendEventAsync(
        string roomId, RoomTimelineEvent roomEvent, CancellationToken cancellationToken);

    /// <summary>按发生/状态顺序返回指定房间最近的有界事件列表。</summary>
    Task<IReadOnlyList<RoomTimelineEvent>> ListEventsAsync(
        string roomId, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// 单进程开发/测试用房间监控存储。
/// 运行快照、事件队列和幂等集合只驻留内存，每房间最多保留 500 条时间线事件。
/// </summary>
public sealed class InMemoryRoomMonitoringStore : IRoomMonitoringStore
{
    private readonly ConcurrentDictionary<string, RoomRuntimeTelemetry> runtimes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RoomTimelineEvent>> events =
        new(StringComparer.Ordinal);
    /// <summary>
    /// 每个房间已接收的 EventId 集合；与事件队列同生命周期，用于并发重复心跳的幂等去重。
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> eventIds =
        new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtimes.TryGetValue(roomId, out var runtime);
        return Task.FromResult(runtime);
    }

    /// <inheritdoc/>
    public Task SetRuntimeAsync(
        RoomRuntimeTelemetry runtime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtimes[runtime.RoomId] = runtime;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 按 EventId 幂等追加房间事件；并发重复事件只保留一次，超过 500 条时同步释放旧幂等键。
    /// </summary>
    public Task AppendEventAsync(
        string roomId, RoomTimelineEvent roomEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roomEventIds = eventIds.GetOrAdd(
            roomId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        // EventId 由权威生产者或 Lobby 生成；并发重复写入只有首个调用可以进入队列。
        if (!roomEventIds.TryAdd(roomEvent.EventId, 0)) return Task.CompletedTask;
        var queue = events.GetOrAdd(roomId, _ => new ConcurrentQueue<RoomTimelineEvent>());
        queue.Enqueue(roomEvent);
        while (queue.Count > 500)
        {
            if (queue.TryDequeue(out var removed)) roomEventIds.TryRemove(removed.EventId, out _);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RoomTimelineEvent>> ListEventsAsync(
        string roomId, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RoomTimelineEvent> result = events.TryGetValue(roomId, out var queue)
            ? queue.Reverse().Take(limit).Reverse().ToArray()
            : [];
        return Task.FromResult(result);
    }
}

/// <summary>
/// Redis 热遥测与 PostgreSQL 权威事件历史的生产实现。
/// 最新 Runtime 通过 TTL 自动过期以暴露数据陈旧，时间线先写 PostgreSQL 再维护 Redis 有界列表。
/// </summary>
public sealed class RedisRoomMonitoringStore : IRoomMonitoringStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly IDatabase database;
    private readonly NpgsqlDataSource postgres;
    private readonly string prefix;

    /// <summary>取得共享 Redis/PostgreSQL 连接并冻结键前缀；连接生命周期由容器拥有。</summary>
    public RedisRoomMonitoringStore(
        LobbyPersistenceConnections connections,
        IOptions<LobbyOptions> options)
    {
        database = connections.Redis.GetDatabase();
        postgres = connections.Postgres;
        prefix = options.Value.Persistence.RedisKeyPrefix;
    }

    /// <inheritdoc/>
    public async Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken)
    {
        var value = await database.StringGetAsync(RuntimeKey(roomId))
            .WaitAsync(cancellationToken);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<RoomRuntimeTelemetry>(value.ToString(), JsonOptions);
    }

    /// <inheritdoc/>
    public async Task SetRuntimeAsync(
        RoomRuntimeTelemetry runtime, CancellationToken cancellationToken)
    {
        await database.StringSetAsync(
                RuntimeKey(runtime.RoomId),
                JsonSerializer.Serialize(runtime, JsonOptions),
                TimeSpan.FromHours(6))
            .WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 先向 PostgreSQL 权威历史执行幂等追加，再更新 Redis 热缓存。
    /// 数据库写入失败时不返回成功，避免仅存在于易失缓存的调查证据。
    /// </summary>
    public async Task AppendEventAsync(
        string roomId, RoomTimelineEvent roomEvent, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(roomEvent, JsonOptions);
        await using var command = postgres.CreateCommand(
            """
            INSERT INTO room_event_history(
                event_id, room_id, match_id, state_sequence, event_type,
                occurred_at_utc, trace_id, payload)
            SELECT $1, room_id, NULLIF(payload->>'matchId', ''),
                   $2, $3, $4, $5, $6::jsonb
            FROM lobby_rooms
            WHERE room_id=$7
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """);
        command.Parameters.AddWithValue(Guid.Parse(roomEvent.EventId));
        command.Parameters.AddWithValue(roomEvent.StateSequence);
        command.Parameters.AddWithValue(roomEvent.EventType);
        command.Parameters.AddWithValue(roomEvent.OccurredAtUtc);
        command.Parameters.AddWithValue(roomEvent.TraceId);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue(roomId);
        // 只有首次权威写入才更新缓存；重复投递不会造成 Redis 列表重复。
        if (await command.ExecuteScalarAsync(cancellationToken) is null) return;

        const string appendIfNewScript = """
            if redis.call('SET', KEYS[2], '1', 'EX', 604800, 'NX') then
                redis.call('LPUSH', KEYS[1], ARGV[1])
                redis.call('LTRIM', KEYS[1], 0, 499)
                redis.call('EXPIRE', KEYS[1], 604800)
                return 1
            end
            return 0
            """;
        // Lua 将 EventId 去重标记与列表追加放在同一原子边界，避免并发心跳重复事件或中途丢失。
        await database.ScriptEvaluateAsync(
                appendIfNewScript,
                [EventsKey(roomId), EventIdKey(roomId, roomEvent.EventId)],
                [payload])
            .WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RoomTimelineEvent>> ListEventsAsync(
        string roomId, int limit, CancellationToken cancellationToken)
    {
        var values = await database.ListRangeAsync(EventsKey(roomId), 0, limit - 1)
            .WaitAsync(cancellationToken);
        var cached = values
            .Select(value => JsonSerializer.Deserialize<RoomTimelineEvent>(
                value.ToString(), JsonOptions))
            .OfType<RoomTimelineEvent>()
            .Reverse()
            .ToArray();
        if (cached.Length >= limit) return cached;

        // Redis 过期或缓存不完整时回源权威库，并恢复热缓存供后续详情读取。
        await using var command = postgres.CreateCommand(
            """
            SELECT payload::text
            FROM room_event_history
            WHERE room_id=$1
            ORDER BY occurred_at_utc DESC, event_id DESC
            LIMIT $2
            """);
        command.Parameters.AddWithValue(roomId);
        command.Parameters.AddWithValue(limit);
        var persisted = new List<RoomTimelineEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = JsonSerializer.Deserialize<RoomTimelineEvent>(
                reader.GetString(0), JsonOptions);
            if (item is not null) persisted.Add(item);
        }
        persisted.Reverse();
        if (persisted.Count > 0)
        {
            var entries = persisted
                .AsEnumerable()
                .Reverse()
                .Select(item => (RedisValue)JsonSerializer.Serialize(item, JsonOptions))
                .ToArray();
            await database.KeyDeleteAsync(EventsKey(roomId)).WaitAsync(cancellationToken);
            await database.ListRightPushAsync(EventsKey(roomId), entries)
                .WaitAsync(cancellationToken);
            await database.KeyExpireAsync(EventsKey(roomId), TimeSpan.FromDays(7))
                .WaitAsync(cancellationToken);
        }
        return persisted;
    }

    private string RuntimeKey(string roomId) => $"{prefix}:monitor:room:{roomId}:runtime";
    private string EventsKey(string roomId) => $"{prefix}:monitor:room:{roomId}:events";
    /// <summary>构造单个事件的幂等键；EventId 已在入口限制为 UUID，不包含外部敏感数据。</summary>
    private string EventIdKey(string roomId, string eventId) =>
        $"{prefix}:monitor:room:{roomId}:event:{eventId}";
}
