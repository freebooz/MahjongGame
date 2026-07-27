using System.Collections.Concurrent;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Storage;

public interface IRoomMonitoringStore
{
    Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken);
    Task SetRuntimeAsync(
        RoomRuntimeTelemetry runtime, CancellationToken cancellationToken);
    Task AppendEventAsync(
        string roomId, RoomTimelineEvent roomEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoomTimelineEvent>> ListEventsAsync(
        string roomId, int limit, CancellationToken cancellationToken);
}

public sealed class InMemoryRoomMonitoringStore : IRoomMonitoringStore
{
    private readonly ConcurrentDictionary<string, RoomRuntimeTelemetry> runtimes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RoomTimelineEvent>> events =
        new(StringComparer.Ordinal);

    public Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtimes.TryGetValue(roomId, out var runtime);
        return Task.FromResult(runtime);
    }

    public Task SetRuntimeAsync(
        RoomRuntimeTelemetry runtime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        runtimes[runtime.RoomId] = runtime;
        return Task.CompletedTask;
    }

    public Task AppendEventAsync(
        string roomId, RoomTimelineEvent roomEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queue = events.GetOrAdd(roomId, _ => new ConcurrentQueue<RoomTimelineEvent>());
        queue.Enqueue(roomEvent);
        while (queue.Count > 500) queue.TryDequeue(out _);
        return Task.CompletedTask;
    }

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

public sealed class RedisRoomMonitoringStore : IRoomMonitoringStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly IDatabase database;
    private readonly string prefix;

    public RedisRoomMonitoringStore(
        LobbyPersistenceConnections connections,
        IOptions<LobbyOptions> options)
    {
        database = connections.Redis.GetDatabase();
        prefix = options.Value.Persistence.RedisKeyPrefix;
    }

    public async Task<RoomRuntimeTelemetry?> GetRuntimeAsync(
        string roomId, CancellationToken cancellationToken)
    {
        var value = await database.StringGetAsync(RuntimeKey(roomId))
            .WaitAsync(cancellationToken);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<RoomRuntimeTelemetry>(value.ToString(), JsonOptions);
    }

    public async Task SetRuntimeAsync(
        RoomRuntimeTelemetry runtime, CancellationToken cancellationToken)
    {
        await database.StringSetAsync(
                RuntimeKey(runtime.RoomId),
                JsonSerializer.Serialize(runtime, JsonOptions),
                TimeSpan.FromHours(6))
            .WaitAsync(cancellationToken);
    }

    public async Task AppendEventAsync(
        string roomId, RoomTimelineEvent roomEvent, CancellationToken cancellationToken)
    {
        var key = EventsKey(roomId);
        var transaction = database.CreateTransaction();
        _ = transaction.ListLeftPushAsync(key, JsonSerializer.Serialize(roomEvent, JsonOptions));
        _ = transaction.ListTrimAsync(key, 0, 499);
        _ = transaction.KeyExpireAsync(key, TimeSpan.FromDays(7));
        _ = await transaction.ExecuteAsync().WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RoomTimelineEvent>> ListEventsAsync(
        string roomId, int limit, CancellationToken cancellationToken)
    {
        var values = await database.ListRangeAsync(EventsKey(roomId), 0, limit - 1)
            .WaitAsync(cancellationToken);
        return values
            .Select(value => JsonSerializer.Deserialize<RoomTimelineEvent>(
                value.ToString(), JsonOptions))
            .OfType<RoomTimelineEvent>()
            .Reverse()
            .ToArray();
    }

    private string RuntimeKey(string roomId) => $"{prefix}:monitor:room:{roomId}:runtime";
    private string EventsKey(string roomId) => $"{prefix}:monitor:room:{roomId}:events";
}
