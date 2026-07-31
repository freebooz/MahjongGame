using Npgsql;

namespace GuiyangMahjong.GameData.Tests;

/// <summary>使用真实PostgreSQL验证阶段8.2迁移幂等、旧写关闭和可控回滚。</summary>
public sealed class LegacyReplayMigrationPostgresTests
{
    [Fact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Migration_IsRepeatable_ClosesOldWriter_AndRollbackRestoresCompatibility()
    {
        var connectionString = Environment.GetEnvironmentVariable("GAMEDATA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var root = FindRoot();
        await ExecuteFileAsync(dataSource, Path.Combine(root, "Services", "Apps", "GuiyangMahjong.GameData", "Storage", "schema.sql"));
        await ExecuteFileAsync(dataSource, Path.Combine(root, "Services", "GuiyangMahjong.PlayerData", "Storage", "schema.sql"));

        // 模拟8.1运行中的历史库：旧Replay行存在，而8.2数据库门禁尚未启用。
        await ExecuteAsync(dataSource, "DROP TRIGGER IF EXISTS trg_reject_replay_evidence_write ON player_data.evidence_events");
        var eventId = Guid.NewGuid();
        await ExecuteAsync(dataSource,
            """
            INSERT INTO player_data.evidence_events(
                event_id,player_id,evidence_type,occurred_at_utc,source_reference,data,sensitivity,recorded_at_utc)
            VALUES ($1,'player-migration','Replay',now(),$2,'{"replayId":"legacy"}'::jsonb,'Restricted',now())
            """, eventId, $"replay:{eventId:N}");

        var migration = Path.Combine(root, "Docs", "architecture", "sql", "stage-8.2-replay-evidence-migration.sql");
        await ExecuteFileAsync(dataSource, migration);
        await ExecuteFileAsync(dataSource, migration);
        Assert.Equal(1L, await ScalarAsync(dataSource,
            "SELECT count(*) FROM replay.legacy_player_evidence WHERE event_id=$1", eventId));

        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(dataSource,
            """
            INSERT INTO player_data.evidence_events(
                event_id,player_id,evidence_type,occurred_at_utc,source_reference,data,sensitivity,recorded_at_utc)
            VALUES (gen_random_uuid(),'player-migration','Replay',now(),$1,'{}'::jsonb,'Restricted',now())
            """, $"blocked:{Guid.NewGuid():N}"));

        await ExecuteFileAsync(dataSource, Path.Combine(root, "Docs", "architecture", "sql", "stage-8.2-replay-evidence-rollback.sql"));
        await ExecuteAsync(dataSource,
            """
            INSERT INTO player_data.evidence_events(
                event_id,player_id,evidence_type,occurred_at_utc,source_reference,data,sensitivity,recorded_at_utc)
            VALUES (gen_random_uuid(),'player-rollback','Replay',now(),$1,'{}'::jsonb,'Restricted',now())
            """, $"rollback:{Guid.NewGuid():N}");
    }

    private static async Task ExecuteFileAsync(NpgsqlDataSource source, string path) =>
        await ExecuteAsync(source, await File.ReadAllTextAsync(path));

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql, object value)
    {
        await using var command = source.CreateCommand(sql);
        command.Parameters.AddWithValue(value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "Services"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
