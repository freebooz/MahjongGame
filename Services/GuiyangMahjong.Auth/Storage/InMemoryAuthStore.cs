using System.Security.Cryptography;
using GuiyangMahjong.Auth.Domain;

namespace GuiyangMahjong.Auth.Storage;

/// <summary>
/// 单进程开发/测试用 Auth 存储。
/// gate 把身份、会话轮换、管理幂等和控制事件组成原子临界区；
/// 数据不持久化且无法跨副本共享，生产环境禁止注册此实现。
/// </summary>
public sealed class InMemoryAuthStore : IAuthStore
{
    // 各集合分别保存安装/玩家身份索引、会话、管理回执、控制状态/历史和登录事件。
    // 所有复合读写必须持有 gate，避免轮换和控制撤销交错产生两个有效会话。
    private readonly Dictionary<string, AuthIdentity> identitiesByInstallation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthIdentity> identitiesByPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RefreshSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AdminRevokePlayerSessionsResult> adminCommands =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerControlState> playerControls =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlayerControlEvent> playerControlEvents =
        new(StringComparer.Ordinal);
    private readonly List<AuthLoginEvent> loginEvents = [];
    private readonly object gate = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<SessionCreationStatus> CreateRefreshSessionAsync(
        RefreshSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var control = GetEffectiveControl(session.PlayerId, now);
            if (control.AccountStatus == "Banned")
                return Task.FromResult(SessionCreationStatus.Banned);
            if (control.AccountStatus == "Frozen")
                return Task.FromResult(SessionCreationStatus.Frozen);
            sessions.Add(session.SessionId, session);
            return Task.FromResult(SessionCreationStatus.Created);
        }
    }

    /// <inheritdoc/>
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
            var control = GetEffectiveControl(current.PlayerId, now);
            if (control.AccountStatus == "Banned")
                return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.Banned, null));
            if (control.AccountStatus == "Frozen")
                return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.Frozen, null));

            sessions[currentSessionId] = current with { RevokedAtUtc = now };
            sessions.Add(replacement.SessionId, replacement with { PlayerId = current.PlayerId });
            return Task.FromResult(new RefreshRotationResult(RefreshRotationStatus.Rotated, identity));
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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
                             && item.CreatedAtUtc <= effectiveAtUtc
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

    /// <inheritdoc/>
    public Task<AdminPlayerControlStoreResult> ApplyPlayerControlAsync(
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (playerControlEvents.TryGetValue(commandId, out var duplicate))
            {
                EnsureSameControlCommand(
                    duplicate,
                    playerId,
                    action,
                    expectedVersion,
                    reason,
                    traceId,
                    ticketId,
                    requestedBy,
                    approvedBy,
                    effectiveAtUtc,
                    expiresAtUtc,
                    riskLabel);
                return Task.FromResult(new AdminPlayerControlStoreResult(
                    AdminPlayerControlStatus.Duplicate,
                    ToControlResult(duplicate, true),
                    duplicate.AfterState,
                    null));
            }
            if (!identitiesByPlayer.ContainsKey(playerId))
            {
                return Task.FromResult(new AdminPlayerControlStoreResult(
                    AdminPlayerControlStatus.PlayerNotFound,
                    null,
                    null,
                    "Player was not found."));
            }
            var before = GetEffectiveControl(playerId, effectiveAtUtc);
            if (before.Version != expectedVersion)
            {
                return Task.FromResult(new AdminPlayerControlStoreResult(
                    AdminPlayerControlStatus.VersionConflict,
                    null,
                    before,
                    "Player control state changed."));
            }
            var transition = ApplyControlTransition(
                before,
                action,
                effectiveAtUtc,
                expiresAtUtc,
                riskLabel);
            if (transition.State is null)
            {
                return Task.FromResult(new AdminPlayerControlStoreResult(
                    AdminPlayerControlStatus.InvalidTransition,
                    null,
                    before,
                    transition.Error));
            }
            playerControls[playerId] = transition.State;
            var revoked = 0;
            if (action is AdminPlayerControlAction.TemporaryFreezePlayer
                or AdminPlayerControlAction.PermanentBanPlayer)
            {
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
            }
            var controlEvent = new PlayerControlEvent(
                commandId,
                playerId,
                action.ToString(),
                reason,
                traceId,
                ticketId,
                requestedBy,
                approvedBy,
                effectiveAtUtc,
                expiresAtUtc,
                riskLabel,
                revoked,
                before,
                transition.State);
            playerControlEvents.Add(commandId, controlEvent);
            return Task.FromResult(new AdminPlayerControlStoreResult(
                AdminPlayerControlStatus.Applied,
                ToControlResult(controlEvent, false, revoked),
                transition.State,
                null));
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task<IReadOnlyList<PlayerDirectoryItem>> ListPlayersAsync(
        string? search,
        int limit,
        DateTimeOffset? afterCreatedAtUtc,
        string? afterPlayerId,
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
                // 创建时间不随登录或风控变化，可避免翻页期间状态更新导致大面积重复或遗漏。
                .Where(identity => afterCreatedAtUtc is null
                    || identity.CreatedAtUtc < afterCreatedAtUtc
                    || (identity.CreatedAtUtc == afterCreatedAtUtc
                        && string.CompareOrdinal(identity.PlayerId, afterPlayerId) < 0))
                .OrderByDescending(identity => identity.CreatedAtUtc)
                .ThenByDescending(identity => identity.PlayerId, StringComparer.Ordinal)
                .Take(limit)
                .Select(identity => BuildDirectoryItem(identity, now))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
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
                history.Select(item => item.DeviceId).Distinct(StringComparer.Ordinal).ToArray(),
                playerControlEvents.Values
                    .Where(item => item.PlayerId == playerId)
                    .OrderByDescending(item => item.EffectiveAtUtc)
                    .Take(200)
                    .ToArray()));
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
        var control = GetEffectiveControl(identity.PlayerId, now);
        return new PlayerDirectoryItem(
            identity.PlayerId,
            identity.DisplayName,
            identity.Provider,
            control.AccountStatus,
            identity.CreatedAtUtc,
            identity.UpdatedAtUtc,
            lastLogin?.OccurredAtUtc,
            lastLogin?.DeviceId,
            lastLogin?.MaskedIp,
            activeSessions,
            control.Version,
            control.FrozenUntilUtc,
            control.MutedUntilUtc,
            control.RiskLabels);
    }

    private PlayerControlState GetEffectiveControl(
        string playerId,
        DateTimeOffset now)
    {
        var state = playerControls.GetValueOrDefault(playerId)
            ?? PlayerControlPolicy.Empty;
        return PlayerControlPolicy.Normalize(state, now);
    }

    private static (PlayerControlState? State, string? Error) ApplyControlTransition(
        PlayerControlState before,
        AdminPlayerControlAction action,
        DateTimeOffset effectiveAtUtc,
        DateTimeOffset? expiresAtUtc,
        string? riskLabel)
        => PlayerControlPolicy.Apply(
            before,
            action,
            effectiveAtUtc,
            expiresAtUtc,
            riskLabel);

    private static void EnsureSameControlCommand(
        PlayerControlEvent existing,
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
        string? riskLabel)
    {
        if (existing.PlayerId != playerId
            || existing.ActionType != action.ToString()
            || existing.BeforeState.Version != expectedVersion
            || existing.Reason != reason
            || existing.TraceId != traceId
            || existing.TicketId != ticketId
            || existing.RequestedBy != requestedBy
            || existing.ApprovedBy != approvedBy
            || (existing.EffectiveAtUtc - effectiveAtUtc).Duration()
                > TimeSpan.FromMilliseconds(1)
            || !SameInstant(existing.ExpiresAtUtc, expiresAtUtc)
            || existing.RiskLabel != riskLabel)
        {
            throw new InvalidOperationException(
                "Admin command id was reused with different command parameters.");
        }
    }

    private static bool SameInstant(
        DateTimeOffset? left,
        DateTimeOffset? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue
            || (left.Value - right!.Value).Duration()
                <= TimeSpan.FromMilliseconds(1));

    private static AdminUpdatePlayerControlResult ToControlResult(
        PlayerControlEvent controlEvent,
        bool duplicate,
        int? revokedSessionCount = null) =>
        new(
            controlEvent.CommandId,
            controlEvent.PlayerId,
            controlEvent.ActionType,
            controlEvent.BeforeState,
            controlEvent.AfterState,
            revokedSessionCount ?? controlEvent.RevokedSessionCount,
            duplicate);

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
