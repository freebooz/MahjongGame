using GuiyangMahjong.Lobby.Domain;
using Npgsql;

namespace GuiyangMahjong.Lobby.Storage;

/// <summary>
/// 提供玩家房间和连接历史的只读访问；生产实现只读取数据库投影，
/// 写入由房间快照及不可变事件触发器完成，避免管理应用修改历史。
/// </summary>
public interface IPlayerHistoryStore
{
    Task<PlayerHistoryPage<PlayerRoomHistoryRecord>> ListRoomsAsync(
        string playerId,
        int pageSize,
        DateTimeOffset? beforeAtUtc,
        string? beforeRoomId,
        CancellationToken cancellationToken);

    Task<PlayerHistoryPage<PlayerConnectionHistoryRecord>> ListConnectionsAsync(
        string playerId,
        int pageSize,
        DateTimeOffset? beforeAtUtc,
        string? beforeEventId,
        CancellationToken cancellationToken);
}

/// <summary>
/// 本地开发模式没有持久数据库，返回确定的空历史，避免把当前房间误报为历史。
/// </summary>
public sealed class InMemoryPlayerHistoryStore : IPlayerHistoryStore
{
    public Task<PlayerHistoryPage<PlayerRoomHistoryRecord>> ListRoomsAsync(
        string playerId,
        int pageSize,
        DateTimeOffset? beforeAtUtc,
        string? beforeRoomId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlayerHistoryPage<PlayerRoomHistoryRecord>(
            [], null, null));
    }

    public Task<PlayerHistoryPage<PlayerConnectionHistoryRecord>> ListConnectionsAsync(
        string playerId,
        int pageSize,
        DateTimeOffset? beforeAtUtc,
        string? beforeEventId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PlayerHistoryPage<PlayerConnectionHistoryRecord>(
            [], null, null));
    }
}

/// <summary>
/// PostgreSQL 玩家历史只读存储；使用键集分页保证并发追加期间不会跳项或重复。
/// </summary>
public sealed class PostgresPlayerHistoryStore(
    LobbyPersistenceConnections connections) : IPlayerHistoryStore
{
    private readonly NpgsqlDataSource postgres = connections.Postgres;

    public async Task<PlayerHistoryPage<PlayerRoomHistoryRecord>> ListRoomsAsync(
        string playerId,
        int pageSize,
        DateTimeOffset? beforeAtUtc,
        string? beforeRoomId,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize, 1, 200);
        await using var command = postgres.CreateCommand(
            """
            SELECT player_id, room_id, match_id, joined_at_utc,
                   left_at_utc, leave_reason
            FROM player_room_history
            WHERE player_id=$1
              AND ($2::timestamptz IS NULL
                   OR (joined_at_utc, room_id) < ($2, $3))
            ORDER BY joined_at_utc DESC, room_id DESC
            LIMIT $4
            """);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(
            beforeAtUtc.HasValue ? beforeAtUtc.Value : DBNull.Value);
        command.Parameters.AddWithValue(beforeRoomId ?? string.Empty);
        command.Parameters.AddWithValue(take + 1);
        var items = new List<PlayerRoomHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PlayerRoomHistoryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return ToRoomPage(items, take);
    }

    public async Task<PlayerHistoryPage<PlayerConnectionHistoryRecord>>
        ListConnectionsAsync(
            string playerId,
            int pageSize,
            DateTimeOffset? beforeAtUtc,
            string? beforeEventId,
            CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize, 1, 200);
        var parsedEventId = Guid.TryParse(beforeEventId, out var eventId)
            ? eventId
            : Guid.Empty;
        await using var command = postgres.CreateCommand(
            """
            SELECT event_id, player_id, room_id, match_id, from_state,
                   to_state, trustee, occurred_at_utc, trace_id
            FROM player_connection_history
            WHERE player_id=$1
              AND ($2::timestamptz IS NULL
                   OR (occurred_at_utc, event_id) < ($2, $3))
            ORDER BY occurred_at_utc DESC, event_id DESC
            LIMIT $4
            """);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(
            beforeAtUtc.HasValue ? beforeAtUtc.Value : DBNull.Value);
        command.Parameters.AddWithValue(parsedEventId);
        command.Parameters.AddWithValue(take + 1);
        var items = new List<PlayerConnectionHistoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PlayerConnectionHistoryRecord(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetString(8)));
        }
        var hasMore = items.Count > take;
        var visible = items.Take(take).ToArray();
        var boundary = hasMore ? visible[^1] : null;
        return new PlayerHistoryPage<PlayerConnectionHistoryRecord>(
            visible,
            boundary?.OccurredAtUtc,
            boundary?.EventId);
    }

    private static PlayerHistoryPage<PlayerRoomHistoryRecord> ToRoomPage(
        IReadOnlyCollection<PlayerRoomHistoryRecord> items,
        int take)
    {
        var hasMore = items.Count > take;
        var visible = items.Take(take).ToArray();
        var boundary = hasMore ? visible[^1] : null;
        return new PlayerHistoryPage<PlayerRoomHistoryRecord>(
            visible,
            boundary?.JoinedAtUtc,
            boundary?.RoomId);
    }
}
