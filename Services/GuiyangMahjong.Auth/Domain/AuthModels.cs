namespace GuiyangMahjong.Auth.Domain;

public sealed record GuestLoginRequest(string InstallationId, string? DisplayName);
public sealed record RefreshSessionRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);
public sealed record LoginObservation(string MaskedIp, string ClientSummary);
public sealed record AdminRevokePlayerSessionsRequest(
    string Reason,
    string TraceId,
    DateTimeOffset EffectiveAtUtc);
public sealed record AdminRevokePlayerSessionsResult(
    string CommandId,
    string PlayerId,
    bool PlayerFound,
    int RevokedSessionCount,
    DateTimeOffset EffectiveAtUtc,
    bool Duplicate);

public enum AdminPlayerControlAction
{
    TemporaryFreezePlayer,
    PermanentBanPlayer,
    LiftPlayerBan,
    MutePlayer,
    UnmutePlayer,
    MarkRiskAccount
}

public sealed record AdminUpdatePlayerControlRequest(
    string ActionType,
    long ExpectedVersion,
    string Reason,
    string TraceId,
    string TicketId,
    string RequestedBy,
    string ApprovedBy,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? RiskLabel);

public sealed record PlayerControlState(
    long Version,
    string AccountStatus,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels,
    DateTimeOffset? RiskLabelsExpireAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PlayerControlEvent(
    string CommandId,
    string PlayerId,
    string ActionType,
    string Reason,
    string TraceId,
    string TicketId,
    string RequestedBy,
    string ApprovedBy,
    DateTimeOffset EffectiveAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? RiskLabel,
    int RevokedSessionCount,
    PlayerControlState BeforeState,
    PlayerControlState AfterState);

public enum AdminPlayerControlStatus
{
    Applied,
    Duplicate,
    PlayerNotFound,
    VersionConflict,
    InvalidTransition
}

public sealed record AdminUpdatePlayerControlResult(
    string CommandId,
    string PlayerId,
    string ActionType,
    PlayerControlState BeforeState,
    PlayerControlState AfterState,
    int RevokedSessionCount,
    bool Duplicate);

public sealed record AdminPlayerControlStoreResult(
    AdminPlayerControlStatus Status,
    AdminUpdatePlayerControlResult? Result,
    PlayerControlState? CurrentState,
    string? Error);

public sealed record AuthSessionResponse(
    string PlayerId,
    string DisplayName,
    string Provider,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);

public sealed record AuthIdentity(
    string PlayerId,
    string DisplayName,
    string Provider,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record RefreshSession(
    string SessionId,
    string PlayerId,
    byte[] TokenHash,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? RevokedAtUtc);

public sealed record AuthLoginEvent(
    string EventId,
    string PlayerId,
    string DeviceId,
    string MaskedIp,
    string ClientSummary,
    string Outcome,
    DateTimeOffset OccurredAtUtc);

public sealed record AuthSessionMonitor(
    string SessionReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    bool Active);

public sealed record PlayerDirectoryItem(
    string PlayerId,
    string DisplayName,
    string Provider,
    string AccountStatus,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastLoginAtUtc,
    string? CurrentDeviceId,
    string? CurrentMaskedIp,
    int ActiveSessionCount,
    long ControlVersion,
    DateTimeOffset? FrozenUntilUtc,
    DateTimeOffset? MutedUntilUtc,
    string[] RiskLabels);

public sealed record PlayerDirectoryDetail(
    PlayerDirectoryItem Player,
    AuthSessionMonitor[] Sessions,
    AuthLoginEvent[] LoginHistory,
    string[] KnownDeviceIds,
    PlayerControlEvent[] ControlHistory);

public enum SessionCreationStatus { Created, Frozen, Banned }
public enum RefreshRotationStatus
{
    Rotated,
    NotFound,
    Invalid,
    Expired,
    Revoked,
    Frozen,
    Banned
}
public sealed record RefreshRotationResult(RefreshRotationStatus Status, AuthIdentity? Identity);
