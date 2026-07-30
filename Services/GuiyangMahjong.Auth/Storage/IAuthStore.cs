using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Storage;

/// <summary>
/// Auth 身份、刷新会话、登录历史和玩家控制的权威存储边界。
/// 生产实现必须保证刷新令牌单次轮换、控制版本乐观并发及管理命令幂等，
/// 只持久化 installation/token 哈希和脱敏网络观察值。
/// </summary>
public interface IAuthStore
{
    /// <summary>初始化或验证认证表结构；失败时 Auth 不得进入就绪状态。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>检查身份、会话和控制状态存储可用性，不签发或撤销令牌。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 按 installationHash 原子取得或创建游客身份。
    /// 相同安装哈希永远返回原身份，proposedIdentity 仅在首次插入时采用。
    /// </summary>
    Task<AuthIdentity> GetOrCreateGuestAsync(
        string installationHash,
        AuthIdentity proposedIdentity,
        CancellationToken cancellationToken);

    /// <summary>账号有效时创建刷新会话；冻结或封禁返回状态且不得保存会话。</summary>
    Task<SessionCreationStatus> CreateRefreshSessionAsync(
        RefreshSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// 原子消费当前刷新会话并创建唯一后继。
    /// 当前令牌不存在、哈希不符、过期、已撤销或账号受控时不能产生 replacement。
    /// </summary>
    Task<RefreshRotationResult> RotateRefreshSessionAsync(
        string currentSessionId,
        byte[] currentTokenHash,
        RefreshSession replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>按会话标识和固定时间哈希比较撤销刷新会话；重复撤销保持幂等。</summary>
    Task<bool> RevokeRefreshSessionAsync(
        string sessionId,
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按 Admin commandId 幂等撤销玩家在 effectiveAtUtc 前的全部活动会话，
    /// 返回本次真正改变的会话数量。
    /// </summary>
    Task<AdminRevokePlayerSessionsResult> RevokePlayerSessionsAsync(
        string commandId,
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以 expectedVersion 应用双人审批玩家控制并追加控制事件。
    /// 冻结/封禁同时撤销会话；版本冲突、非法迁移或命令冲突不产生部分状态。
    /// </summary>
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

    /// <summary>追加一条脱敏登录事件；EventId 必须幂等，历史读取保持有界。</summary>
    Task RecordLoginAsync(AuthLoginEvent loginEvent, CancellationToken cancellationToken);
    /// <summary>按不可变创建时间与 PlayerId 执行键集分页；limit 包含用于判断下一页的额外记录。</summary>
    Task<IReadOnlyList<PlayerDirectoryItem>> ListPlayersAsync(
        string? search,
        int limit,
        DateTimeOffset? afterCreatedAtUtc,
        string? afterPlayerId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// 聚合指定玩家的身份、有效控制、会话、登录、设备和控制历史；
    /// 不存在返回空，令牌哈希永不包含在详情中。
    /// </summary>
    Task<PlayerDirectoryDetail?> GetPlayerDetailAsync(
        string playerId, DateTimeOffset now, CancellationToken cancellationToken);
}
