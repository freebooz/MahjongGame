namespace GuiyangMahjong.Lobby.Matchmaking;

/// <summary>基础匹配票据状态；状态转换只允许 Pending→Reserved→Consumed 或进入终态。</summary>
public enum MatchmakingTicketState
{
    Pending,
    Reserved,
    Consumed,
    Expired,
    Cancelled
}

/// <summary>
/// 匹配票据权威快照。
/// Version 用于乐观并发；ReservationId 把一组原子保留绑定到同一候选分组，不能作为玩家凭据。
/// </summary>
public sealed record MatchmakingTicket(
    Guid TicketId,
    string PlayerId,
    string QueueName,
    MatchmakingTicketState State,
    long Version,
    Guid? ReservationId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ReservedAtUtc,
    DateTimeOffset? ConsumedAtUtc);

/// <summary>消费匹配票据的结果；Duplicate 表示同一 ReservationId 的安全重放。</summary>
public sealed record MatchmakingConsumeResult(
    bool Accepted,
    bool Duplicate,
    MatchmakingTicket? Ticket);

/// <summary>
/// 匹配票据权威存储。
/// 实现必须以 PostgreSQL 唯一约束或同等原子临界区防止同一玩家票据重复组局。
/// </summary>
public interface IMatchmakingTicketStore
{
    /// <summary>创建或返回玩家在队列中的现有活动票据；过期票据不能阻止新票据。</summary>
    Task<MatchmakingTicket> CreateAsync(
        string playerId,
        string queueName,
        TimeSpan timeToLive,
        CancellationToken cancellationToken);

    /// <summary>
    /// 按创建顺序原子保留指定数量候选。
    /// 数量不足时不保留任何票据，避免部分候选长期悬挂。
    /// </summary>
    Task<IReadOnlyList<MatchmakingTicket>> ReserveAsync(
        string queueName,
        int count,
        Guid reservationId,
        CancellationToken cancellationToken);

    /// <summary>消费已由同一 ReservationId 保留的票据；重复消费返回 Duplicate。</summary>
    Task<MatchmakingConsumeResult> ConsumeAsync(
        Guid ticketId,
        Guid reservationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 匹配搜索窗口；Attributes 只能包含低基数、非敏感的服务端规则条件，不能放入 IP、Token 或私有手牌。
/// </summary>
public sealed record MatchmakingSearchWindow(
    string QueueName,
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// 匹配扩圈策略接口。
/// 阶段 4 只提供接口和固定窗口实现，不包含段位、赛事或跨地域选择逻辑。
/// </summary>
public interface IMatchmakingExpansionPolicy
{
    /// <summary>根据等待时间返回下一搜索窗口；输入和输出都不得修改权威票据状态。</summary>
    MatchmakingSearchWindow Expand(MatchmakingSearchWindow current);
}

/// <summary>
/// 阶段 4 固定匹配窗口实现。
/// 它保留当前队列和属性，用作后续扩圈策略的兼容默认值。
/// </summary>
public sealed class FixedMatchmakingExpansionPolicy
    : IMatchmakingExpansionPolicy
{
    /// <inheritdoc/>
    public MatchmakingSearchWindow Expand(
        MatchmakingSearchWindow current) => current;
}
