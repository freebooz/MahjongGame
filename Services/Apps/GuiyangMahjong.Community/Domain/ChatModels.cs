namespace GuiyangMahjong.Community.Domain;

/// <summary>聊天消息发送前授权请求；只携带身份和上下文标识，禁止包含消息正文。</summary>
public sealed record AuthorizeChatMessageRequest(
    string MessageId,
    string PlayerId,
    string RoomId,
    DateTimeOffset RequestedAtUtc);

/// <summary>聊天策略判定；禁言截止时间统一使用 UTC，Reason 仅包含可安全返回的分类原因。</summary>
public sealed record ChatPolicyResult(
    string PlayerId,
    bool Allowed,
    DateTimeOffset? MutedUntilUtc,
    string Reason);

/// <summary>Community 稳定领域错误；失败响应不泄露 Auth 地址、凭据或内部异常堆栈。</summary>
public sealed class CommunityOperationException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
