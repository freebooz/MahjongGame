namespace GuiyangMahjong.Lobby.Domain;

/// <summary>
/// 玩家参与房间的权威历史投影；时间均为 UTC，离开时间为空表示仍在该房间。
/// 该模型只包含调查所需标识，不持久化设备、IP 或聊天正文。
/// </summary>
public sealed record PlayerRoomHistoryRecord(
    string PlayerId,
    string RoomId,
    string MatchId,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset? LeftAtUtc,
    string? LeaveReason);

/// <summary>
/// 玩家连接状态变更的不可变历史；EventId 与房间事件证据使用同一幂等标识。
/// </summary>
public sealed record PlayerConnectionHistoryRecord(
    string EventId,
    string PlayerId,
    string RoomId,
    string? MatchId,
    string? FromState,
    string ToState,
    bool? Trustee,
    DateTimeOffset OccurredAtUtc,
    string TraceId);

/// <summary>
/// 内部历史查询页；下一页边界由不可变时间与稳定标识共同组成。
/// </summary>
public sealed record PlayerHistoryPage<T>(
    T[] Items,
    DateTimeOffset? NextBeforeAtUtc,
    string? NextBeforeId);
