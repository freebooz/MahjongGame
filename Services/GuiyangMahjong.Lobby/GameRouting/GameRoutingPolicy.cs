using GuiyangMahjong.Lobby.Domain;

namespace GuiyangMahjong.Lobby.GameRouting;

/// <summary>
/// 房间到 Dedicated Server 的代际路由策略。
/// RoomEpoch 是路由 fencing token：新实例分配时递增，旧实例携带的较小 Epoch 永远不能覆盖新路由。
/// </summary>
public static class GameRoutingPolicy
{
    /// <summary>
    /// 验证 DS 上报 Epoch。
    /// 初始 Epoch 允许旧版 DS 缺省为 0；发生过重新分配后必须显式携带完全一致的 Epoch。
    /// </summary>
    public static bool AcceptsEpoch(
        long currentRoomEpoch,
        long reportedRoomEpoch,
        bool allowLegacyInitialEpoch = true) =>
        currentRoomEpoch == 1
            ? reportedRoomEpoch == 1
              || (allowLegacyInitialEpoch && reportedRoomEpoch == 0)
            : reportedRoomEpoch == currentRoomEpoch;

    /// <summary>
    /// 创建下一代路由快照并清除旧实例绑定。
    /// 调用方仍须通过 StateVersion 条件写入，单独递增 Epoch 不能替代数据库乐观并发。
    /// </summary>
    public static LobbyRoom BeginReallocation(
        LobbyRoom room,
        TimeProvider timeProvider)
    {
        var recovering = RoomStateMachine.Transition(
            room,
            RoomLifecycle.Recovering,
            timeProvider);
        return recovering with
        {
            RoomEpoch = checked(room.RoomEpoch + 1),
            Route = null,
            PendingServerInstanceId = null,
            LastServerInstanceId =
                room.Route?.ServerInstanceId
                ?? room.PendingServerInstanceId
                ?? room.LastServerInstanceId
        };
    }
}
