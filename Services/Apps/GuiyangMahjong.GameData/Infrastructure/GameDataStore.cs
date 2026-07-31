using System.Collections.Concurrent;
using System.Text.Json;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using GuiyangMahjong.GameData.Domain;
using Npgsql;

namespace GuiyangMahjong.GameData.Infrastructure;

/// <summary>
/// GameData 唯一写入端口。CommitAsync 必须在一个事务中完成结算、战绩、证据、排行榜和 Outbox，
/// 查询方法只返回投影，不允许调用方取得数据库连接或修改历史行。
/// </summary>
public interface IGameDataStore
{
    Task<SettlementWriteResult> CommitAsync(
        FinalResultEnvelope envelope,
        string requestFingerprint,
        SettlementCommitResult firstResult,
        EventEnvelope committedEvent,
        CancellationToken cancellationToken);
    Task<GameRecord?> GetMatchAsync(string matchId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GameRecord>> GetPlayerRecordsAsync(string playerId, int limit, CancellationToken cancellationToken);
    Task<ReplayEvidenceRecord?> GetEvidenceAsync(string evidenceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(int limit, CancellationToken cancellationToken);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>测试和本地开发存储；单锁模拟数据库事务，重复提交返回首次回执且不重复投影。</summary>
public sealed class InMemoryGameDataStore : IGameDataStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Fingerprint, SettlementCommitResult Result)> settlements = [];
    private readonly Dictionary<string, GameRecord> records = [];
    private readonly Dictionary<string, ReplayEvidenceRecord> evidence = [];
    private readonly Dictionary<string, LeaderboardEntry> leaderboard = [];
    private readonly HashSet<string> outboxEvents = [];

    public Task<SettlementWriteResult> CommitAsync(
        FinalResultEnvelope envelope,
        string requestFingerprint,
        SettlementCommitResult firstResult,
        EventEnvelope committedEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(envelope.MatchId, envelope.RoundNo, envelope.SettlementVersion);
        lock (gate)
        {
            if (settlements.TryGetValue(key, out var existing))
            {
                return Task.FromResult(new SettlementWriteResult(
                    existing.Fingerprint == requestFingerprint
                        ? SettlementWriteStatus.Duplicate
                        : SettlementWriteStatus.Conflict,
                    existing.Fingerprint == requestFingerprint
                        ? existing.Result with { Duplicate = true }
                        : null));
            }

            // 先构造全部投影，再一次性发布到内存集合，保持与 PostgreSQL 事务语义一致。
            var record = new GameRecord(
                firstResult.SettlementId,
                envelope.MatchId,
                envelope.RoomId,
                envelope.RoundNo,
                envelope.SettlementVersion,
                envelope.RuleSetVersion,
                firstResult.CommittedAtUtc,
                envelope.PlayerResults);
            var replay = new ReplayEvidenceRecord(
                envelope.EvidenceId,
                envelope.MatchId,
                envelope.RoomEpoch,
                envelope.RoundNo,
                envelope.SettlementVersion,
                envelope.FinalStateHash,
                envelope.ActionLogHash,
                envelope.RandomCommitment,
                firstResult.CommittedAtUtc.AddDays(180),
                envelope.EvidenceManifest);
            settlements.Add(key, (requestFingerprint, firstResult));
            records.Add(key, record);
            evidence.Add(envelope.EvidenceId, replay);
            foreach (var player in envelope.PlayerResults)
            {
                leaderboard[player.PlayerId] = leaderboard.TryGetValue(player.PlayerId, out var current)
                    ? current with
                    {
                        TotalScore = current.TotalScore + player.TotalScore,
                        MatchCount = current.MatchCount + 1,
                        UpdatedAtUtc = firstResult.CommittedAtUtc
                    }
                    : new LeaderboardEntry(
                        player.PlayerId, player.TotalScore, 1, firstResult.CommittedAtUtc);
            }
            outboxEvents.Add(committedEvent.EventId.Value);
            return Task.FromResult(new SettlementWriteResult(SettlementWriteStatus.Inserted, firstResult));
        }
    }

    public Task<GameRecord?> GetMatchAsync(string matchId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(records.Values
                .Where(record => record.MatchId == matchId)
                .OrderByDescending(record => record.RoundNo).FirstOrDefault());
    }

    public Task<IReadOnlyList<GameRecord>> GetPlayerRecordsAsync(
        string playerId, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult<IReadOnlyList<GameRecord>>(records.Values
                .Where(record => record.PlayerResults.Any(player => player.PlayerId == playerId))
                .OrderByDescending(record => record.CommittedAtUtc).Take(limit).ToArray());
    }

    public Task<ReplayEvidenceRecord?> GetEvidenceAsync(string evidenceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return Task.FromResult(evidence.GetValueOrDefault(evidenceId));
    }

    public Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult<IReadOnlyList<LeaderboardEntry>>(leaderboard.Values
                .OrderByDescending(entry => entry.TotalScore).ThenBy(entry => entry.PlayerId, StringComparer.Ordinal)
                .Take(limit).ToArray());
    }

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    private static string Key(string matchId, int roundNo, int version) => $"{matchId}:{roundNo}:{version}";
}

