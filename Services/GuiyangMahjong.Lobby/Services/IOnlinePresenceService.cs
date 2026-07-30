using GuiyangMahjong.Lobby.Domain;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// 定义 Lobby 玩家在线心跳与批量状态查询边界。
/// 在线状态由最后观测时间和统一超时阈值推导，不作为永久账号状态持久化。
/// </summary>
public interface IOnlinePresenceService
{
    /// <summary>记录玩家当前时刻仍在线；取消时不得更新最后观测时间。</summary>
    Task TouchAsync(string playerId, CancellationToken cancellationToken);

    /// <summary>主动移除玩家在线状态，用于正常退出或强制下线。</summary>
    Task RemoveAsync(string playerId, CancellationToken cancellationToken);

    /// <summary>清理过期心跳后返回当前 Lobby 在线玩家数量。</summary>
    Task<long> GetOnlineCountAsync(CancellationToken cancellationToken);

    /// <summary>批量查询指定玩家的在线状态、最后观测时间和 Lobby 归属。</summary>
    Task<IReadOnlyList<PlayerPresenceSnapshot>> GetPlayersAsync(
        IReadOnlyCollection<string> playerIds, CancellationToken cancellationToken);
}
