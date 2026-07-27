using System.Security.Cryptography;
using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Storage;

public sealed class InMemoryAuthStore : IAuthStore
{
    private readonly Dictionary<string, AuthIdentity> identitiesByInstallation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthIdentity> identitiesByPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RefreshSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AdminRevokePlayerSessionsResult> adminCommands =
        new(StringComparer.Ordinal);
    private readonly List<AuthLoginEvent> loginEvents = [];
    private readonly object gate = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<AuthIdentity> GetOrCreateGuestAsync(
        string installationHash,
        AuthIdentity proposedIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (identitiesByInstallation.TryGetValue(installationHash, out var existing))
                return Task.FromResult(existing);
            identitiesByInstallation[installationHash] = proposedIdentity;
            identitiesByPlayer[proposedIdentity.PlayerId] = proposedIdentity;
            return Task.FromResult(proposedIdentity);
        }
    }

    public Task CreateRefreshSessionAsync(RefreshSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) sessions.Add(session.SessionId, session);
        return Task.CompletedTask;
    }

    public Task<RefreshRotationResult> RotateRefreshSessionAsync(
        string currentSessionId,
        byte[] currentTokenHash,
        RefreshSession replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!sessions.TryGetValue(currentSessionId, out var current))
                return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.NotFound, null));
            if (!FixedTimeEquals(current.TokenHash, currentTokenHash))
                return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.Invalid, null));
            if (current.RevokedAtUtc is not null)
                return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.Revoked, null));
            if (current.ExpiresAtUtc <= now)
                return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.Expired, null));
            if (!identitiesByPlayer.TryGetValue(current.PlayerId, out var identity))
                return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.NotFound, null));

            sessions[currentSessionId] = current with { RevokedAtUtc = now };
            sessions.Add(replacement.SessionId, replacement with { PlayerId = current.PlayerId });
            return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.Rotated, identity));
        }
    }

    public Task<bool> RevokeRefreshSessionAsync(
        string sessionId,
        byte[] tokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var session)
                || session.RevokedAtUtc is not null
                || !FixedTimeEquals(session.TokenHash, tokenHash)) return Task.FromResult(false);
            sessions[sessionId] = session with { RevokedAtUtc = now };
            return Task.FromResult(true);
        }
    }

    public Task<AdminRevokePlayerSessionsResult> RevokePlayerSessionsAsync(
        string commandId,
        string playerId,
        DateTimeOffset effectiveAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (adminCommands.TryGetValue(commandId, out var existing))
                return Task.FromResult(existing with { Duplicate = true });
            var found = identitiesByPlayer.ContainsKey(playerId);
            var revoked = 0;
            foreach (var session in sessions.Values
                         .Where(item => item.PlayerId == playerId
                             && item.RevokedAtUtc is null
                             && item.ExpiresAtUtc > effectiveAtUtc)
                         .ToArray())
            {
                sessions[session.SessionId] = session with
                {
                    RevokedAtUtc = effectiveAtUtc
                };
                revoked++;
            }
            var result = new AdminRevokePlayerSessionsResult(
                commandId,
                playerId,
                found,
                revoked,
                effectiveAtUtc,
                false);
            adminCommands[commandId] = result;
            return Task.FromResult(result);
        }
    }

    public Task RecordLoginAsync(AuthLoginEvent loginEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            loginEvents.Add(loginEvent);
            if (loginEvents.Count > 10_000) loginEvents.RemoveRange(0, loginEvents.Count - 10_000);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlayerDirectoryItem>> ListPlayersAsync(
        string? search,
        int limit,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var normalized = search?.Trim() ?? string.Empty;
            IReadOnlyList<PlayerDirectoryItem> result = identitiesByPlayer.Values
                .Where(identity => normalized.Length == 0
                    || identity.PlayerId.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || identity.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(identity => identity.UpdatedAtUtc)
                .Take(limit)
                .Select(identity => BuildDirectoryItem(identity, now))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<PlayerDirectoryDetail?> GetPlayerDetailAsync(
        string playerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!identitiesByPlayer.TryGetValue(playerId, out var identity))
                return Task.FromResult<PlayerDirectoryDetail?>(null);
            var history = loginEvents
                .Where(item => item.PlayerId == playerId)
                .OrderByDescending(item => item.OccurredAtUtc)
                .Take(200)
                .ToArray();
            var monitoredSessions = sessions.Values
                .Where(item => item.PlayerId == playerId)
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(100)
                .Select(item => MapSession(item, now))
                .ToArray();
            return Task.FromResult<PlayerDirectoryDetail?>(new PlayerDirectoryDetail(
                BuildDirectoryItem(identity, now),
                monitoredSessions,
                history,
                history.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).ToArray()));
        }
    }

    private PlayerDirectoryItem BuildDirectoryItem(AuthIdentity identity, DateTimeOffset now)
    {
        var lastLogin = loginEvents
            .Where(item => item.PlayerId == identity.PlayerId && item.Outcome == "Success")
            .MaxBy(item => item.OccurredAtUtc);
        var activeSessions = sessions.Values.Count(item =>
            item.PlayerId == identity.PlayerId
            && item.RevokedAtUtc is null
            && item.ExpiresAtUtc > now);
        return new PlayerDirectoryItem(
            identity.PlayerId,
            identity.DisplayName,
            identity.Provider,
            "Active",
            identity.CreatedAtUtc,
            identity.UpdatedAtUtc,
            lastLogin?.OccurredAtUtc,
            lastLogin?.DeviceId,
            lastLogin?.MaskedIp,
            activeSessions);
    }

    private static AuthSessionMonitor MapSession(RefreshSession session, DateTimeOffset now) =>
        new(
            $"{session.SessionId[..8]}…",
            session.CreatedAtUtc,
            session.ExpiresAtUtc,
            session.RevokedAtUtc,
            session.RevokedAtUtc is null && session.ExpiresAtUtc > now);

    private static bool FixedTimeEquals(byte[] left, byte[] right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}
