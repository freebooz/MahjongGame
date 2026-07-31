using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.GameData.Domain;

namespace GuiyangMahjong.GameData.Settlement;

/// <summary>
/// 最终结果规范化、输入校验、请求指纹和 DS HMAC 验证工具。
/// 规范串字段顺序固定，任何玩家结果或证据清单变化都会使签名失效。
/// </summary>
public static class SettlementSecurity
{
    /// <summary>验证不依赖外部状态的格式、范围、唯一性和时间窗口。</summary>
    public static void ValidateEnvelope(FinalResultEnvelope envelope, DateTimeOffset now)
    {
        if (!Guid.TryParse(envelope.MatchId, out _)
            || !Guid.TryParse(envelope.RoomId, out _)
            || !Guid.TryParse(envelope.ServerInstanceId, out _)
            || !Guid.TryParse(envelope.EvidenceId, out _)
            || envelope.RoundNo is < 1 or > 16
            || envelope.SettlementVersion is < 1 or > 100
            || envelope.RoomEpoch < 1
            || !IsSafeVersion(envelope.RuleSetVersion)
            || !IsSafeVersion(envelope.ServerBuild)
            || !IsSha256(envelope.WorkloadCredentialHash)
            || !IsSha256(envelope.FinalStateHash)
            || !IsSha256(envelope.ActionLogHash)
            || !IsSha256(envelope.RandomCommitment)
            || !IsSha256(envelope.ServerSignature)
            || envelope.GeneratedAt < now.AddMinutes(-15)
            || envelope.GeneratedAt > now.AddMinutes(2)
            || envelope.PlayerResults is null
            || envelope.PlayerResults.Length is < 1 or > 4
            || envelope.PlayerResults.Select(player => player.PlayerId)
                .Distinct(StringComparer.Ordinal).Count() != envelope.PlayerResults.Length
            || envelope.PlayerResults.Select(player => player.SeatId).Distinct().Count()
                != envelope.PlayerResults.Length
            || envelope.PlayerResults.Select(player => player.Rank).Distinct().Count()
                != envelope.PlayerResults.Length
            || envelope.PlayerResults.Any(player => !IsPlayerId(player.PlayerId)
                || player.SeatId is < 0 or > 3
                || player.Rank is < 1 or > 4)
            || envelope.EvidenceManifest is null
            || envelope.EvidenceManifest.Length is < 2 or > 16
            || envelope.EvidenceManifest.Select(item => item.Kind)
                .Distinct(StringComparer.Ordinal).Count() != envelope.EvidenceManifest.Length
            || !envelope.EvidenceManifest.Any(item => item.Kind == "snapshot")
            || !envelope.EvidenceManifest.Any(item => item.Kind == "actions")
            || envelope.EvidenceManifest.Any(item => !IsEvidenceItem(envelope, item)))
        {
            throw GameDataException.Invalid("FINAL_RESULT_INVALID", "最终结算信封格式或范围无效");
        }
    }

    /// <summary>校验 Lobby 返回的权威绑定和玩家集合；任一差异均失败关闭。</summary>
    public static void ValidateAuthority(FinalResultEnvelope envelope, SettlementAuthority authority)
    {
        var submittedPlayers = envelope.PlayerResults.Select(player => player.PlayerId)
            .Order(StringComparer.Ordinal).ToArray();
        var expectedPlayers = authority.PlayerIds.Order(StringComparer.Ordinal).ToArray();
        if (!authority.Authorized
            || authority.MatchId != envelope.MatchId
            || authority.RoomId != envelope.RoomId
            || authority.ServerInstanceId != envelope.ServerInstanceId
            || authority.RoomEpoch != envelope.RoomEpoch
            || authority.RuleSetVersion != envelope.RuleSetVersion
            || authority.ServerBuild != envelope.ServerBuild
            || !submittedPlayers.SequenceEqual(expectedPlayers, StringComparer.Ordinal))
        {
            throw GameDataException.Unauthorized(
                authority.FailureCode ?? "SETTLEMENT_AUTHORITY_MISMATCH",
                "Dedicated Server 结算作用域无效");
        }
    }

