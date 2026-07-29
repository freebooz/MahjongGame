namespace GuiyangMahjong.Admin.Domain;

/// <summary>可注册的监控来源类型；v1 仅允许 Lobby 与 Allocator。</summary>
public enum MonitoringSourceKind
{
    Lobby,
    Allocator
}

/// <summary>
/// 服务注册租约；Generation 在实例重建时单调增加，RegistrationId 标识一次具体进程生命周期。
/// </summary>
public sealed record MonitoringSourceRegistration(
    string RegistrationId,
    string SourceId,
    MonitoringSourceKind Kind,
    string RegionId,
    string ClusterId,
    string LobbyId,
    string NodeId,
    string BaseUrl,
    long Generation,
    DateTimeOffset RegisteredAtUtc);

/// <summary>注册目录返回的来源状态；Conflict 来源不会参与查询或管理路由。</summary>
public sealed record MonitoringSourceLease(
    MonitoringSourceRegistration Registration,
    DateTimeOffset ExpiresAtUtc,
    string Status,
    string? ConflictWith);
