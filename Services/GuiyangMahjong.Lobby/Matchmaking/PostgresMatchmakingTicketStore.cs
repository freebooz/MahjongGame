using GuiyangMahjong.Lobby.Storage;
using Npgsql;

namespace GuiyangMahjong.Lobby.Matchmaking;

/// <summary>
/// PostgreSQL 匹配票据权威实现。
/// 候选领取使用行锁和事务，Redis 即使丢失也不会导致同一票据被两个分组消费。
/// </summary>
public sealed class PostgresMatchmakingTicketStore(
    LobbyPersistenceConnections connections,
    TimeProvider timeProvider) : IMatchmakingTicketStore
{
    private readonly NpgsqlDataSource postgres = connections.Postgres;

    /// <inheritdoc/>
    public async Task<MatchmakingTicket> CreateAsync(
        string playerId,
        string queueName,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        Validate(playerId, queueName, timeToLive);
        var now = timeProvider.GetUtcNow();
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExpireAsync(connection, transaction, now, cancellationToken);
        var ticketId = Guid.NewGuid();
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO matchmaking.matchmaking_tickets(
                ticket_id, player_id, queue_name, state, version,
                created_at_utc, expires_at_utc)
            VALUES ($1, $2, $3, 'Pending', 1, $4, $5)
            ON CONFLICT DO NOTHING
            RETURNING ticket_id
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue(ticketId);
        insert.Parameters.AddWithValue(playerId);
        insert.Parameters.AddWithValue(queueName);
        insert.Parameters.AddWithValue(now);
        insert.Parameters.AddWithValue(now.Add(timeToLive));
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        var result = inserted is not null
            ? new MatchmakingTicket(
                ticketId,
                playerId,
                queueName,
                MatchmakingTicketState.Pending,
                1,
                null,
                now,
                now.Add(timeToLive),
                null,
                null)
            : await FindActiveAsync(
                connection,
                transaction,
                playerId,
                queueName,
                cancellationToken)
              ?? throw new InvalidOperationException("活动匹配票据唯一约束冲突但未找到权威记录。");
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MatchmakingTicket>> ReserveAsync(
        string queueName,
        int count,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queueName) || queueName.Length > 80)
            throw new ArgumentException("匹配队列名称无效。", nameof(queueName));
        if (count is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (reservationId == Guid.Empty)
            throw new ArgumentException("ReservationId 不能为空。", nameof(reservationId));

        var now = timeProvider.GetUtcNow();
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExpireAsync(connection, transaction, now, cancellationToken);
        var candidates = new List<MatchmakingTicket>();
        await using (var select = new NpgsqlCommand(
                         """
                         SELECT ticket_id, player_id, queue_name, state, version,
                                reservation_id, created_at_utc, expires_at_utc,
                                reserved_at_utc, consumed_at_utc
                         FROM matchmaking.matchmaking_tickets
                         WHERE queue_name=$1 AND state='Pending' AND expires_at_utc>$2
                         ORDER BY created_at_utc, ticket_id
                         FOR UPDATE SKIP LOCKED
                         LIMIT $3
                         """,
                         connection,
                         transaction))
        {
            select.Parameters.AddWithValue(queueName);
            select.Parameters.AddWithValue(now);
            select.Parameters.AddWithValue(count);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(Read(reader));
            }
        }
        if (candidates.Count != count)
        {
            await transaction.RollbackAsync(cancellationToken);
            return [];
        }

        await using var update = new NpgsqlCommand(
            """
            UPDATE matchmaking.matchmaking_tickets
            SET state='Reserved', version=version+1,
                reservation_id=$1, reserved_at_utc=$2
            WHERE ticket_id = ANY($3) AND state='Pending'
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue(reservationId);
        update.Parameters.AddWithValue(now);
        update.Parameters.AddWithValue(candidates.Select(ticket => ticket.TicketId).ToArray());
        if (await update.ExecuteNonQueryAsync(cancellationToken) != count)
        {
            await transaction.RollbackAsync(cancellationToken);
            return [];
        }
        await transaction.CommitAsync(cancellationToken);
        return candidates.Select(ticket => ticket with
        {
            State = MatchmakingTicketState.Reserved,
            Version = ticket.Version + 1,
            ReservationId = reservationId,
            ReservedAtUtc = now
        }).ToArray();
    }

    /// <inheritdoc/>
    public async Task<MatchmakingConsumeResult> ConsumeAsync(
        Guid ticketId,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        MatchmakingTicket? ticket;
        await using (var select = new NpgsqlCommand(
                         """
                         SELECT ticket_id, player_id, queue_name, state, version,
                                reservation_id, created_at_utc, expires_at_utc,
                                reserved_at_utc, consumed_at_utc
                         FROM matchmaking.matchmaking_tickets
                         WHERE ticket_id=$1
                         FOR UPDATE
                         """,
                         connection,
                         transaction))
        {
            select.Parameters.AddWithValue(ticketId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            ticket = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        }
        if (ticket is null || ticket.ReservationId != reservationId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MatchmakingConsumeResult(false, false, ticket);
        }
        if (ticket.State == MatchmakingTicketState.Consumed)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MatchmakingConsumeResult(true, true, ticket);
        }
        if (ticket.State != MatchmakingTicketState.Reserved)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MatchmakingConsumeResult(false, false, ticket);
        }

        var now = timeProvider.GetUtcNow();
        await using var update = new NpgsqlCommand(
            """
            UPDATE matchmaking.matchmaking_tickets
            SET state='Consumed', version=version+1, consumed_at_utc=$1
            WHERE ticket_id=$2 AND state='Reserved' AND reservation_id=$3
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue(now);
        update.Parameters.AddWithValue(ticketId);
        update.Parameters.AddWithValue(reservationId);
        var accepted = await update.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        var consumed = accepted
            ? ticket with
            {
                State = MatchmakingTicketState.Consumed,
                Version = ticket.Version + 1,
                ConsumedAtUtc = now
            }
            : ticket;
        return new MatchmakingConsumeResult(accepted, false, consumed);
    }

    /// <summary>在当前事务内把所有过期活动票据转为终态，释放玩家+队列唯一约束。</summary>
    private static async Task ExpireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE matchmaking.matchmaking_tickets
            SET state='Expired', version=version+1
            WHERE state IN ('Pending', 'Reserved') AND expires_at_utc <= $1
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>查询玩家在队列中的唯一活动票据；仅在创建冲突的同一事务内使用。</summary>
    private static async Task<MatchmakingTicket?> FindActiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string playerId,
        string queueName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT ticket_id, player_id, queue_name, state, version,
                   reservation_id, created_at_utc, expires_at_utc,
                   reserved_at_utc, consumed_at_utc
            FROM matchmaking.matchmaking_tickets
            WHERE player_id=$1 AND queue_name=$2
              AND state IN ('Pending', 'Reserved')
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(queueName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    /// <summary>从固定列序读取匹配票据，数据库状态值不在枚举中时立即失败关闭。</summary>
    private static MatchmakingTicket Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        Enum.Parse<MatchmakingTicketState>(reader.GetString(3), false),
        reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetGuid(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));

    /// <summary>校验票据边界，避免无效标识或无限 TTL 进入 PostgreSQL。</summary>
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
