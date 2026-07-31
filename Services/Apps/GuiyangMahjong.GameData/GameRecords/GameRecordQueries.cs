using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Infrastructure;

namespace GuiyangMahjong.GameData.GameRecords;

/// <summary>
/// 战绩只读用例边界。它只读取 SettlementCommitted 产生的投影，不返回资产账本，也不提供历史修改方法。
/// </summary>
public sealed class GameRecordQueries(IGameDataStore store)
{
    /// <summary>查询指定比赛的最新已提交局记录；不存在时返回 null。</summary>
    public Task<GameRecord?> GetMatchAsync(string matchId, CancellationToken cancellationToken) =>
        store.GetMatchAsync(matchId, cancellationToken);

    /// <summary>按提交时间倒序查询玩家战绩，limit 已由 API 边界限制在安全范围内。</summary>
    public Task<IReadOnlyList<GameRecord>> GetPlayerAsync(
        string playerId, int limit, CancellationToken cancellationToken) =>
        store.GetPlayerRecordsAsync(playerId, limit, cancellationToken);
}
