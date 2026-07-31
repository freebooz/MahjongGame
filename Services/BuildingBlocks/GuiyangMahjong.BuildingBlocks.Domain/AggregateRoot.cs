using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;

namespace GuiyangMahjong.BuildingBlocks.Domain;

/// <summary>
/// 领域聚合基础类，只管理乐观版本和待提交事件。
/// 派生业务聚合必须在业务事务成功后调用 ClearUncommittedEvents，失败事务不得发布事件。
/// </summary>
public abstract class AggregateRoot<TId>
    where TId : struct, IStrongValue
{
    private readonly List<EventEnvelope> uncommittedEvents = [];

    /// <summary>聚合稳定标识；创建后不得变化。</summary>
    public abstract TId Id { get; }

    /// <summary>最后持久化版本；每个成功状态迁移只增加一次。</summary>
    public StateVersion Version { get; private set; } = StateVersion.Parse(0);

    /// <summary>当前事务尚未写入 Outbox 的领域事件只读视图。</summary>
    public IReadOnlyList<EventEnvelope> UncommittedEvents => uncommittedEvents;

    /// <summary>
    /// 暂存与新聚合版本绑定的事件。
    /// 调用前业务状态必须已通过领域不变量校验，事件不得直接发送到消息中间件。
    /// </summary>
    protected void Raise(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var expectedVersion = Version.Value + 1;
        if (envelope.AggregateVersion != expectedVersion)
        {
            throw new InvalidOperationException(
                $"事件聚合版本必须为 {expectedVersion}。");
        }
        Version = StateVersion.Parse(expectedVersion);
        uncommittedEvents.Add(envelope);
    }

    /// <summary>
    /// 在业务数据和 Outbox 同一事务提交成功后清除暂存事件。
    /// 事务回滚或提交异常时不得调用，否则会丢失待重试事实。
    /// </summary>
    public void ClearUncommittedEvents() => uncommittedEvents.Clear();

    /// <summary>从持久化快照恢复版本，不产生事件；只允许仓储重建聚合时调用。</summary>
    protected void RestoreVersion(StateVersion version)
    {
        if (uncommittedEvents.Count != 0)
            throw new InvalidOperationException("存在未提交事件时不能恢复版本。");
        Version = version;
    }
}
