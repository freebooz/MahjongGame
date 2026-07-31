using GuiyangMahjong.Lobby.Domain;

namespace GuiyangMahjong.Lobby.Rooms;

/// <summary>
/// 房间只读端口，供大厅聚合、重连和管理查询复用。
/// 该接口不得暴露密码、结算凭据或缓存实现，调用方也不得据此绕过房间命令修改聚合。
/// </summary>
public interface IRoomReader
{
    /// <summary>按公开房间号读取权威快照；不存在时返回 null，不改变缓存或租约。</summary>
    Task<LobbyRoom?> GetRoomByCodeAsync(string roomCode, CancellationToken cancellationToken);

    /// <summary>按内部房间标识读取权威快照；不存在时返回 null。</summary>
    Task<LobbyRoom?> GetRoomByIdAsync(string roomId, CancellationToken cancellationToken);

    /// <summary>读取玩家当前唯一活动房间；终态房间不得作为活动映射返回。</summary>
    Task<LobbyRoom?> GetActiveRoomByPlayerAsync(string playerId, CancellationToken cancellationToken);

    /// <summary>读取允许公开发现的房间投影来源；实现必须过滤终态与禁止加入房间。</summary>
    Task<IReadOnlyList<LobbyRoom>> ListPublicRoomsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 房间写模型端口，集中约束创建、成员变更和乐观并发更新。
/// PostgreSQL 实现必须在同一事务中维护房间快照、成员座位和玩家活动房间唯一约束。
/// </summary>
public interface IRoomWriter
{
    /// <summary>原子创建房间及房主成员关系；房号或玩家活动租约冲突不得留下部分数据。</summary>
    Task<Storage.CreateRoomResult> TryCreateRoomAsync(
        LobbyRoom room,
        CancellationToken cancellationToken);

    /// <summary>原子加入玩家并分配确定性座位；重复加入返回同一房间快照。</summary>
    Task<Storage.AddPlayerResult> TryAddPlayerAsync(
        string roomCode,
        string playerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以快照内的前一版本为条件更新房间。
    /// 陈旧版本返回 false；成功时 StateVersion 必须严格增加且 RoomEpoch 不得倒退。
    /// </summary>
    Task<bool> UpdateRoomAsync(LobbyRoom room, CancellationToken cancellationToken);
}

/// <summary>
/// 房间成员的显式座位快照。
/// SeatIndex 从 0 开始且在单个 RoomEpoch 内稳定，JoinedAtUtc 使用服务端时间。
/// </summary>
public sealed record RoomSeat(
    string PlayerId,
    int SeatIndex,
    DateTimeOffset JoinedAtUtc);

/// <summary>
/// 房间状态转换记录，用于解释管理命令、DS 生命周期和恢复过程。
/// 记录只包含脱敏元数据，不包含手牌、加入票据或内部凭据。
/// </summary>
public sealed record RoomStateHistoryEntry(
    string EventId,
    string RoomId,
    RoomLifecycle From,
    RoomLifecycle To,
    long StateVersion,
    long RoomEpoch,
    string Reason,
    string TraceId,
    DateTimeOffset OccurredAtUtc);
