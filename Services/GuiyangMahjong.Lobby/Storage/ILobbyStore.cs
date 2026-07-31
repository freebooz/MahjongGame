using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Rooms;

namespace GuiyangMahjong.Lobby.Storage;

/// <summary>创建房间的原子判定；房间码冲突与玩家已有活动房间需由调用方分别处理。</summary>
public enum CreateRoomStatus
{
    Created,
    RoomCodeConflict,
    PlayerAlreadyActive
}

/// <summary>创建结果；PlayerAlreadyActive 时 ActiveRoom 返回冲突玩家当前房间。</summary>
public sealed record CreateRoomResult(CreateRoomStatus Status, LobbyRoom? ActiveRoom = null);

/// <summary>加入房间的原子判定，覆盖幂等成员、容量、准入控制和跨房间唯一性。</summary>
public enum AddPlayerStatus
{
    Added,
    AlreadyMember,
    RoomNotFound,
    RoomClosed,
    RoomFull,
    AdmissionProhibited,
    AlreadyInAnotherRoom
}

/// <summary>加入结果；成功、已是成员或业务拒绝时可携带判断时的房间快照。</summary>
public sealed record AddPlayerResult(AddPlayerStatus Status, LobbyRoom? Room);

/// <summary>比赛结果最终化判定；Duplicate 仅表示同序号同载荷，Conflict 表示不可接受的重用。</summary>
public enum FinalizeMatchStatus
{
    Accepted,
    Duplicate,
    Conflict
}

/// <summary>
/// Lobby 房间、成员索引和比赛结果的权威存储边界。
/// 生产实现必须在事务内维护“玩家最多一个活动房间”、StateSequence 乐观更新和结果幂等，
/// 缓存只能加速读取，不能成为覆盖 PostgreSQL 权威状态的来源。
/// </summary>
public interface ILobbyStore : IRoomReader, IRoomWriter
{
    /// <summary>初始化或验证持久化结构；失败时 Lobby 不得进入就绪状态。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>检查权威存储与必要缓存可用性，不改变房间状态。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>原子创建房间并建立活动玩家索引；房间码或玩家冲突不会留下部分记录。</summary>
    new Task<CreateRoomResult> TryCreateRoomAsync(LobbyRoom room, CancellationToken cancellationToken);

    /// <summary>按公开房间短码读取当前快照；不存在返回空。</summary>
    new Task<LobbyRoom?> GetRoomByCodeAsync(string roomCode, CancellationToken cancellationToken);

    /// <summary>按内部 RoomId 读取当前快照；不存在返回空。</summary>
    new Task<LobbyRoom?> GetRoomByIdAsync(string roomId, CancellationToken cancellationToken);

    /// <summary>读取玩家当前唯一活动房间；终态房间不应返回。</summary>
    new Task<LobbyRoom?> GetActiveRoomByPlayerAsync(string playerId, CancellationToken cancellationToken);
    /// <summary>
    /// 批量查询玩家当前活动房间；用于监控分页，避免逐玩家查询或扫描全部房间。
    /// 返回字典只包含当前确实位于活动房间中的玩家。
    /// </summary>
    Task<IReadOnlyDictionary<string, LobbyRoom>> GetActiveRoomsByPlayersAsync(
        IReadOnlyCollection<string> playerIds,
        CancellationToken cancellationToken);

    /// <summary>返回可公开发现且允许加入的房间目录快照，不包含密码哈希和加入票据。</summary>
    new Task<IReadOnlyList<LobbyRoom>> ListPublicRoomsAsync(CancellationToken cancellationToken);
    /// <summary>按不可变创建时间与 RoomId 执行键集分页，避免状态更新时间变化扰动翻页边界。</summary>
    Task<IReadOnlyList<LobbyRoom>> ListRoomsForMonitoringAsync(
        int limit,
        DateTimeOffset? afterCreatedAtUtc,
        string? afterRoomId,
        string? lifecycle,
        string? gameMode,
        string? search,
        CancellationToken cancellationToken);

    /// <summary>
    /// 在加入前清理等待房间中已超过 staleBeforeUtc 的失联成员，
    /// 同时保留 prospectivePlayerId；成员和活动索引必须原子更新。
    /// </summary>
    Task<LobbyRoom?> ReconcileWaitingRoomMembersAsync(
        string roomCode,
        string prospectivePlayerId,
        DateTimeOffset staleBeforeUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以 Dedicated Server 权威连接集合刷新活动成员最后观察时间。
    /// 仅更新在线观察索引，不得擅自改变已开局玩家名单或结算参与者。
    /// </summary>
    Task RefreshConnectedPlayersAsync(
        string roomId,
        IReadOnlyCollection<string> connectedPlayerIds,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>原子加入玩家并递增状态序号；同玩家跨房间冲突必须返回明确状态。</summary>
    new Task<AddPlayerResult> TryAddPlayerAsync(
        string roomCode, string playerId, CancellationToken cancellationToken);

    /// <summary>
    /// 以 RoomId 和预期 StateSequence 语义更新房间快照。
    /// 陈旧写入返回 false，成功时同步活动玩家索引和缓存。
    /// </summary>
    new Task<bool> UpdateRoomAsync(LobbyRoom room, CancellationToken cancellationToken);

    /// <summary>
    /// 原子保存权威比赛结果并关闭房间、释放活动玩家索引。
    /// 相同 ResultSequence/载荷可重放，不同载荷或倒退序号必须返回 Conflict。
    /// </summary>
    Task<FinalizeMatchStatus> FinalizeMatchAsync(
        LobbyRoom closedRoom, MatchResultReport report, CancellationToken cancellationToken);
}
