namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 定义玩家访问令牌撤销水位的读写边界。
/// 水位只允许单调前移，使早于或等于该时间签发的令牌立即失效。
/// </summary>
public interface IPlayerAccessRevocationStore
{
    /// <summary>
    /// 将玩家撤销水位推进到指定 UTC 时间，并返回最终生效水位。
    /// </summary>
    Task<DateTimeOffset> RevokeBeforeAsync(
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken);

    /// <summary>判断指定签发时间的玩家令牌是否已被撤销。</summary>
    Task<bool> IsRevokedAsync(
        string playerId,
        DateTimeOffset issuedAtUtc,
        CancellationToken cancellationToken);
}