/// <summary>
/// PostgreSQL 权威存储。首次结算和四类投影、证据清单及 SettlementCommitted Outbox 同事务提交；
/// 唯一冲突后只读取首次指纹/回执，不执行透明重试或覆盖更新。
/// </summary>
public sealed class PostgresGameDataStore(NpgsqlDataSource dataSource) : IGameDataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SettlementWriteResult> CommitAsync(
        FinalResultEnvelope envelope,
        string requestFingerprint,
        SettlementCommitResult firstResult,
        EventEnvelope committedEvent,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var insertSettlement = new NpgsqlCommand(
            """
            INSERT INTO settlement.final_results(
                settlement_id,match_id,room_id,round_no,settlement_version,
                server_instance_id,room_epoch,ruleset_version,server_build,
                final_state_hash,action_log_hash,random_commitment,evidence_id,
                request_fingerprint,envelope,generated_at,committed_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15::jsonb,$16,$17)
            ON CONFLICT (match_id,round_no,settlement_version) DO NOTHING
            RETURNING settlement_id
            """, connection, transaction);
        AddSettlementParameters(insertSettlement, envelope, requestFingerprint, firstResult);
        var inserted = await insertSettlement.ExecuteScalarAsync(cancellationToken);
        if (inserted is null)
        {
            await using var existing = new NpgsqlCommand(
                """
                SELECT settlement_id,request_fingerprint,committed_at
                FROM settlement.final_results
                WHERE match_id=$1 AND round_no=$2 AND settlement_version=$3
                """, connection, transaction);
            existing.Parameters.AddWithValue(envelope.MatchId);
            existing.Parameters.AddWithValue(envelope.RoundNo);
            existing.Parameters.AddWithValue(envelope.SettlementVersion);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("结算唯一冲突后首次记录不可见。");
            var same = reader.GetString(1) == requestFingerprint;
            var result = same
                ? new SettlementCommitResult(
                    reader.GetGuid(0).ToString(), envelope.MatchId, envelope.RoundNo,
                    envelope.SettlementVersion, reader.GetFieldValue<DateTimeOffset>(2), true)
                : null;
            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return new SettlementWriteResult(
                same ? SettlementWriteStatus.Duplicate : SettlementWriteStatus.Conflict,
                result);
        }

        await InsertProjectionsAsync(connection, transaction, envelope, firstResult, cancellationToken);
        await InsertOutboxAsync(connection, transaction, committedEvent, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SettlementWriteResult(SettlementWriteStatus.Inserted, firstResult);
    }

    public async Task<GameRecord?> GetMatchAsync(string matchId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT settlement_id,match_id,room_id,round_no,settlement_version,ruleset_version,
                   committed_at,player_results::text
            FROM game_record.matches WHERE match_id=$1 ORDER BY round_no DESC LIMIT 1
            """);
        command.Parameters.AddWithValue(matchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<GameRecord>> GetPlayerRecordsAsync(
        string playerId, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT m.settlement_id,m.match_id,m.room_id,m.round_no,m.settlement_version,
                   m.ruleset_version,m.committed_at,m.player_results::text
            FROM game_record.matches m
            JOIN game_record.participants p
              ON p.match_id=m.match_id AND p.round_no=m.round_no
             AND p.settlement_version=m.settlement_version
            WHERE p.player_id=$1 ORDER BY m.committed_at DESC LIMIT $2
            """);
        command.Parameters.AddWithValue(playerId);
        command.Parameters.AddWithValue(limit);
        var records = new List<GameRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) records.Add(ReadRecord(reader));
        return records;
    }

    public async Task<ReplayEvidenceRecord?> GetEvidenceAsync(string evidenceId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT evidence_id,match_id,room_epoch,round_no,settlement_version,
                   final_state_hash,action_log_hash,random_commitment,retain_until,objects::text
            FROM replay.evidence_manifests WHERE evidence_id=$1
            """);
        command.Parameters.AddWithValue(Guid.Parse(evidenceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReplayEvidenceRecord(
            reader.GetGuid(0).ToString(), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            JsonSerializer.Deserialize<EvidenceManifestItem[]>(reader.GetString(9), JsonOptions) ?? []);
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT player_id,total_score,match_count,updated_at
            FROM leaderboard.player_scores
            ORDER BY total_score DESC,player_id LIMIT $1
            """);
        command.Parameters.AddWithValue(limit);
        var entries = new List<LeaderboardEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new LeaderboardEntry(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        return entries;
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static void AddSettlementParameters(
        NpgsqlCommand command, FinalResultEnvelope envelope, string fingerprint, SettlementCommitResult result)
    {
        command.Parameters.AddWithValue(Guid.Parse(result.SettlementId));
        command.Parameters.AddWithValue(envelope.MatchId);
        command.Parameters.AddWithValue(envelope.RoomId);
        command.Parameters.AddWithValue(envelope.RoundNo);
        command.Parameters.AddWithValue(envelope.SettlementVersion);
        command.Parameters.AddWithValue(envelope.ServerInstanceId);
        command.Parameters.AddWithValue(envelope.RoomEpoch);
        command.Parameters.AddWithValue(envelope.RuleSetVersion);
        command.Parameters.AddWithValue(envelope.ServerBuild);
        command.Parameters.AddWithValue(envelope.FinalStateHash.ToLowerInvariant());
        command.Parameters.AddWithValue(envelope.ActionLogHash.ToLowerInvariant());
        command.Parameters.AddWithValue(envelope.RandomCommitment.ToLowerInvariant());
        command.Parameters.AddWithValue(Guid.Parse(envelope.EvidenceId));
        command.Parameters.AddWithValue(fingerprint);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(envelope, JsonOptions));
        command.Parameters.AddWithValue(envelope.GeneratedAt);
        command.Parameters.AddWithValue(result.CommittedAtUtc);
    }

    private static async Task InsertProjectionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FinalResultEnvelope envelope,
        SettlementCommitResult result,
        CancellationToken cancellationToken)
    {
        await using var record = new NpgsqlCommand(
            """
            INSERT INTO game_record.matches(
                settlement_id,match_id,room_id,round_no,settlement_version,ruleset_version,
                committed_at,player_results)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8::jsonb)
            """, connection, transaction);
        record.Parameters.AddWithValue(Guid.Parse(result.SettlementId));
        record.Parameters.AddWithValue(envelope.MatchId);
        record.Parameters.AddWithValue(envelope.RoomId);
        record.Parameters.AddWithValue(envelope.RoundNo);
        record.Parameters.AddWithValue(envelope.SettlementVersion);
        record.Parameters.AddWithValue(envelope.RuleSetVersion);
        record.Parameters.AddWithValue(result.CommittedAtUtc);
        record.Parameters.AddWithValue(JsonSerializer.Serialize(envelope.PlayerResults, JsonOptions));
        await record.ExecuteNonQueryAsync(cancellationToken);

        foreach (var player in envelope.PlayerResults)
        {
            await using var participant = new NpgsqlCommand(
                """
                INSERT INTO game_record.participants(
                    match_id,round_no,settlement_version,player_id,seat_id,rank,total_score)
                VALUES ($1,$2,$3,$4,$5,$6,$7)
                """, connection, transaction);
            participant.Parameters.AddWithValue(envelope.MatchId);
            participant.Parameters.AddWithValue(envelope.RoundNo);
            participant.Parameters.AddWithValue(envelope.SettlementVersion);
            participant.Parameters.AddWithValue(player.PlayerId);
            participant.Parameters.AddWithValue(player.SeatId);
            participant.Parameters.AddWithValue(player.Rank);
            participant.Parameters.AddWithValue(player.TotalScore);
            await participant.ExecuteNonQueryAsync(cancellationToken);

            await using var score = new NpgsqlCommand(
                """
                INSERT INTO leaderboard.player_scores(player_id,total_score,match_count,updated_at)
                VALUES ($1,$2,1,$3)
                ON CONFLICT (player_id) DO UPDATE
                SET total_score=leaderboard.player_scores.total_score + EXCLUDED.total_score,
                    match_count=leaderboard.player_scores.match_count + 1,
                    updated_at=EXCLUDED.updated_at
                """, connection, transaction);
            score.Parameters.AddWithValue(player.PlayerId);
            score.Parameters.AddWithValue(player.TotalScore);
            score.Parameters.AddWithValue(result.CommittedAtUtc);
            await score.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var evidence = new NpgsqlCommand(
            """
            INSERT INTO replay.evidence_manifests(
                evidence_id,match_id,room_epoch,round_no,settlement_version,
                final_state_hash,action_log_hash,random_commitment,objects,created_at,retain_until)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb,$10,$11)
            """, connection, transaction);
        evidence.Parameters.AddWithValue(Guid.Parse(envelope.EvidenceId));
        evidence.Parameters.AddWithValue(envelope.MatchId);
        evidence.Parameters.AddWithValue(envelope.RoomEpoch);
        evidence.Parameters.AddWithValue(envelope.RoundNo);
        evidence.Parameters.AddWithValue(envelope.SettlementVersion);
        evidence.Parameters.AddWithValue(envelope.FinalStateHash.ToLowerInvariant());
        evidence.Parameters.AddWithValue(envelope.ActionLogHash.ToLowerInvariant());
        evidence.Parameters.AddWithValue(envelope.RandomCommitment.ToLowerInvariant());
        evidence.Parameters.AddWithValue(JsonSerializer.Serialize(envelope.EvidenceManifest, JsonOptions));
        evidence.Parameters.AddWithValue(result.CommittedAtUtc);
        evidence.Parameters.AddWithValue(result.CommittedAtUtc.AddDays(180));
        await evidence.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO game_data_integration.platform_outbox(
                event_id,event_type,schema_version,aggregate_type,aggregate_id,
                aggregate_version,payload_json,occurred_at,created_at,status,
                attempt_count,next_attempt_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7::jsonb,$8,$8,'Pending',0,$8)
            """, connection, transaction);
        command.Parameters.AddWithValue(envelope.EventId.Value);
        command.Parameters.AddWithValue(envelope.EventType);
        command.Parameters.AddWithValue(envelope.SchemaVersion);
        command.Parameters.AddWithValue(envelope.AggregateType);
        command.Parameters.AddWithValue(envelope.AggregateId);
        command.Parameters.AddWithValue(envelope.AggregateVersion);
        command.Parameters.AddWithValue(envelope.Payload.GetRawText());
        command.Parameters.AddWithValue(envelope.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static GameRecord ReadRecord(NpgsqlDataReader reader) => new(
        reader.GetGuid(0).ToString(), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
        reader.GetInt32(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
        JsonSerializer.Deserialize<FinalPlayerResult[]>(reader.GetString(7), JsonOptions) ?? []);
}
