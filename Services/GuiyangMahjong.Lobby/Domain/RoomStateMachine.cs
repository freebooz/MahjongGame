namespace GuiyangMahjong.Lobby.Domain;

/// <summary>房间状态转换白名单；禁止通过数值顺序绕过状态约束。</summary>
public static class RoomStateMachine
{
    private static readonly IReadOnlyDictionary<RoomLifecycle, RoomLifecycle[]> Allowed =
        new Dictionary<RoomLifecycle, RoomLifecycle[]>
        {
            [RoomLifecycle.Creating] = [RoomLifecycle.Allocating, RoomLifecycle.Failed],
            [RoomLifecycle.Allocating] = [RoomLifecycle.Waiting, RoomLifecycle.Failed, RoomLifecycle.Closed],
            [RoomLifecycle.Waiting] = [RoomLifecycle.Playing, RoomLifecycle.Closed, RoomLifecycle.Failed],
            [RoomLifecycle.Playing] = [RoomLifecycle.Settling, RoomLifecycle.Failed],
            [RoomLifecycle.Settling] = [RoomLifecycle.Closed, RoomLifecycle.Failed],
            [RoomLifecycle.Closed] = [],
            [RoomLifecycle.Failed] = [RoomLifecycle.Closed]
        };

    /// <summary>判断转换是否幂等或位于白名单；不修改房间。</summary>
    public static bool CanTransition(RoomLifecycle from, RoomLifecycle to) =>
        from == to || Allowed[from].Contains(to);

    /// <summary>
    /// 创建迁移后的房间快照，成功时序号递增并更新服务端 UTC 时间；
    /// 非法转换抛出异常且不改变输入记录。
    /// </summary>
    public static LobbyRoom Transition(LobbyRoom room, RoomLifecycle next, TimeProvider timeProvider)
    {
        if (!CanTransition(room.Lifecycle, next))
        {
            throw new InvalidOperationException($"不允许房间状态从 {room.Lifecycle} 转换到 {next}");
        }

        return room with
        {
            Lifecycle = next,
            StateSequence = room.StateSequence + 1,
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };
    }
}
