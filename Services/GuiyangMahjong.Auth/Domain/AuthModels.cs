namespace GuiyangMahjong.Auth.Domain;

public sealed record GuestLoginRequest(string InstallationId, string? DisplayName);
public sealed record RefreshSessionRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);
public sealed record LoginObservation(string MaskedIp, string ClientSummary);

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
    int ActiveSessionCount);

public sealed record PlayerDirectoryDetail(
    PlayerDirectoryItem Player,
    AuthSessionMonitor[] Sessions,
    AuthLoginEvent[] LoginHistory,
    string[] KnownDeviceIds);

public enum RefreshRotationStatus { Rotated, NotFound, Invalid, Expired, Revoked }
public sealed record RefreshRotationResult(RefreshRotationStatus Status, AuthIdentity? Identity);
