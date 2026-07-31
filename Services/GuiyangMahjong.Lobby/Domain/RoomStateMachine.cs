namespace GuiyangMahjong.Lobby.Domain;

/// <summary>
/// 房间控制面状态转换白名单。
/// 所有命令必须通过本类型迁移并递增 StateVersion，禁止按枚举数值或直接赋值绕过约束。
/// </summary>
public static class RoomStateMachine
{
    private static readonly IReadOnlyDictionary<RoomLifecycle, RoomLifecycle[]> Allowed =
        new Dictionary<RoomLifecycle, RoomLifecycle[]>
        {
            [RoomLifecycle.Created] =
                [RoomLifecycle.Waiting, RoomLifecycle.Allocating, RoomLifecycle.Terminating, RoomLifecycle.Aborted],
            [RoomLifecycle.Waiting] =
                [RoomLifecycle.Ready, RoomLifecycle.Allocating, RoomLifecycle.Playing, RoomLifecycle.Recovering,
                    RoomLifecycle.Terminating, RoomLifecycle.Aborted, RoomLifecycle.Finished],
            [RoomLifecycle.Ready] =
                [RoomLifecycle.Waiting, RoomLifecycle.Allocating, RoomLifecycle.Starting, RoomLifecycle.Recovering,
                    RoomLifecycle.Terminating,
                    RoomLifecycle.Aborted],
            [RoomLifecycle.Allocating] =
                [RoomLifecycle.Waiting, RoomLifecycle.Starting, RoomLifecycle.Recovering,
                    RoomLifecycle.Terminating, RoomLifecycle.Aborted, RoomLifecycle.Finished],
            [RoomLifecycle.Starting] =
                [RoomLifecycle.Playing, RoomLifecycle.Recovering, RoomLifecycle.Terminating, RoomLifecycle.Aborted],
            [RoomLifecycle.Playing] =
                [RoomLifecycle.Suspended, RoomLifecycle.Recovering, RoomLifecycle.Settling,
                    RoomLifecycle.Terminating, RoomLifecycle.Aborted],
            [RoomLifecycle.Suspended] =
                [RoomLifecycle.Playing, RoomLifecycle.Recovering, RoomLifecycle.Terminating, RoomLifecycle.Aborted],
            [RoomLifecycle.Recovering] =
                [RoomLifecycle.Allocating, RoomLifecycle.Starting, RoomLifecycle.Playing,
                    RoomLifecycle.Terminating, RoomLifecycle.Aborted],
            [RoomLifecycle.Settling] =
                [RoomLifecycle.Recovering, RoomLifecycle.Finished, RoomLifecycle.Terminating, RoomLifecycle.Aborted],
            [RoomLifecycle.Finished] = [RoomLifecycle.Archived],
            [RoomLifecycle.Terminating] =
                [RoomLifecycle.Finished, RoomLifecycle.Aborted, RoomLifecycle.Archived],
            [RoomLifecycle.Aborted] = [RoomLifecycle.Finished, RoomLifecycle.Archived],
            [RoomLifecycle.Archived] = []
        };

    /// <summary>判断转换是否幂等或位于白名单；不修改房间。</summary>
    public static bool CanTransition(RoomLifecycle from, RoomLifecycle to) =>
        from == to
        || (Allowed.TryGetValue(from, out var allowed) && allowed.Contains(to));

    /// <summary>
    /// 返回数据库和内部审计使用的规范状态名。
    /// 不能依赖带别名枚举的 ToString 选择结果，否则 Closed/Finished 等值可能随运行时实现变化。
    /// </summary>
    public static string ToCanonicalName(RoomLifecycle lifecycle) => lifecycle switch
    {
        RoomLifecycle.Created => nameof(RoomLifecycle.Created),
        RoomLifecycle.Waiting => nameof(RoomLifecycle.Waiting),
        RoomLifecycle.Ready => nameof(RoomLifecycle.Ready),
        RoomLifecycle.Allocating => nameof(RoomLifecycle.Allocating),
        RoomLifecycle.Starting => nameof(RoomLifecycle.Starting),
        RoomLifecycle.Playing => nameof(RoomLifecycle.Playing),
        RoomLifecycle.Suspended => nameof(RoomLifecycle.Suspended),
        RoomLifecycle.Recovering => nameof(RoomLifecycle.Recovering),
        RoomLifecycle.Settling => nameof(RoomLifecycle.Settling),
        RoomLifecycle.Finished => nameof(RoomLifecycle.Finished),
        RoomLifecycle.Terminating => nameof(RoomLifecycle.Terminating),
        RoomLifecycle.Aborted => nameof(RoomLifecycle.Aborted),
        RoomLifecycle.Archived => nameof(RoomLifecycle.Archived),
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle))
    };

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
