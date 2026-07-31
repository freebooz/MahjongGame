using GuiyangMahjong.GameData.Domain;
using GuiyangMahjong.GameData.Infrastructure;

namespace GuiyangMahjong.GameData.Administration;

/// <summary>
/// 调查侧证据元数据只读边界。它不返回对象正文或私有手牌，正文授权仍由既有 Admin 调查流程控制。
/// </summary>
public sealed class ReplayEvidenceQueries(IGameDataStore store)
{
    /// <summary>查询证据清单和摘要；不存在时返回 null，不生成对象存储下载凭据。</summary>
    public Task<ReplayEvidenceRecord?> GetAsync(string evidenceId, CancellationToken cancellationToken) =>
        store.GetEvidenceAsync(evidenceId, cancellationToken);
}
