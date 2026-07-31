using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Infrastructure;

namespace GuiyangMahjong.GameData.Leaderboards;

/// <summary>基础排行榜投影查询；该投影可由结算事件重建，绝不能反向作为比赛结果权威来源。</summary>
public sealed class LeaderboardQueries(IGameDataStore store)
{
    /// <summary>返回按累计分数排序的基础榜单；本阶段不包含赛季、跨区或奖励结算。</summary>
    public Task<IReadOnlyList<LeaderboardEntry>> GetBasicAsync(
        int limit, CancellationToken cancellationToken) =>
        store.GetLeaderboardAsync(limit, cancellationToken);
}
