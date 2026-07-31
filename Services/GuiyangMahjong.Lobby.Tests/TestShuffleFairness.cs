using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Security;

namespace GuiyangMahjong.Lobby.Tests;

/// <summary>
/// 为结算测试生成可重复的有效公平性证明。
/// 固定材料只用于验证契约门禁和幂等行为，绝不能复制到生产随机源实现。
/// </summary>
internal static class TestShuffleFairness
{
    /// <summary>创建包含连续单局证明和最终事件链摘要的测试结算。</summary>
    internal static MatchResultReport CreateReport(
        string roomId,
        string serverInstanceId,
        long resultSequence,
        int completedRounds,
        MatchPlayerResult[] players,
        string ruleId = "GuiyangMainstreamV1",
        int ruleVersion = 1)
    {
        var proofs = new ShuffleAuditProof[completedRounds];
        var eventChainDigest = string.Empty;
        var createdAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var roundId = 1; roundId <= completedRounds; roundId++)
        {
            var proof = new ShuffleAuditProof(
                "UE-FRandomStream-FisherYates-v1",
                roundId,
                roundId.ToString("x8"),
                roundId.ToString("x64"),
                new string('0', 64),
                (roundId + 100).ToString("x64"),
                ruleId,
                ruleVersion,
                new string('a', 40),
                createdAtUtc.AddMinutes(roundId),
                createdAtUtc.AddMinutes(roundId).AddSeconds(30));
            proof = proof with
            {
                SeedCommitment = ShuffleFairnessVerifier.CalculateCommitment(roomId, proof)
            };
            proofs[roundId - 1] = proof;
            eventChainDigest = ShuffleFairnessVerifier.CalculateEventChainDigest(
                eventChainDigest, roomId, proof);
        }
        return new MatchResultReport(
            roomId, serverInstanceId, resultSequence, completedRounds,
            players, proofs, eventChainDigest);
    }
}
