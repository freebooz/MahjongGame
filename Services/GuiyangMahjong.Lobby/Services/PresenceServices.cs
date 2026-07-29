using System.Collections.Concurrent;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Services;

public interface IOnlinePresenceService
{
    Task TouchAsync(string playerId, CancellationToken cancellationToken);
    Task RemoveAsync(string playerId, CancellationToken cancellationToken);
    Task<long> GetOnlineCountAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PlayerPresenceSnapshot>> GetPlayersAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken);
}

public sealed class InMemoryOnlinePresenceService(
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider) : IOnlinePresenceService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSeen = new(StringComparer.Ordinal);
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(options.Value.PresenceTimeoutSeconds);
    private readonly string lobbyId = options.Value.LobbyId;

    public Task TouchAsync(string playerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lastSeen[playerId] = timeProvider.GetUtcNow();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string playerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lastSeen.TryRemove(playerId, out _);
        return Task.CompletedTask;
    }

    public Task<long> GetOnlineCountAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cutoff = timeProvider.GetUtcNow() - timeout;
        foreach (var pair in lastSeen)
            if (pair.Value < cutoff) lastSeen.TryRemove(pair.Key, out _);
        return Task.FromResult((long)lastSeen.Count);
    }

    public Task<IReadOnlyList<PlayerPresenceSnapshot>> GetPlayersAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cutoff = timeProvider.GetUtcNow() - timeout;
        IReadOnlyList<PlayerPresenceSnapshot> result = playerIds
            .Distinct(StringComparer.Ordinal)
            .Select(playerId =>
            {
                var found = lastSeen.TryGetValue(playerId, out var observedAt);
                return new PlayerPresenceSnapshot(
                    playerId,
                    found && observedAt >= cutoff,
                    found ? observedAt : null,
                    lobbyId);
            })
            .ToArray();
        return Task.FromResult(result);
    }
}

public sealed class RedisOnlinePresenceService : IOnlinePresenceService
{
    private readonly IDatabase database;
    private readonly RedisKey presenceKey;
    private readonly TimeSpan timeout;
    private readonly TimeProvider timeProvider;
    private readonly string lobbyId;

    public RedisOnlinePresenceService(
        LobbyPersistenceConnections connections,
        IOptions<LobbyOptions> options,
        TimeProvider timeProvider)
    {
        database = connections.Redis.GetDatabase();
        presenceKey = $"{options.Value.Persistence.RedisKeyPrefix}:presence";
        timeout = TimeSpan.FromSeconds(options.Value.PresenceTimeoutSeconds);
        lobbyId = options.Value.LobbyId;
        this.timeProvider = timeProvider;
    }

    public async Task TouchAsync(string playerId, CancellationToken cancellationToken)
    {
        var score = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await database.SortedSetAddAsync(presenceKey, playerId, score).WaitAsync(cancellationToken);
    }

    public async Task RemoveAsync(string playerId, CancellationToken cancellationToken) =>
        await database.SortedSetRemoveAsync(presenceKey, playerId).WaitAsync(cancellationToken);

    public async Task<long> GetOnlineCountAsync(CancellationToken cancellationToken)
    {
        var cutoff = (timeProvider.GetUtcNow() - timeout).ToUnixTimeMilliseconds();
        await database.SortedSetRemoveRangeByScoreAsync(
            presenceKey, double.NegativeInfinity, cutoff).WaitAsync(cancellationToken);
        return await database.SortedSetLengthAsync(presenceKey).WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlayerPresenceSnapshot>> GetPlayersAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cutoff = now - timeout;
        var distinct = playerIds.Distinct(StringComparer.Ordinal).ToArray();
        var scores = await Task.WhenAll(distinct.Select(playerId =>
            database.SortedSetScoreAsync(presenceKey, playerId).WaitAsync(cancellationToken)));
        return distinct.Select((playerId, index) =>
        {
            var observedAt = scores[index].HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)scores[index]!.Value)
                : (DateTimeOffset?)null;
            return new PlayerPresenceSnapshot(
                playerId,
                observedAt >= cutoff,
                observedAt,
                lobbyId);
        }).ToArray();
    }
}
