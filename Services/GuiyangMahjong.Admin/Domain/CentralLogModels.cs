namespace GuiyangMahjong.Admin.Domain;

/// <summary>
/// Admin 从 Loki 查询网关接收的脱敏结构化日志；不包含原始标签集合或任意证据正文。
/// </summary>
/// <param name="Timestamp">日志 UTC 时间。</param>
/// <param name="Level">受控日志级别。</param>
/// <param name="Service">来源服务。</param>
/// <param name="TraceId">业务 TraceId 或技术 TraceId。</param>
/// <param name="RoomId">房间标识。</param>
/// <param name="PlayerId">玩家标识；仅在已批准的目标范围内返回。</param>
/// <param name="MatchId">比赛标识。</param>
/// <param name="ServerInstanceId">Dedicated Server 实例标识。</param>
/// <param name="EventId">幂等事件标识。</param>
/// <param name="Message">已经过服务端和 Collector 双层脱敏的日志摘要。</param>
public sealed record CentralLogRecord(
    DateTimeOffset Timestamp,
    string Level,
    string Service,
    string? TraceId,
    string? RoomId,
    string? PlayerId,
    string? MatchId,
    string? ServerInstanceId,
    string? EventId,
    string Message);
