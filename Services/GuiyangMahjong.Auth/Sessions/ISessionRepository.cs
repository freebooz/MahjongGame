using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Sessions;

/// <summary>
/// Sessions 模块持久化端口，封装创建、轮换和撤销 Refresh Token 的事务边界。
/// 所有实现必须保证轮换单次消费，并在重用时撤销整个 Token Family。
/// </summary>
public interface ISessionRepository
{
    /// <summary>依据服务端并发策略创建会话；账号受控时不得落库。</summary>
    Task<SessionCreationStatus> CreateRefreshSessionAsync(
        RefreshSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>原子轮换当前会话并继承 Family、设备及 Epoch 快照。</summary>
    Task<RefreshRotationResult> RotateRefreshSessionAsync(
        string currentSessionId,
        byte[] currentTokenHash,
        RefreshSession replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>按 SessionId 和固定时间哈希比较幂等撤销单个会话。</summary>
    Task<bool> RevokeRefreshSessionAsync(
        string sessionId,
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
