namespace GuiyangMahjong.Lobby.Matchmaking;

/// <summary>
/// 本地开发和单元测试使用的匹配票据存储。
/// 所有复合状态变更在同一锁中完成，用于模拟生产事务语义；数据随进程退出丢失。
/// </summary>
public sealed class InMemoryMatchmakingTicketStore(TimeProvider timeProvider)
    : IMatchmakingTicketStore
{
    private readonly Dictionary<Guid, MatchmakingTicket> tickets = [];
    private readonly object mutationGate = new();

    /// <inheritdoc/>
    public Task<MatchmakingTicket> CreateAsync(
        string playerId,
        string queueName,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(playerId, queueName, timeToLive);
        lock (mutationGate)
        {
            var now = timeProvider.GetUtcNow();
            ExpireUnsafe(now);
            var existing = tickets.Values.FirstOrDefault(ticket =>
                ticket.PlayerId == playerId
                && ticket.QueueName == queueName
                && ticket.State is
                    MatchmakingTicketState.Pending
                    or MatchmakingTicketState.Reserved);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var created = new MatchmakingTicket(
                Guid.NewGuid(),
                playerId,
                queueName,
                MatchmakingTicketState.Pending,
                1,
                null,
                now,
                now.Add(timeToLive),
                null,
                null);
            tickets.Add(created.TicketId, created);
            return Task.FromResult(created);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MatchmakingTicket>> ReserveAsync(
        string queueName,
        int count,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(queueName) || queueName.Length > 80)
            throw new ArgumentException("匹配队列名称无效。", nameof(queueName));
        if (count is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (reservationId == Guid.Empty)
            throw new ArgumentException("ReservationId 不能为空。", nameof(reservationId));

        lock (mutationGate)
        {
            var now = timeProvider.GetUtcNow();
            ExpireUnsafe(now);
            var candidates = tickets.Values
                .Where(ticket => ticket.QueueName == queueName
                    && ticket.State == MatchmakingTicketState.Pending)
                .OrderBy(ticket => ticket.CreatedAtUtc)
                .ThenBy(ticket => ticket.TicketId)
                .Take(count)
                .ToArray();
            if (candidates.Length != count)
            {
                return Task.FromResult<IReadOnlyList<MatchmakingTicket>>([]);
            }

            var reserved = candidates.Select(ticket => ticket with
            {
                State = MatchmakingTicketState.Reserved,
                Version = ticket.Version + 1,
                ReservationId = reservationId,
                ReservedAtUtc = now
            }).ToArray();
            foreach (var ticket in reserved)
            {
                tickets[ticket.TicketId] = ticket;
            }
            return Task.FromResult<IReadOnlyList<MatchmakingTicket>>(reserved);
        }
    }

    /// <inheritdoc/>
    public Task<MatchmakingConsumeResult> ConsumeAsync(
        Guid ticketId,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (mutationGate)
        {
            if (!tickets.TryGetValue(ticketId, out var ticket)
                || ticket.ReservationId != reservationId)
            {
                return Task.FromResult(new MatchmakingConsumeResult(false, false, ticket));
            }
            if (ticket.State == MatchmakingTicketState.Consumed)
            {
                return Task.FromResult(new MatchmakingConsumeResult(true, true, ticket));
            }
            if (ticket.State != MatchmakingTicketState.Reserved)
            {
                return Task.FromResult(new MatchmakingConsumeResult(false, false, ticket));
            }

            var consumed = ticket with
            {
                State = MatchmakingTicketState.Consumed,
                Version = ticket.Version + 1,
                ConsumedAtUtc = timeProvider.GetUtcNow()
            };
            tickets[ticketId] = consumed;
            return Task.FromResult(new MatchmakingConsumeResult(true, false, consumed));
        }
    }

    /// <summary>把已过期的活动票据转为终态，使同一玩家可以重新排队。</summary>
    private void ExpireUnsafe(DateTimeOffset now)
    {
        foreach (var ticket in tickets.Values
                     .Where(ticket => ticket.ExpiresAtUtc <= now
                         && ticket.State is
                             MatchmakingTicketState.Pending
                             or MatchmakingTicketState.Reserved)
                     .ToArray())
        {
            tickets[ticket.TicketId] = ticket with
            {
                State = MatchmakingTicketState.Expired,
                Version = ticket.Version + 1
            };
        }
    }

    /// <summary>在进入临界区后验证有界输入，防止无界队列名或无效 TTL 污染权威存储。</summary>
    private static void Validate(
        string playerId,
        string queueName,
        TimeSpan timeToLive)
    {
        if (string.IsNullOrWhiteSpace(playerId) || playerId.Length > 80)
            throw new ArgumentException("玩家标识无效。", nameof(playerId));
        if (string.IsNullOrWhiteSpace(queueName) || queueName.Length > 80)
            throw new ArgumentException("匹配队列名称无效。", nameof(queueName));
        if (timeToLive < TimeSpan.FromSeconds(10)
            || timeToLive > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(timeToLive));
    }
}
