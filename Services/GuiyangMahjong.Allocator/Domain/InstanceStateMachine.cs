namespace GuiyangMahjong.Allocator.Domain;

/// <summary>
/// Dedicated Server 实例状态转换白名单。
/// 所有管理器写入必须经此类型，禁止依赖枚举数值顺序或直接赋值绕过终态约束。
/// </summary>
internal static class InstanceStateMachine
{
    private static readonly IReadOnlyDictionary<GameServerInstanceState, GameServerInstanceState[]> Allowed =
        new Dictionary<GameServerInstanceState, GameServerInstanceState[]>
        {
            [GameServerInstanceState.Starting] = [GameServerInstanceState.Ready, GameServerInstanceState.Failed],
            [GameServerInstanceState.Ready] = [GameServerInstanceState.Allocated, GameServerInstanceState.Failed],
            [GameServerInstanceState.Allocated] = [GameServerInstanceState.Draining, GameServerInstanceState.Failed],
            [GameServerInstanceState.Draining] = [GameServerInstanceState.Stopped, GameServerInstanceState.Failed],
            [GameServerInstanceState.Stopped] = [],
            [GameServerInstanceState.Failed] = [GameServerInstanceState.Stopped]
        };

    /// <summary>
    /// 幂等迁移实例到 next；相同状态无副作用，非法转换抛出异常且保持原状态。
    /// 调用方负责在同步边界内持久化迁移。
    /// </summary>
    public static void Transition(GameServerInstance instance, GameServerInstanceState next)
    {
        if (instance.State == next) return;
        if (!Allowed[instance.State].Contains(next))
        {
            throw new InvalidOperationException($"实例状态不允许从 {instance.State} 转换到 {next}");
        }
        instance.State = next;
    }
}
