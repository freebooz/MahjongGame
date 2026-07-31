using System.Text;
using GuiyangMahjong.BuildingBlocks.Idempotency;
using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.BuildingBlocks.Persistence;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using Npgsql;
using Xunit;

namespace GuiyangMahjong.BuildingBlocks.Tests;

/// <summary>
/// 仅在显式提供隔离 PostgreSQL 时执行的测试标记。
/// 默认测试绝不连接 localhost、开发共享库或生产数据库。
/// </summary>
public sealed class BuildingBlocksPostgresFactAttribute : FactAttribute
{
    public BuildingBlocksPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "BUILDING_BLOCKS_TEST_POSTGRES")))
        {
            Skip =
                "Set BUILDING_BLOCKS_TEST_POSTGRES to run isolated PostgreSQL transaction tests.";
        }
    }
}

/// <summary>
/// 验证 PostgreSQL Outbox、Inbox 和幂等实现的事务、唯一约束与并发领取语义。
/// 每个测试使用随机 bb_test_ Schema，并在 finally 中精确删除该测试 Schema。
/// </summary>
public sealed class PostgresBuildingBlocksTests
{
    /// <summary>迁移升级必须创建全部表，逆向迁移必须只删除基础表并保留服务 Schema。</summary>
    [BuildingBlocksPostgresFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task SchemaMigration_UpgradeAndRollbackAreExecutable()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        Assert.Equal(0, await fixture.CountAsync("platform_outbox"));

        await PlatformPersistenceSchema.RollbackAsync(
            fixture.DataSource,
            fixture.Names,
            CancellationToken.None);

        await using var command = fixture.DataSource.CreateCommand(
            """
            SELECT to_regclass($1) IS NULL
            """);
        command.Parameters.AddWithValue(
            $"{fixture.Names.Schema}.platform_outbox");
        Assert.True((bool)(await command.ExecuteScalarAsync()
                          ?? throw new InvalidOperationException(
                              "迁移回滚校验未返回结果。")));
    }

    /// <summary>业务事务回滚后业务行与 Outbox 必须同时不存在；提交后必须同时存在。</summary>
    [BuildingBlocksPostgresFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Outbox_IsAtomicWithBusinessTransactionAndRollback()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var outbox = new PostgresOutboxStore(
            fixture.DataSource,
            fixture.Names);
        var message = CreateOutbox("atomic-001");

        await using (var connection =
                     await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction =
                     await connection.BeginTransactionAsync())
        {
            await InsertBusinessProbeAsync(
                connection,
                transaction,
                fixture.Names.Schema,
                "rolled-back");
            await outbox.AddAsync(
                connection,
                transaction,
                message,
                CancellationToken.None);
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await fixture.CountAsync("business_probe"));
        Assert.Equal(0, await fixture.CountAsync("platform_outbox"));

        await using (var connection =
                     await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction =
                     await connection.BeginTransactionAsync())
        {
            await InsertBusinessProbeAsync(
                connection,
                transaction,
                fixture.Names.Schema,
                "committed");
            await outbox.AddAsync(
                connection,
                transaction,
                message,
                CancellationToken.None);
            await transaction.CommitAsync();
        }
        Assert.Equal(1, await fixture.CountAsync("business_probe"));
        Assert.Equal(1, await fixture.CountAsync("platform_outbox"));
    }

