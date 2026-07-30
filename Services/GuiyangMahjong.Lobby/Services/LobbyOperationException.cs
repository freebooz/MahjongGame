// Lobby 领域异常：携带稳定错误码、用户可读消息和 HTTP 状态，供异常中间件统一转换。
// 消息不得包含连接串、令牌、内部堆栈或其他敏感实现细节。
using GuiyangMahjong.Lobby.Domain;

namespace GuiyangMahjong.Lobby.Services;

/// <summary>
/// Lobby 可预期业务失败。
/// 同时携带领域枚举、HTTP 状态和可选重试建议，由统一异常中间件生成稳定响应；
/// 构造消息必须可安全展示给客户端。
/// </summary>
public sealed class LobbyOperationException : Exception
{
    /// <summary>创建一个可映射的 Lobby 错误；retryAfterMilliseconds 单位为毫秒且仅用于瞬态失败。</summary>
    public LobbyOperationException(
        LobbyErrorCode errorCode,
        string chineseMessage,
        int statusCode,
        int? retryAfterMilliseconds = null)
        : base(chineseMessage)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        RetryAfterMilliseconds = retryAfterMilliseconds;
    }

    /// <summary>服务内部的强类型错误分类。</summary>
    public LobbyErrorCode ErrorCode { get; }

    /// <summary>建议由异常边界返回的 HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>客户端重试前建议等待的毫秒数；不可重试错误为空。</summary>
    public int? RetryAfterMilliseconds { get; }

    /// <summary>跨版本稳定的机器错误码；未知枚举安全收敛为 INTERNAL_ERROR。</summary>
    public string StableCode => ErrorCode switch
    {
        LobbyErrorCode.InvalidRequest => "INVALID_REQUEST",
        LobbyErrorCode.SessionExpired => "SESSION_EXPIRED",
        LobbyErrorCode.RequestInProgress => "REQUEST_IN_PROGRESS",
        LobbyErrorCode.RoomNotFound => "ROOM_NOT_FOUND",
        LobbyErrorCode.RoomFull => "ROOM_FULL",
        LobbyErrorCode.RoomClosed => "ROOM_CLOSED",
        LobbyErrorCode.PasswordRequired => "PASSWORD_REQUIRED",
        LobbyErrorCode.WrongPassword => "WRONG_PASSWORD",
        LobbyErrorCode.RateLimited => "RATE_LIMITED",
        LobbyErrorCode.ServerUnavailable => "SERVER_UNAVAILABLE",
        LobbyErrorCode.TicketExpired => "TICKET_EXPIRED",
        LobbyErrorCode.VersionMismatch => "VERSION_MISMATCH",
        LobbyErrorCode.Timeout => "TIMEOUT",
        LobbyErrorCode.Cancelled => "CANCELLED",
        LobbyErrorCode.BackendNotConfigured => "BACKEND_NOT_CONFIGURED",
        _ => "INTERNAL_ERROR"
    };
}
