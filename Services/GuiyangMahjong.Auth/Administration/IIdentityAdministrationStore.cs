using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Administration;

/// <summary>
/// Administration 模块的最小持久化端口，只允许会话撤销和 Identity 账号控制。
/// 接口刻意不提供 Room、Settlement、Inventory 或对局结果写入能力。
/// </summary>
public interface IIdentityAdministrationStore
{
    /// <summary>按管理命令幂等撤销玩家会话并推进 SessionEpoch。</summary>
    Task<AdminRevokePlayerSessionsResult> RevokePlayerSessionsAsync(
        string commandId,
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken);

    /// <summary>在双人审批和乐观版本约束下应用账号控制并追加审计事实。</summary>
    Task<AdminPlayerControlStoreResult> ApplyPlayerControlAsync(
        string commandId,
        string playerId,
        AdminPlayerControlAction action,
        long expectedVersion,
        string reason,
        string traceId,
        string ticketId,
        string requestedBy,
        string approvedBy,
        DateTimeOffset effectiveAtUtc,
        DateTimeOffset? expiresAtUtc,
        string? riskLabel,
        CancellationToken cancellationToken);
}
