using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Players;

/// <summary>
/// Players 模块供内部监控使用的只读目录端口。返回模型必须脱敏，
/// 不得包含 Token 哈希、签名密钥、完整 IP 或原始设备指纹。
/// </summary>
public interface IPlayerDirectoryReader
{
    /// <summary>使用稳定键集游标读取玩家摘要，limit 包含调用方要求的探测记录。</summary>
    Task<IReadOnlyList<PlayerDirectoryItem>> ListPlayersAsync(
        string? search,
        int limit,
        DateTimeOffset? afterCreatedAtUtc,
        string? afterPlayerId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>读取玩家会话、登录、设备引用和控制历史的脱敏聚合详情。</summary>
    Task<PlayerDirectoryDetail?> GetPlayerDetailAsync(
        string playerId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
