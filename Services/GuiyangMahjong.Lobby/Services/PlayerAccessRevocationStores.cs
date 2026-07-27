using System.Collections.Concurrent;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Storage;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Services;

public interface IPlayerAccessRevocationStore
{
    Task<DateTimeOffset> RevokeBeforeAsync(
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken);
    Task<bool> IsRevokedAsync(
        string playerId,
        DateTimeOffset issuedAtUtc,
        CancellationToken cancellationToken);
}

public sealed class InMemoryPlayerAccessRevocationStore(
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider) : IPlayerAccessRevocationStore
{
    private readonly ConcurrentDictionary<string, Revocation> revocations =
        new(StringComparer.Ordinal);
    private readonly TimeSpan ttl =
        TimeSpan.FromMinutes(options.Value.AccessRevocationTtlMinutes);

    public Task<DateTimeOffset> RevokeBeforeAsync(
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAtUtc = timeProvider.GetUtcNow() + ttl;
        var value = revocations.AddOrUpdate(
            playerId,
            _ => new Revocation(effectiveAtUtc, expiresAtUtc),
            (_, current) => new Revocation(
                current.RevokedBeforeUtc > effectiveAtUtc
                    ? current.RevokedBeforeUtc
                    : effectiveAtUtc,
                expiresAtUtc));
        return Task.FromResult(value.RevokedBeforeUtc);
    }

    public Task<bool> IsRevokedAsync(
        string playerId,
        DateTimeOffset issuedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!revocations.TryGetValue(playerId, out var value))
            return Task.FromResult(false);
        if (value.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            revocations.TryRemove(playerId, out _);
            return Task.FromResult(false);
        }
        return Task.FromResult(issuedAtUtc <= value.RevokedBeforeUtc);
    }

    private sealed record Revocation(
        DateTimeOffset RevokedBeforeUtc,
        DateTimeOffset ExpiresAtUtc);
}

public sealed class RedisPlayerAccessRevocationStore : IPlayerAccessRevocationStore
{
    private const string RevokeScript =
        """
        local current = redis.call('GET', KEYS[1])
        local incoming = tonumber(ARGV[1])
        if not current or tonumber(current) < incoming then
            redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
            return incoming
        end
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return tonumber(current)
        """;

    private readonly IDatabase database;
    private readonly string keyPrefix;
    private readonly long ttlMilliseconds;

    public RedisPlayerAccessRevocationStore(
        LobbyPersistenceConnections connections,
        IOptions<LobbyOptions> options)
    {
        database = connections.Redis.GetDatabase();
        keyPrefix = $"{options.Value.Persistence.RedisKeyPrefix}:access-revoked-before:";
        ttlMilliseconds = checked((long)TimeSpan
            .FromMinutes(options.Value.AccessRevocationTtlMinutes)
            .TotalMilliseconds);
    }

    public async Task<DateTimeOffset> RevokeBeforeAsync(
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken)
    {
        var result = await database.ScriptEvaluateAsync(
            RevokeScript,
            [GetKey(playerId)],
            [effectiveAtUtc.ToUnixTimeMilliseconds(), ttlMilliseconds])
            .WaitAsync(cancellationToken);
        return DateTimeOffset.FromUnixTimeMilliseconds((long)result);
    }

    public async Task<bool> IsRevokedAsync(
        string playerId,
        DateTimeOffset issuedAtUtc,
        CancellationToken cancellationToken)
    {
        var value = await database.StringGetAsync(GetKey(playerId))
            .WaitAsync(cancellationToken);
        return value.HasValue
            && issuedAtUtc.ToUnixTimeMilliseconds() <= (long)value;
    }

    private RedisKey GetKey(string playerId) => $"{keyPrefix}{playerId}";
}
