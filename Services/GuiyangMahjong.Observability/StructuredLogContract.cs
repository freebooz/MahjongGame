namespace GuiyangMahjong.Observability;

/// <summary>
/// 结构化日志 v1 的机器可读契约；CI 使用该列表阻止字段被意外删除或重命名。
/// </summary>
public static class StructuredLogContract
{
    /// <summary>每条日志必须存在的顶层字段，业务标识未知时写 JSON null。</summary>
    public static readonly string[] RequiredFields =
    [
        "Timestamp",
        "Level",
        "Service",
        "Environment",
        "TraceId",
        "RoomId",
        "PlayerId",
        "MatchId",
        "ServerInstanceId",
        "EventId",
        "Category",
        "Message",
        "Properties"
    ];
}
