using System.Text.Json;
using System.Text.Json.Serialization;
using GuiyangMahjong.Lobby.Domain;
using GuiyangMahjong.Lobby.Options;
using GuiyangMahjong.Lobby.Rooms;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace GuiyangMahjong.Lobby.Storage;

/// <summary>
/// Redis 保存热房间快照，PostgreSQL 通过唯一约束提供房间号原子性和重启恢复。
/// 任何密码字段均已是盐化摘要，原始密码不会传入本类型或日志。
/// 所有业务写入先提交 PostgreSQL，再以 StateSequence 条件刷新缓存，陈旧缓存不能覆盖新状态。
/// </summary>
public sealed class RedisPostgresLobbyStore : ILobbyStore
{
    private const string CacheIfNewerScript =
        "local current=redis.call('get',KEYS[1]); "
        + "if current then local ok,value=pcall(cjson.decode,current); "
        + "if ok and tonumber(value.stateSequence)>tonumber(ARGV[2]) then return 0 end end; "
        + "redis.call('set',KEYS[1],ARGV[1],'PX',ARGV[3]); return 1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LobbyPersistenceOptions options;
    private readonly IConnectionMultiplexer redis;
    private readonly NpgsqlDataSource postgres;
    private readonly ILogger<RedisPostgresLobbyStore> logger;

    /// <summary>取得进程共享 Redis/PostgreSQL 客户端和已验证配置；连接生命周期由容器拥有。</summary>
    public RedisPostgresLobbyStore(
        IOptions<LobbyOptions> options,
        LobbyPersistenceConnections connections,
        ILogger<RedisPostgresLobbyStore> logger)
    {
        this.options = options.Value.Persistence;
        redis = connections.Redis;
        postgres = connections.Postgres;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var schemaPath = LobbyStoragePaths.SchemaPath;
        var sql = await File.ReadAllTextAsync(schemaPath, cancellationToken);
        await using var command = postgres.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("PostgreSQL 大厅表结构已验证，Redis 前缀={RedisKeyPrefix}", options.RedisKeyPrefix);
    }

