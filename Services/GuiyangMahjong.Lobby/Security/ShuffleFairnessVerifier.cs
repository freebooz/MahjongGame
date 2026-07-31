using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Lobby.Domain;

namespace GuiyangMahjong.Lobby.Security;

/// <summary>
/// 验证 Dedicated Server 披露的洗牌承诺和审计事件链。
/// 这里只验证密码学绑定和冻结规则；完整牌序复现由隔离的争议调查工具完成。
/// </summary>
public static class ShuffleFairnessVerifier
{
    private const string Algorithm = "UE-FRandomStream-FisherYates-v1";
    private const string CommitmentVersion = "fair-shuffle-v1";
    private const string EventChainVersion = "fair-audit-chain-v1";

    /// <summary>
    /// 验证证明数量、连续局号、字段格式、房间规则绑定、逐局承诺和最终事件链摘要。
    /// 任一条件失败均返回 false，调用方必须拒绝持久化结算，不能降级为仅记录告警。
    /// </summary>
    public static bool Verify(MatchResultReport report, LobbyRoom room)
    {
        if (report.ShuffleProofs is null
            || report.ShuffleProofs.Length != report.CompletedRounds
            || !IsLowerHex(report.EventChainDigest, 64)
            || !TryGetRuleIdentity(room.RuleSnapshot, out var roomRuleId, out var roomRuleVersion))
        {
            return false;
        }

        var previousDigest = string.Empty;
        string? expectedRuleHash = null;
        for (var index = 0; index < report.ShuffleProofs.Length; index++)
        {
            var proof = report.ShuffleProofs[index];
            if (proof.Algorithm != Algorithm
                || proof.RoundId != index + 1
                || !IsLowerHex(proof.SeedHex, 8)
                || !IsLowerHex(proof.ServerNonceHex, 64)
                || !IsLowerHex(proof.SeedCommitment, 64)
                || !IsLowerHex(proof.DeckOrderDigest, 64)
                || string.IsNullOrWhiteSpace(proof.RuleId)
                || proof.RuleId.Contains('|', StringComparison.Ordinal)
                || proof.RuleId.Contains('=', StringComparison.Ordinal)
                || proof.RuleId != roomRuleId
                || proof.RuleVersion != roomRuleVersion
                || !IsLowerHex(proof.RuleHash, 40)
                || proof.CreatedAtUtc == default
                || proof.RevealedAtUtc == default
                || proof.RevealedAtUtc < proof.CreatedAtUtc
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(proof.SeedCommitment),
                    Encoding.ASCII.GetBytes(CalculateCommitment(report.RoomId, proof))))
            {
                return false;
            }

            // 房间创建后规则不可变，因此每局必须引用完全相同的 UE 规则快照摘要。
            expectedRuleHash ??= proof.RuleHash;
            if (!string.Equals(expectedRuleHash, proof.RuleHash, StringComparison.Ordinal))
            {
                return false;
            }
            previousDigest = CalculateEventChainDigest(previousDigest, report.RoomId, proof);
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(report.EventChainDigest!),
            Encoding.ASCII.GetBytes(previousDigest));
    }

    /// <summary>按跨语言规范文本重新计算发牌前承诺；公开用于契约测试和离线审计工具。</summary>
    public static string CalculateCommitment(string roomId, ShuffleAuditProof proof)
    {
        var canonical =
            $"{CommitmentVersion}|seed={proof.SeedHex}|roomId={roomId}|roundId={proof.RoundId}"
            + $"|ruleId={proof.RuleId}|ruleVersion={proof.RuleVersion}|ruleHash={proof.RuleHash}"
            + $"|serverNonce={proof.ServerNonceHex}";
        return Sha256Hex(canonical);
    }

    /// <summary>按局号顺序链接披露证明，检测记录删除、插入和重排。</summary>
    public static string CalculateEventChainDigest(
        string previousDigest,
        string roomId,
        ShuffleAuditProof proof)
    {
        var previous = string.IsNullOrEmpty(previousDigest) ? "genesis" : previousDigest;
        var canonical =
            $"{EventChainVersion}|previous={previous}|roomId={roomId}|roundId={proof.RoundId}"
            + $"|commitment={proof.SeedCommitment}|deckOrderDigest={proof.DeckOrderDigest}"
            + $"|ruleHash={proof.RuleHash}";
        return Sha256Hex(canonical);
    }

    /// <summary>读取 JSON 或内存对象规则标识；未携带版本时按现有 v1 契约处理。</summary>
    private static bool TryGetRuleIdentity(
        IReadOnlyDictionary<string, object?> snapshot,
        out string ruleId,
        out int ruleVersion)
    {
        ruleId = string.Empty;
        ruleVersion = 1;
        if (!snapshot.TryGetValue("ruleId", out var ruleIdValue))
        {
            return false;
        }
        ruleId = ruleIdValue switch
        {
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
            string text => text,
            _ => Convert.ToString(ruleIdValue, CultureInfo.InvariantCulture) ?? string.Empty
        };
        if (snapshot.TryGetValue("ruleVersion", out var versionValue))
        {
            ruleVersion = versionValue switch
            {
                JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt32(out var value) => value,
                int value => value,
                long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
                _ when int.TryParse(
                    Convert.ToString(versionValue, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value) => value,
                _ => 0
            };
        }
        return !string.IsNullOrWhiteSpace(ruleId) && ruleVersion > 0;
    }

    /// <summary>限制为小写十六进制，保证 C++ 与 C# 不会产生多种持久表示。</summary>
    private static bool IsLowerHex(string? value, int expectedLength) =>
        value is not null && value.Length == expectedLength
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>对 UTF-8 规范文本计算小写 SHA-256。</summary>
    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
