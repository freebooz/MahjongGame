using System.Collections.Concurrent;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 提供单 Lobby 进程使用的在线状态实现。
/// 该实现不跨副本复制，仅适用于开发、测试或显式单副本部署。
/// </summary>
public sealed class InMemoryOnlinePresenceService(
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider) : IOnlinePresenceService
{
    /// <summary>玩家最后一次被 Lobby 观测到的 UTC 时间。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSeen = new(StringComparer.Ordinal);

    /// <summary>超过此时间未产生心跳的玩家按离线处理。</summary>
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(options.Value.PresenceTimeoutSeconds);

    /// <summary>写入状态快照的当前 Lobby 实例标识。</summary>
    private readonly string lobbyId = options.Value.LobbyId;

    /// <inheritdoc />
    public Task TouchAsync(string playerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lastSeen[playerId] = timeProvider.GetUtcNow();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(string playerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lastSeen.TryRemove(playerId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<long> GetOnlineCountAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cutoff = timeProvider.GetUtcNow() - timeout;
        foreach (var pair in lastSeen)
            if (pair.Value < cutoff) lastSeen.TryRemove(pair.Key, out _);
        return Task.FromResult((long)lastSeen.Count);
    }

    /// <inheritdoc />
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