    /// <inheritdoc/>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = postgres.CreateCommand("SELECT 1");
            _ = await command.ExecuteScalarAsync(cancellationToken);
            _ = await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or RedisException or TimeoutException)
        {
            logger.LogWarning(exception, "Lobby persistence readiness check failed");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<CreateRoomResult> TryCreateRoomAsync(LobbyRoom room, CancellationToken cancellationToken)
    {
        room = NormalizeSeats(room);
        var payload = JsonSerializer.Serialize(room, JsonOptions);
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO lobby_rooms(
                room_id, room_code, lifecycle, state_sequence,
                state_version, room_epoch, payload, created_at_utc, updated_at_utc)
            VALUES ($1, $2, $3, $4, $4, $5, $6::jsonb, $7, $8)
            ON CONFLICT DO NOTHING
            RETURNING room_id
            """, connection, transaction);
        command.Parameters.AddWithValue(room.RoomId);
        command.Parameters.AddWithValue(room.RoomCode);
        command.Parameters.AddWithValue(RoomStateMachine.ToCanonicalName(room.Lifecycle));
        command.Parameters.AddWithValue(room.StateSequence);
        command.Parameters.AddWithValue(room.RoomEpoch);
        command.Parameters.AddWithValue(payload);
        command.Parameters.AddWithValue(room.CreatedAtUtc);
        command.Parameters.AddWithValue(room.UpdatedAtUtc);
        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        if (inserted is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateRoomResult(CreateRoomStatus.RoomCodeConflict);
        }

        if (IsActive(room.Lifecycle))
        {
            var conflictingRoomId = await TryReserveActivePlayersAsync(
                connection, transaction, room, cancellationToken);
            if (conflictingRoomId is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new CreateRoomResult(
                    CreateRoomStatus.PlayerAlreadyActive,
                    await GetRoomByIdAsync(conflictingRoomId, cancellationToken));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        await CacheRoomAsync(room);
        return new CreateRoomResult(CreateRoomStatus.Created);
    }

    /// <inheritdoc/>
    public Task<LobbyRoom?> GetRoomByCodeAsync(string roomCode, CancellationToken cancellationToken) =>
        GetRoomAsync("room_code", roomCode, cancellationToken);

    /// <inheritdoc/>
    public Task<LobbyRoom?> GetRoomByIdAsync(string roomId, CancellationToken cancellationToken) =>
        GetRoomAsync("room_id", roomId, cancellationToken);

    /// <inheritdoc/>
    public async Task<LobbyRoom?> GetActiveRoomByPlayerAsync(
        string playerId, CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            SELECT room.payload::text
            FROM active_player_rooms AS active
            JOIN lobby_rooms AS room ON room.room_id = active.room_id
            WHERE active.player_id = $1
            """);
        command.Parameters.AddWithValue(playerId);
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (payload is null) return null;
        var room = JsonSerializer.Deserialize<LobbyRoom>(payload, JsonOptions);
        if (room is not null) await CacheRoomAsync(room);
        return room;
    }

    /// <summary>
    /// 通过 active_player_rooms 索引一次批量关联房间，避免监控页对每名玩家执行一次数据库往返。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, LobbyRoom>>
        GetActiveRoomsByPlayersAsync(
            IReadOnlyCollection<string> playerIds,
            CancellationToken cancellationToken)
    {
        if (playerIds.Count == 0)
            return new Dictionary<string, LobbyRoom>(StringComparer.Ordinal);
        var normalizedIds = playerIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await using var command = postgres.CreateCommand(
            """
            SELECT active.player_id, room.payload::text
            FROM active_player_rooms AS active
            JOIN lobby_rooms AS room ON room.room_id = active.room_id
            WHERE active.player_id = ANY($1)
            """);
        command.Parameters.AddWithValue(normalizedIds);
        var result = new Dictionary<string, LobbyRoom>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var room = JsonSerializer.Deserialize<LobbyRoom>(
                reader.GetString(1),
                JsonOptions);
            if (room is not null)
                result[reader.GetString(0)] = room;
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LobbyRoom>> ListPublicRoomsAsync(CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            SELECT payload::text FROM lobby_rooms
            WHERE lifecycle IN ('Created', 'Creating', 'Waiting', 'Ready', 'Allocating', 'Starting')
            ORDER BY updated_at_utc DESC LIMIT 100
            """);
        var result = new List<LobbyRoom>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var room = JsonSerializer.Deserialize<LobbyRoom>(reader.GetString(0), JsonOptions);
            if (room is { PublicRoom: true }) result.Add(room);
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LobbyRoom>> ListRoomsForMonitoringAsync(
        int limit,
        DateTimeOffset? afterCreatedAtUtc,
        string? afterRoomId,
        string? lifecycle,
        string? gameMode,
        string? search,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            SELECT payload::text FROM lobby_rooms
            WHERE ($4 = '' OR lower(lifecycle) = lower($4))
              AND (
                $5 = ''
                OR COALESCE(
                    payload -> 'ruleSnapshot' ->> 'gameMode',
                    payload -> 'ruleSnapshot' ->> 'playMode',
                    payload -> 'ruleSnapshot' ->> 'variant',
                    'Standard') ILIKE $5)
              AND (
                $6 = ''
                OR room_id ILIKE '%' || $6 || '%'
                OR room_code ILIKE '%' || $6 || '%'
                OR payload ->> 'matchId' ILIKE '%' || $6 || '%'
                OR EXISTS (
                    SELECT 1
                    FROM jsonb_array_elements_text(
                        payload -> 'playerIds') AS player_id
                    WHERE player_id ILIKE '%' || $6 || '%'))
              AND (
                $1::timestamptz IS NULL
                OR (created_at_utc, room_id) < ($1, $2))
            ORDER BY created_at_utc DESC, room_id DESC
            LIMIT $3
            """);
        command.Parameters.Add(new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = afterCreatedAtUtc.HasValue
                ? afterCreatedAtUtc.Value
                : DBNull.Value
        });
        command.Parameters.AddWithValue(afterRoomId ?? string.Empty);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(lifecycle?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue(gameMode?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue(search?.Trim() ?? string.Empty);
        var result = new List<LobbyRoom>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var room = JsonSerializer.Deserialize<LobbyRoom>(reader.GetString(0), JsonOptions);
            if (room is not null) result.Add(room);
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<LobbyRoom?> ReconcileWaitingRoomMembersAsync(
        string roomCode,
        string prospectivePlayerId,
        DateTimeOffset staleBeforeUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand(
            "SELECT payload::text FROM lobby_rooms WHERE room_code=$1 FOR UPDATE",
            connection,
            transaction);
        select.Parameters.AddWithValue(roomCode);
        var payload = await select.ExecuteScalarAsync(cancellationToken) as string;
        if (payload is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var room = JsonSerializer.Deserialize<LobbyRoom>(payload, JsonOptions)
            ?? throw new InvalidDataException("Stored lobby room payload could not be parsed.");
        if (room.Lifecycle is not (
            RoomLifecycle.Created
            or RoomLifecycle.Waiting
            or RoomLifecycle.Ready
            or RoomLifecycle.Allocating
            or RoomLifecycle.Starting))
        {
            await transaction.CommitAsync(cancellationToken);
            return room;
        }

        var retained = new List<string>();
        await using (var active = new NpgsqlCommand(
                         """
                         SELECT player_id FROM active_player_rooms
                         WHERE room_id=$1 AND updated_at_utc >= $2
                         """,
                         connection,
                         transaction))
        {
            active.Parameters.AddWithValue(room.RoomId);
            active.Parameters.AddWithValue(staleBeforeUtc);
            await using var reader = await active.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var playerId = reader.GetString(0);
                if (room.PlayerIds.Contains(playerId, StringComparer.Ordinal)) retained.Add(playerId);
            }
        }

        var retainedPlayerIds = room.PlayerIds
            .Where(playerId => retained.Contains(playerId, StringComparer.Ordinal))
            .ToArray();
        if (retainedPlayerIds.Length == room.PlayerIds.Length)
        {
            await transaction.CommitAsync(cancellationToken);
            return room;
        }

        await using (var deleteStale = new NpgsqlCommand(
                         "DELETE FROM active_player_rooms WHERE room_id=$1 AND updated_at_utc < $2",
                         connection,
                         transaction))
        {
            deleteStale.Parameters.AddWithValue(room.RoomId);
            deleteStale.Parameters.AddWithValue(staleBeforeUtc);
            await deleteStale.ExecuteNonQueryAsync(cancellationToken);
        }

        if (retainedPlayerIds.Length == 0)
        {
            var activeRoomId = await GetActiveRoomIdAsync(
                connection, transaction, prospectivePlayerId, cancellationToken);
            if (activeRoomId is not null && activeRoomId != room.RoomId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return room;
            }
            retainedPlayerIds = [prospectivePlayerId];
        }

        var updated = NormalizeSeats(room with
        {
            OwnerPlayerId = retainedPlayerIds.Contains(room.OwnerPlayerId, StringComparer.Ordinal)
                ? room.OwnerPlayerId
                : retainedPlayerIds[0],
            PlayerIds = retainedPlayerIds,
            StateSequence = room.StateSequence + 1,
            UpdatedAtUtc = observedAtUtc
        });
        if (!await ReserveActivePlayerAsync(
                connection, transaction, updated, retainedPlayerIds[0], cancellationToken)
            || !await UpdateInTransactionAsync(
                connection, transaction, updated, room.StateSequence, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return room;
        }

        await transaction.CommitAsync(cancellationToken);
        await CacheRoomAsync(updated);
        return updated;
    }

    /// <inheritdoc/>
    public async Task RefreshConnectedPlayersAsync(
        string roomId,
        IReadOnlyCollection<string> connectedPlayerIds,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (connectedPlayerIds.Count == 0) return;
        await using var command = postgres.CreateCommand(
            """
            UPDATE active_player_rooms SET updated_at_utc=$1
            WHERE room_id=$2 AND player_id = ANY($3)
            """);
        command.Parameters.AddWithValue(observedAtUtc);
        command.Parameters.AddWithValue(roomId);
        command.Parameters.AddWithValue(connectedPlayerIds.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AddPlayerResult> TryAddPlayerAsync(
        string roomCode, string playerId, CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand(
            "SELECT payload::text FROM lobby_rooms WHERE room_code = $1 FOR UPDATE", connection, transaction);
        select.Parameters.AddWithValue(roomCode);
        var payload = await select.ExecuteScalarAsync(cancellationToken) as string;
        if (payload is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AddPlayerResult(AddPlayerStatus.RoomNotFound, null);
        }

        var room = JsonSerializer.Deserialize<LobbyRoom>(payload, JsonOptions)
            ?? throw new InvalidDataException("数据库房间快照无法解析");
        var activeRoomId = await GetActiveRoomIdAsync(connection, transaction, playerId, cancellationToken);
        if (activeRoomId is not null && activeRoomId != room.RoomId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AddPlayerResult(
                AddPlayerStatus.AlreadyInAnotherRoom,
                await GetRoomByIdAsync(activeRoomId, cancellationToken));
        }
        if (room.PlayerIds.Contains(playerId, StringComparer.Ordinal))
        {
            if (activeRoomId is null && IsActive(room.Lifecycle))
            {
                await ReserveActivePlayerAsync(connection, transaction, room, playerId, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new AddPlayerResult(AddPlayerStatus.AlreadyMember, room);
        }
        if (room.Lifecycle is not (
            RoomLifecycle.Created
            or RoomLifecycle.Waiting
            or RoomLifecycle.Ready
            or RoomLifecycle.Allocating
            or RoomLifecycle.Starting))
        {
            await transaction.CommitAsync(cancellationToken);
            return new AddPlayerResult(AddPlayerStatus.RoomClosed, room);
        }
        if (room.NewPlayersProhibited)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AddPlayerResult(AddPlayerStatus.AdmissionProhibited, room);
        }
        if (room.PlayerIds.Length >= room.MaximumPlayers)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AddPlayerResult(AddPlayerStatus.RoomFull, room);
        }

        var updated = room with
        {
            PlayerIds = [.. room.PlayerIds, playerId],
            Seats =
            [
                .. NormalizeSeats(room).Seats,
                new RoomSeat(
                    playerId,
                    Enumerable.Range(0, room.MaximumPlayers)
                        .First(index => NormalizeSeats(room).Seats
                            .All(seat => seat.SeatIndex != index)),
                    DateTimeOffset.UtcNow)
            ],
            StateSequence = room.StateSequence + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        if (!await ReserveActivePlayerAsync(connection, transaction, updated, playerId, cancellationToken))
        {
            var conflictingRoomId = await GetActiveRoomIdAsync(
                connection, transaction, playerId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AddPlayerResult(
                AddPlayerStatus.AlreadyInAnotherRoom,
                conflictingRoomId is null
                    ? null
                    : await GetRoomByIdAsync(conflictingRoomId, cancellationToken));
        }
        if (!await UpdateInTransactionAsync(
                connection, transaction, updated, room.StateSequence, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AddPlayerResult(AddPlayerStatus.RoomClosed, room);
        }
        await transaction.CommitAsync(cancellationToken);
        await CacheRoomAsync(updated);
        return new AddPlayerResult(AddPlayerStatus.Added, updated);
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateRoomAsync(LobbyRoom room, CancellationToken cancellationToken)
    {
        room = NormalizeSeats(room);
        var payload = JsonSerializer.Serialize(room, JsonOptions);
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            UPDATE lobby_rooms
            SET lifecycle=$1, state_sequence=$2, state_version=$2, room_epoch=$3,
                payload=$4::jsonb, updated_at_utc=$5
            WHERE room_id=$6 AND state_version=$7 AND room_epoch <= $3
            """, connection, transaction);
        command.Parameters.AddWithValue(RoomStateMachine.ToCanonicalName(room.Lifecycle));
        command.Parameters.AddWithValue(room.StateSequence);
        command.Parameters.AddWithValue(room.RoomEpoch);
        command.Parameters.AddWithValue(payload);
        command.Parameters.AddWithValue(room.UpdatedAtUtc);
        command.Parameters.AddWithValue(room.RoomId);
        command.Parameters.AddWithValue(room.StateSequence - 1);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (!await SynchronizeActivePlayersAsync(connection, transaction, room, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        await CacheRoomAsync(room);
        return true;
    }

    /// <inheritdoc/>
    public async Task<FinalizeMatchStatus> FinalizeMatchAsync(
        LobbyRoom closedRoom, MatchResultReport report, CancellationToken cancellationToken)
    {
        var resultPayload = JsonSerializer.Serialize(report, JsonOptions);
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO match_results(match_id, result_sequence, room_id, payload, created_at_utc)
            VALUES ($1, $2, $3, $4::jsonb, $5)
            ON CONFLICT (match_id, result_sequence) DO NOTHING
            RETURNING result_sequence
            """, connection, transaction);
        insert.Parameters.AddWithValue(closedRoom.MatchId);
        insert.Parameters.AddWithValue(report.ResultSequence);
        insert.Parameters.AddWithValue(closedRoom.RoomId);
        insert.Parameters.AddWithValue(resultPayload);
        insert.Parameters.AddWithValue(closedRoom.UpdatedAtUtc);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        if (inserted is null)
        {
            await using var existing = new NpgsqlCommand(
                "SELECT payload::text FROM match_results WHERE match_id=$1 AND result_sequence=$2",
                connection, transaction);
            existing.Parameters.AddWithValue(closedRoom.MatchId);
            existing.Parameters.AddWithValue(report.ResultSequence);
            var existingPayload = await existing.ExecuteScalarAsync(cancellationToken) as string;
            await transaction.CommitAsync(cancellationToken);
            if (existingPayload is null) return FinalizeMatchStatus.Conflict;
            var existingReport = JsonSerializer.Deserialize<MatchResultReport>(existingPayload, JsonOptions);
            return existingReport is not null && MatchResultsEqual(existingReport, report)
                ? FinalizeMatchStatus.Duplicate
                : FinalizeMatchStatus.Conflict;
        }

        await using var update = new NpgsqlCommand(
            """
            UPDATE lobby_rooms
            SET lifecycle=$1, state_sequence=$2, state_version=$2, room_epoch=$3,
                payload=$4::jsonb, updated_at_utc=$5
            WHERE room_id=$6 AND state_version=$7 AND room_epoch <= $3
            """, connection, transaction);
        update.Parameters.AddWithValue(RoomStateMachine.ToCanonicalName(closedRoom.Lifecycle));
        update.Parameters.AddWithValue(closedRoom.StateSequence);
        update.Parameters.AddWithValue(closedRoom.RoomEpoch);
        update.Parameters.AddWithValue(JsonSerializer.Serialize(closedRoom, JsonOptions));
        update.Parameters.AddWithValue(closedRoom.UpdatedAtUtc);
        update.Parameters.AddWithValue(closedRoom.RoomId);
        update.Parameters.AddWithValue(closedRoom.StateSequence - 1);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FinalizeMatchStatus.Conflict;
        }
        await DeleteActivePlayersAsync(connection, transaction, closedRoom.RoomId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await CacheRoomAsync(closedRoom);
        return FinalizeMatchStatus.Accepted;
    }

    private async Task<LobbyRoom?> GetRoomAsync(string column, string value, CancellationToken cancellationToken)
    {
        var database = redis.GetDatabase();
        var cacheKey = column == "room_code" ? RoomCodeKey(value) : RoomIdKey(value);
        var cached = await database.StringGetAsync(cacheKey);
        if (cached.HasValue)
        {
            return JsonSerializer.Deserialize<LobbyRoom>((string)cached!, JsonOptions);
        }

        var sql = $"SELECT payload::text FROM lobby_rooms WHERE {column} = $1";
        await using var command = postgres.CreateCommand(sql);
        command.Parameters.AddWithValue(value);
        var payload = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (payload is null) return null;
        var room = JsonSerializer.Deserialize<LobbyRoom>(payload, JsonOptions);
        if (room is not null) await CacheRoomAsync(room);
        return room;
    }

    private async Task CacheRoomAsync(LobbyRoom room)
    {
        var payload = JsonSerializer.Serialize(room, JsonOptions);
        var database = redis.GetDatabase();
        var expiry = TimeSpan.FromHours(24);
        var expiryMilliseconds = (long)expiry.TotalMilliseconds;
        await Task.WhenAll(
            database.ScriptEvaluateAsync(
                CacheIfNewerScript,
                [new RedisKey(RoomCodeKey(room.RoomCode))],
                [new RedisValue(payload), room.StateSequence, expiryMilliseconds]),
            database.ScriptEvaluateAsync(
                CacheIfNewerScript,
                [new RedisKey(RoomIdKey(room.RoomId))],
                [new RedisValue(payload), room.StateSequence, expiryMilliseconds]));
    }

    private static async Task<bool> UpdateInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LobbyRoom room,
        long expectedStateSequence,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            """
            UPDATE lobby_rooms
            SET lifecycle=$1, state_sequence=$2, state_version=$2, room_epoch=$3,
                payload=$4::jsonb, updated_at_utc=$5
            WHERE room_id=$6 AND state_version=$7 AND room_epoch <= $3
            """, connection, transaction);
        update.Parameters.AddWithValue(RoomStateMachine.ToCanonicalName(room.Lifecycle));
        update.Parameters.AddWithValue(room.StateSequence);
        update.Parameters.AddWithValue(room.RoomEpoch);
        update.Parameters.AddWithValue(JsonSerializer.Serialize(NormalizeSeats(room), JsonOptions));
        update.Parameters.AddWithValue(room.UpdatedAtUtc);
        update.Parameters.AddWithValue(room.RoomId);
        update.Parameters.AddWithValue(expectedStateSequence);
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<string?> TryReserveActivePlayersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LobbyRoom room,
        CancellationToken cancellationToken)
    {
        foreach (var playerId in room.PlayerIds.Distinct(StringComparer.Ordinal))
        {
            if (await ReserveActivePlayerAsync(connection, transaction, room, playerId, cancellationToken))
                continue;
            var activeRoomId = await GetActiveRoomIdAsync(
                connection, transaction, playerId, cancellationToken);
            if (activeRoomId != room.RoomId) return activeRoomId;
        }
        return null;
    }

    private static async Task<bool> SynchronizeActivePlayersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LobbyRoom room,
        CancellationToken cancellationToken)
    {
        if (!IsActive(room.Lifecycle))
        {
            await DeleteActivePlayersAsync(connection, transaction, room.RoomId, cancellationToken);
            return true;
        }

        if (await TryReserveActivePlayersAsync(connection, transaction, room, cancellationToken) is not null)
            return false;

        await using var deleteRemoved = new NpgsqlCommand(
            "DELETE FROM active_player_rooms WHERE room_id=$1 AND NOT (player_id = ANY($2))",
            connection,
            transaction);
        deleteRemoved.Parameters.AddWithValue(room.RoomId);
        deleteRemoved.Parameters.AddWithValue(room.PlayerIds);
        await deleteRemoved.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> ReserveActivePlayerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LobbyRoom room,
        string playerId,
        CancellationToken cancellationToken)
    {
        await using var reserve = new NpgsqlCommand(
            """
            INSERT INTO active_player_rooms(player_id, room_id, match_id, updated_at_utc)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (player_id) DO NOTHING
            RETURNING player_id
            """,
            connection,
            transaction);
        reserve.Parameters.AddWithValue(playerId);
        reserve.Parameters.AddWithValue(room.RoomId);
        reserve.Parameters.AddWithValue(room.MatchId);
        reserve.Parameters.AddWithValue(room.UpdatedAtUtc);
        if (await reserve.ExecuteScalarAsync(cancellationToken) is not null) return true;
        return await GetActiveRoomIdAsync(connection, transaction, playerId, cancellationToken)
            == room.RoomId;
    }

    private static async Task<string?> GetActiveRoomIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string playerId,
        CancellationToken cancellationToken)
    {
        await using var lookup = new NpgsqlCommand(
            "SELECT room_id FROM active_player_rooms WHERE player_id=$1",
            connection,
            transaction);
        lookup.Parameters.AddWithValue(playerId);
        return await lookup.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task DeleteActivePlayersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string roomId,
        CancellationToken cancellationToken)
    {
        await using var delete = new NpgsqlCommand(
            "DELETE FROM active_player_rooms WHERE room_id=$1", connection, transaction);
        delete.Parameters.AddWithValue(roomId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsActive(RoomLifecycle lifecycle) => lifecycle is
        RoomLifecycle.Created
        or RoomLifecycle.Waiting
        or RoomLifecycle.Ready
        or RoomLifecycle.Allocating
        or RoomLifecycle.Starting
        or RoomLifecycle.Playing
        or RoomLifecycle.Suspended
        or RoomLifecycle.Recovering
        or RoomLifecycle.Settling
        or RoomLifecycle.Terminating;

    /// <summary>为升级前 JSONB 快照补齐显式座位，避免读取旧数据时产生重复或越界座位。</summary>
    private static LobbyRoom NormalizeSeats(LobbyRoom room)
    {
        var assigned = room.Seats
            .Where(seat => room.PlayerIds.Contains(seat.PlayerId, StringComparer.Ordinal))
            .Where(seat => seat.SeatIndex >= 0 && seat.SeatIndex < room.MaximumPlayers)
            .GroupBy(seat => seat.PlayerId, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(seat => seat.SeatIndex)
            .Select(group => group.First())
            .ToList();
        foreach (var playerId in room.PlayerIds)
        {
            if (assigned.Any(seat => seat.PlayerId == playerId))
            {
                continue;
            }

            var seatIndex = Enumerable.Range(0, room.MaximumPlayers)
                .First(index => assigned.All(seat => seat.SeatIndex != index));
            assigned.Add(new RoomSeat(playerId, seatIndex, room.CreatedAtUtc));
        }

        return room with { Seats = assigned.OrderBy(seat => seat.SeatIndex).ToArray() };
    }

    private string RoomCodeKey(string roomCode) => $"{options.RedisKeyPrefix}:room:code:{roomCode}";
    private string RoomIdKey(string roomId) => $"{options.RedisKeyPrefix}:room:id:{roomId}";

    private static bool MatchResultsEqual(MatchResultReport left, MatchResultReport right) =>
        left.RoomId == right.RoomId
        && left.ServerInstanceId == right.ServerInstanceId
        && left.ResultSequence == right.ResultSequence
        && left.CompletedRounds == right.CompletedRounds
        && left.Players.SequenceEqual(right.Players)
        && string.Equals(left.EventChainDigest, right.EventChainDigest, StringComparison.Ordinal)
        && (left.ShuffleProofs ?? []).SequenceEqual(right.ShuffleProofs ?? []);

}
