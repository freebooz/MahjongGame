using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Storage;

public interface IAuthStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task<AuthIdentity> GetOrCreateGuestAsync(
        string installationHash,
        AuthIdentity proposedIdentity,
        CancellationToken cancellationToken);
    Task CreateRefreshSessionAsync(RefreshSession session, CancellationToken cancellationToken);
    Task<RefreshRotationResult> RotateRefreshSessionAsync(
        string currentSessionId,
        byte[] currentTokenHash,
        RefreshSession replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<bool> RevokeRefreshSessionAsync(
        string sessionId,
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<AdminRevokePlayerSessionsResult> RevokePlayerSessionsAsync(
        string commandId,
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken);
    Task RecordLoginAsync(AuthLoginEvent loginEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlayerDirectoryItem>> ListPlayersAsync(
        string? search, int limit, DateTimeOffset now, CancellationToken cancellationToken);
    Task<PlayerDirectoryDetail?> GetPlayerDetailAsync(
        string playerId, DateTimeOffset now, CancellationToken cancellationToken);
}