    /// <summary>使用 DS 结果凭据固定时间验证签名；凭据和签名原文均不得写日志。</summary>
    public static bool VerifySignature(FinalResultEnvelope envelope, string signingKey)
    {
        if (signingKey.Length < 32) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(BuildCanonical(envelope)));
        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(envelope.ServerSignature);
        }
        catch (FormatException)
        {
            return false;
        }
        var valid = expected.Length == supplied.Length
            && CryptographicOperations.FixedTimeEquals(expected, supplied);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(supplied);
        return valid;
    }

    /// <summary>计算用于数据库冲突检测的 SHA-256 指纹，不包含可重复计算的签名字段。</summary>
    public static string Fingerprint(FinalResultEnvelope envelope) =>
        Sha256(BuildCanonical(envelope));

    /// <summary>对短期结果凭据做不可逆摘要，供 Lobby 与自身存储的凭据哈希比较。</summary>
    public static string CredentialHash(string credential) => Sha256(credential);

    /// <summary>返回跨 C++/.NET 一致的 UTF-8 规范串；列表先按稳定业务键排序。</summary>
    public static string BuildCanonical(FinalResultEnvelope envelope)
    {
        var players = string.Join(';', envelope.PlayerResults
            .OrderBy(player => player.SeatId)
            .Select(player => $"{player.PlayerId},{player.SeatId},{player.Rank},{player.TotalScore}"));
        var evidence = string.Join(';', envelope.EvidenceManifest
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .Select(item => $"{item.Kind},{item.ObjectKey},{item.Sha256.ToLowerInvariant()},{item.SizeBytes}"));
        return string.Join('|',
            "final-result-v1",
            envelope.MatchId,
            envelope.RoomId,
            envelope.RoundNo,
            envelope.SettlementVersion,
            envelope.ServerInstanceId,
            envelope.RoomEpoch,
            envelope.RuleSetVersion,
            envelope.ServerBuild,
            envelope.WorkloadCredentialHash.ToLowerInvariant(),
            envelope.FinalStateHash.ToLowerInvariant(),
            envelope.ActionLogHash.ToLowerInvariant(),
            envelope.RandomCommitment.ToLowerInvariant(),
            players,
            envelope.EvidenceId,
            evidence,
            envelope.GeneratedAt.ToUnixTimeMilliseconds());
    }

    private static bool IsEvidenceItem(FinalResultEnvelope envelope, EvidenceManifestItem item)
    {
        if (item.Kind is not ("snapshot" or "actions" or "shuffle-audit")
            || !IsSha256(item.Sha256)
            || item.SizeBytes is < 1 or > 1024L * 1024 * 1024
            || item.ObjectKey.Length is < 10 or > 512
            || item.ObjectKey.Contains("..", StringComparison.Ordinal)
            || Uri.IsWellFormedUriString(item.ObjectKey, UriKind.Absolute))
            return false;
        var expectedPrefix = $"matches/{envelope.MatchId}/epochs/{envelope.RoomEpoch}/";
        return item.ObjectKey.StartsWith(expectedPrefix, StringComparison.Ordinal)
            && item.ObjectKey.Contains($"/{item.Sha256.ToLowerInvariant()}/", StringComparison.Ordinal);
    }

    private static bool IsPlayerId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80
        && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ':');

    private static bool IsSafeVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80
        && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSha256(string value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>GameData 稳定错误；中间件据此输出统一 Problem Details，不泄露内部异常。</summary>
public sealed class GameDataException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static GameDataException Invalid(string code, string message) =>
        new(code, message, StatusCodes.Status400BadRequest);
    public static GameDataException Unauthorized(string code, string message) =>
        new(code, message, StatusCodes.Status401Unauthorized);
    public static GameDataException Conflict(string code, string message) =>
        new(code, message, StatusCodes.Status409Conflict);
    public static GameDataException Unavailable(string code, string message) =>
        new(code, message, StatusCodes.Status503ServiceUnavailable);
}
