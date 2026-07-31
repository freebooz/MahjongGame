namespace GuiyangMahjong.Lobby.Infrastructure;

/// <summary>
/// Lobby 中尚未迁出的历史职责清单。
/// 这些常量用于架构测试和文档，不代表 Lobby 可以继续扩展相应写模型。
/// </summary>
public static class LegacyResponsibilityBoundary
{
    /// <summary>玩家长期房间与连接历史的目标归属，后续迁移到 GameData。</summary>
    public const string PlayerHistoryTarget = "GameData";

    /// <summary>最终结算幂等持久化的目标归属，后续迁移到 GameData/Settlement。</summary>
    public const string SettlementTarget = "GameData/Settlement";

    /// <summary>证据链和动作日志的目标归属，后续迁移到 ReplayEvidence。</summary>
    public const string EvidenceTarget = "ReplayEvidence";

    /// <summary>复杂玩家监控和风险控制的目标归属，后续迁移到 TrustSafety。</summary>
    public const string TrustSafetyTarget = "TrustSafety";
}
