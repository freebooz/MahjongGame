using System.Collections.Concurrent;
using GuiyangMahjong.Lobby.Options;
using Microsoft.Extensions.Options;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 提供单进程开发与测试环境使用的访问撤销存储。
/// 数据不会跨 Lobby 副本复制，进程重启后自动清空。
/// </summary>
public sealed class InMemoryPlayerAccessRevocationStore(
    IOptions<LobbyOptions> options,
    TimeProvider timeProvider) : IPlayerAccessRevocationStore
{
    /// <summary>按玩家保存撤销水位及其自动过期时间。</summary>
    private readonly ConcurrentDictionary<string, Revocation> revocations =
        new(StringComparer.Ordinal);

    /// <summary>撤销记录的保留时长，应覆盖访问令牌最大生命周期。</summary>
    private readonly TimeSpan ttl =
        TimeSpan.FromMinutes(options.Value.AccessRevocationTtlMinutes);

    /// <inheritdoc />
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
                // 撤销水位只能向未来推进，避免并发旧请求重新激活已经失效的令牌。
                current.RevokedBeforeUtc > effectiveAtUtc
                    ? current.RevokedBeforeUtc
                    : effectiveAtUtc,
                expiresAtUtc));
        return Task.FromResult(value.RevokedBeforeUtc);
    }

    /// <inheritdoc />
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

    /// <summary>保存单个玩家撤销水位及其自动回收时间。</summary>
    private sealed record Revocation(
        DateTimeOffset RevokedBeforeUtc,
        DateTimeOffset ExpiresAtUtc);
}
