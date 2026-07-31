using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.Admin.Domain;

namespace GuiyangMahjong.Admin.Security;

/// <summary>
/// 管理读模型字段级脱敏服务。普通监控只能看到稳定设备摘要和粗粒度 IP 网段；
/// 只有已通过工单、案件、RBAC/ABAC 与读取审计的调查请求才能取得上游已授权字段。
/// </summary>
public sealed class AdminDataRedactionService
{
    /// <summary>按调查授权收窄玩家摘要；不改变业务状态、游标或来源新鲜度。</summary>
    public PlayerMonitorListItem RedactPlayer(
        PlayerMonitorListItem player,
        bool identityInvestigationAuthorized)
    {
        if (identityInvestigationAuthorized) return player;
        return player with
        {
            CurrentDeviceId = StableDeviceSummary(player.CurrentDeviceId),
            CurrentMaskedIp = FurtherMaskIp(player.CurrentMaskedIp)
        };
    }

    /// <summary>设备标识只保留不可逆摘要前缀，便于同页关联而不能还原原始设备 ID。</summary>
    private static string? StableDeviceSummary(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        return $"device-{Convert.ToHexStringLower(digest.AsSpan(0, 6))}";
    }

    /// <summary>将上游已脱敏 IP 再收窄为网络摘要；格式未知时不透传原值。</summary>
    private static string? FurtherMaskIp(string? maskedIp)
    {
        if (string.IsNullOrWhiteSpace(maskedIp)) return null;
        var parts = maskedIp.Split('.', StringSplitOptions.TrimEntries);
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.*.*" : "masked-network";
    }
}