    /// <summary>两个 Worker 并发领取时不能获得同一 event_id，租约内第三个 Worker 不得重复领取。</summary>
    [BuildingBlocksPostgresFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Outbox_ConcurrentClaimsDoNotOverlap()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var outbox = new PostgresOutboxStore(
            fixture.DataSource,
            fixture.Names);
        await fixture.InsertOutboxAsync(
            outbox,
            CreateOutbox("claim-001"));
        await fixture.InsertOutboxAsync(
            outbox,
            CreateOutbox("claim-002"));
        var now = DateTimeOffset.UtcNow;

        var results = await Task.WhenAll(
            outbox.ClaimAsync(
                "worker-a",
                1,
                now,
                TimeSpan.FromMinutes(1),
                CancellationToken.None),
            outbox.ClaimAsync(
                "worker-b",
                1,
                now,
                TimeSpan.FromMinutes(1),
                CancellationToken.None));
        var third = await outbox.ClaimAsync(
            "worker-c",
            10,
            now,
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.Single(results[0]);
        Assert.Single(results[1]);
        Assert.NotEqual(
            results[0][0].EventId,
            results[1][0].EventId);
        Assert.Empty(third);
    }

    /// <summary>消费者业务写入和 Inbox 完成同事务提交，重复事件随后快速确认且不重复写业务行。</summary>
    [BuildingBlocksPostgresFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Inbox_DuplicateEventIsAcknowledgedWithoutBusinessReplay()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var inbox = new PostgresInboxStore(
            fixture.DataSource,
            fixture.Names);
        var envelope = CreateEnvelope("inbox-001");

        // 先证明业务行和 Inbox 开始记录在同一事务回滚后都会消失。
        await using (var rollbackConnection =
                     await fixture.DataSource.OpenConnectionAsync())
        await using (var rollbackTransaction =
                     await rollbackConnection.BeginTransactionAsync())
        {
            var begin = await inbox.TryBeginAsync(
                rollbackConnection,
                rollbackTransaction,
                envelope,
                "history-projector",
                1,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            Assert.Equal(InboxBeginResult.Started, begin);
            await InsertBusinessProbeAsync(
                rollbackConnection,
                rollbackTransaction,
                fixture.Names.Schema,
                "rolled-back-inbox-business");
            await rollbackTransaction.RollbackAsync();
        }
        Assert.Equal(0, await fixture.CountAsync("business_probe"));
        Assert.Equal(0, await fixture.CountAsync("platform_inbox"));

        await using (var connection =
                     await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction =
                     await connection.BeginTransactionAsync())
        {
            var begin = await inbox.TryBeginAsync(
                connection,
                transaction,
                envelope,
                "history-projector",
                1,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            Assert.Equal(InboxBeginResult.Started, begin);
            await InsertBusinessProbeAsync(
                connection,
                transaction,
                fixture.Names.Schema,
                "inbox-business");
            await inbox.CompleteAsync(
                connection,
                transaction,
                envelope.EventId,
                "history-projector",
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using var duplicateConnection =
            await fixture.DataSource.OpenConnectionAsync();
        await using var duplicateTransaction =
            await duplicateConnection.BeginTransactionAsync();
        var duplicate = await inbox.TryBeginAsync(
            duplicateConnection,
            duplicateTransaction,
            envelope,
            "history-projector",
            1,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await duplicateTransaction.RollbackAsync();

        Assert.Equal(
            InboxBeginResult.DuplicateCompleted,
            duplicate);
        Assert.Equal(1, await fixture.CountAsync("business_probe"));
        Assert.Equal(1, await fixture.CountAsync("platform_inbox"));
    }

    /// <summary>数据库唯一约束必须区分相同参数重放和同 Key 不同参数冲突。</summary>
    [BuildingBlocksPostgresFact]
    [Trait("Category", "ExternalPersistence")]
    public async Task Idempotency_UniqueConstraintDetectsReplayAndConflict()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        var store = new PostgresIdempotencyStore(
            fixture.DataSource,
            fixture.Names);
        var key = IdempotencyKey.Parse("postgres-idempotency-001");
        var now = DateTimeOffset.UtcNow;
        var fingerprint = Fingerprint("""{"value":1}""");

        var acquired = await store.TryBeginAsync(
            "test.command",
            key,
            fingerprint,
            now,
            now.AddHours(1),
            CancellationToken.None);
        await store.CompleteAsync(
            "test.command",
            key,
            fingerprint,
            new IdempotentResponse(
                200,
                "application/json",
                Encoding.UTF8.GetBytes("""{"ok":true}""")),
            CancellationToken.None);
        var replay = await store.TryBeginAsync(
            "test.command",
            key,
            fingerprint,
            now.AddSeconds(1),
            now.AddHours(1),
            CancellationToken.None);
        var conflict = await store.TryBeginAsync(
            "test.command",
            key,
            Fingerprint("""{"value":2}"""),
            now.AddSeconds(1),
            now.AddHours(1),
            CancellationToken.None);

        Assert.Equal(IdempotencyDecision.Acquired, acquired.Decision);
        Assert.Equal(IdempotencyDecision.Replay, replay.Decision);
        Assert.NotNull(replay.Response);
        Assert.Equal(IdempotencyDecision.Conflict, conflict.Decision);
    }

    private static OutboxMessage CreateOutbox(string suffix) =>
        OutboxMessage.FromEnvelope(
            CreateEnvelope(suffix),
            DateTimeOffset.UtcNow);

    private static EventEnvelope CreateEnvelope(string suffix)
    {
        var now = DateTimeOffset.UtcNow;
        return EventEnvelope.Create(
            new RoomCreated(
                RoomId.Parse($"room-{suffix}"),
                RoomEpoch.Parse(1),
                PlayerId.Parse($"player-{suffix}"),
                RuleSetVersion.Parse("1.0.0"),
                now),
            "Room",
            $"room-{suffix}",
            1,
            "BuildingBlocksTests",
            "0123456789abcdef0123456789abcdef",
            CorrelationId.Parse($"correlation-{suffix}"),
            now);
    }

    private static async Task InsertBusinessProbeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string value)
    {
        if (!schema.StartsWith("bb_test_", StringComparison.Ordinal))
            throw new ArgumentException("测试 Schema 格式无效。", nameof(schema));
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO "{schema}".business_probe(value) VALUES ($1)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }

    private static string Fingerprint(string body) =>
        new Sha256RequestFingerprint().Compute(
            "POST",
            "/test/command",
            Encoding.UTF8.GetBytes(body));

    /// <summary>隔离 PostgreSQL Schema 生命周期和安全清理边界。</summary>
    private sealed class PostgresFixture : IAsyncDisposable
    {
        private PostgresFixture(
            NpgsqlDataSource dataSource,
            PersistenceTableNames names)
        {
            DataSource = dataSource;
            Names = names;
        }

        public NpgsqlDataSource DataSource { get; }
        public PersistenceTableNames Names { get; }

        public static async Task<PostgresFixture> CreateAsync()
        {
            var connectionString =
                Environment.GetEnvironmentVariable(
                    "BUILDING_BLOCKS_TEST_POSTGRES")
                ?? throw new InvalidOperationException(
                    "BUILDING_BLOCKS_TEST_POSTGRES is required.");
            var schema = $"bb_test_{Guid.NewGuid():N}";
            var dataSource = NpgsqlDataSource.Create(connectionString);
            var fixture = new PostgresFixture(
                dataSource,
                new PersistenceTableNames(schema));
            await PlatformPersistenceSchema.ApplyAsync(
                dataSource,
                fixture.Names,
                CancellationToken.None);
            await using var command = dataSource.CreateCommand(
                $"""
                CREATE TABLE "{schema}".business_probe(
                    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """);
            await command.ExecuteNonQueryAsync();
            return fixture;
        }

        public async Task InsertOutboxAsync(
            PostgresOutboxStore store,
            OutboxMessage message)
        {
            await using var connection =
                await DataSource.OpenConnectionAsync();
            await using var transaction =
                await connection.BeginTransactionAsync();
            await store.AddAsync(
                connection,
                transaction,
                message,
                CancellationToken.None);
            await transaction.CommitAsync();
        }

        public async Task<long> CountAsync(string table)
        {
            if (!new[]
                {
                    "business_probe",
                    "platform_outbox",
                    "platform_inbox"
                }.Contains(table, StringComparer.Ordinal))
            {
                throw new ArgumentException("测试表名不在白名单。", nameof(table));
            }
            await using var command = DataSource.CreateCommand(
                $"""SELECT COUNT(*) FROM "{Names.Schema}"."{table}" """);
            return (long)(await command.ExecuteScalarAsync()
                          ?? throw new InvalidOperationException(
                              "COUNT 未返回结果。"));
        }

        public async ValueTask DisposeAsync()
        {
            // 只允许删除本测试生成的随机 Schema，防止环境变量指向错误数据库时扩大清理范围。
            if (!Names.Schema.StartsWith(
                    "bb_test_",
                    StringComparison.Ordinal)
                || Names.Schema.Length != 40)
            {
                throw new InvalidOperationException(
                    "拒绝清理非测试 PostgreSQL Schema。");
            }
            await using (var command = DataSource.CreateCommand(
                             $"""DROP SCHEMA "{Names.Schema}" CASCADE"""))
            {
                await command.ExecuteNonQueryAsync();
            }
            await DataSource.DisposeAsync();
        }
    }
}
