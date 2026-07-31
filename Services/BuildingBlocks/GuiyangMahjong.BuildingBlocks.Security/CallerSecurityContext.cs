using GuiyangMahjong.Contracts.Common;

namespace GuiyangMahjong.BuildingBlocks.Security;

/// <summary>跨服务调用者类别；类型用于授权策略选择，不代表具体权限集合。</summary>
public enum CallerKind
{
    Anonymous,
    Player,
    Service,
    Administrator
}

/// <summary>
/// 已验证调用者的最小安全上下文。
/// 不保存 Access Token、Refresh Token、Join Ticket 或未经脱敏的设备/IP 数据。
/// </summary>
public sealed record CallerSecurityContext(
    CallerKind Kind,
    PlayerId? PlayerId,
    SessionId? SessionId,
    string? ServiceName,
    IReadOnlySet<string> Permissions)
{
    /// <summary>验证类别与身份字段一致性，防止匿名调用伪装成带 PlayerId 的主体。</summary>
    public void Validate()
    {
        if (Kind == CallerKind.Player
            && (PlayerId is null || SessionId is null))
            throw new InvalidOperationException("玩家上下文缺少 PlayerId 或 SessionId。");
        if (Kind == CallerKind.Service
            && !StrongValueValidation.IsIdentifier(ServiceName))
            throw new InvalidOperationException("服务上下文缺少有效 ServiceName。");
        if (Kind == CallerKind.Anonymous
            && (PlayerId is not null
                || SessionId is not null
                || Permissions.Count != 0))
            throw new InvalidOperationException("匿名上下文不能携带身份或权限。");
    }

    /// <summary>仅输出类别和脱敏身份，供结构化日志使用。</summary>
    public IReadOnlyDictionary<string, object?> ToSafeLogProperties() =>
        new Dictionary<string, object?>
        {
            ["caller_kind"] = Kind.ToString(),
            ["player"] = PlayerId?.ToSafeLogString(),
            ["session"] = SessionId?.ToSafeLogString(),
            ["service"] = ServiceName,
            ["permission_count"] = Permissions.Count
        };
}
