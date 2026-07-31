using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using GuiyangMahjong.Workers.Storage;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Npgsql;
using Xunit;

namespace GuiyangMahjong.Workers.Tests;

/// <summary>只有显式提供隔离 PostgreSQL 时才运行，避免测试误连开发或生产数据库。</summary>
public sealed class WorkersPostgresFactAttribute : FactAttribute
{
    public WorkersPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("WORKERS_TEST_POSTGRES")))
        {
            Skip = "设置 WORKERS_TEST_POSTGRES 后运行 Workers PostgreSQL 集成测试。";
        }
    }
}

/// <summary>只有显式提供隔离 NATS 时才运行，测试会重建固定平台 Stream。</summary>
public sealed class WorkersNatsFactAttribute : FactAttribute
{
    public WorkersNatsFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("WORKERS_TEST_NATS")))
        {
            Skip = "设置 WORKERS_TEST_NATS 后运行 JetStream 集成测试。";
        }
    }
}

/// <summary>验证 Inbox 去重、乱序保护、失败人工状态以及 JetStream 发布确认和 MsgId 去重。</summary>
public sealed class WorkersExternalIntegrationTests
{
    [WorkersPostgresFact]
    public async Task Inbox_DeduplicatesAckLoss_AndRejectsOutOfOrderOverwrite()
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresConnection());
        await ResetSchemaAsync(dataSource);
        var storage = new WorkerStorage(dataSource);
        var latest = CreateRoomEvent(2, "Playing");
        var stale = CreateRoomEvent(1, "Starting");

        Assert.Equal(
            ProjectionResult.Applied,
            await storage.ApplyAsync(
                "audit-projection-v1", "room.state.changed.v1",
                ProjectionKind.Audit, latest, DateTimeOffset.UtcNow,
                CancellationToken.None));
        // 模拟业务已提交但 ACK 丢失：相同事件再次投递必须快速确认且不重复插入。
        Assert.Equal(
            ProjectionResult.Duplicate,
            await storage.ApplyAsync(
                "audit-projection-v1", "room.state.changed.v1",
                ProjectionKind.Audit, latest, DateTimeOffset.UtcNow,
                CancellationToken.None));
        Assert.Equal(
            ProjectionResult.Stale,
            await storage.ApplyAsync(
                "audit-projection-v1", "room.state.changed.v1",
                ProjectionKind.Audit, stale, DateTimeOffset.UtcNow,
                CancellationToken.None));

        await using var count = dataSource.CreateCommand(
            "SELECT COUNT(*) FROM worker_projection.audit_events");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [WorkersPostgresFact]
    public async Task PoisonMessage_IsIdempotentlyRecordedForManualReview()
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresConnection());
        await ResetSchemaAsync(dataSource);
        var storage = new WorkerStorage(dataSource);
        var envelope = CreateRoomEvent(1, "Waiting");
        await storage.RecordFailureAsync(
            "audit-projection-v1", "room.state.changed.v1", envelope,
            12, "UNSUPPORTED_SCHEMA", "future schema", DateTimeOffset.UtcNow,
            CancellationToken.None);
        await storage.RecordFailureAsync(
            "audit-projection-v1", "room.state.changed.v1", envelope,
            13, "UNSUPPORTED_SCHEMA", "future schema", DateTimeOffset.UtcNow,
            CancellationToken.None);

        await using var count = dataSource.CreateCommand(
            "SELECT COUNT(*) FROM worker_integration.failed_events WHERE status='PendingReview'");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [WorkersPostgresFact]
    public async Task DuplicateSettlement_UpdatesLeaderboardOnce_AndCannotCreateAssetSideEffects()
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresConnection());
        await ResetSchemaAsync(dataSource);
        var storage = new WorkerStorage(dataSource);
        var envelope = EventEnvelope.Create(
            new SettlementCommitted(
                MatchId.Parse("match-workers-settlement"),
                RoomId.Parse("room-workers-settlement"),
                "settlement-workers-001",
                DateTimeOffset.UtcNow),
            "match",
            "match-workers-settlement",
            1,
            "workers-tests",
            "0123456789abcdef0123456789abcdef",
            CorrelationId.Parse("workers-settlement-correlation"),
            DateTimeOffset.UtcNow);

        Assert.Equal(
            ProjectionResult.Applied,
            await storage.ApplyAsync(
                "leaderboard-projection-v1", "settlement.committed.v1",
                ProjectionKind.Leaderboard, envelope, DateTimeOffset.UtcNow,
                CancellationToken.None));
        Assert.Equal(
            ProjectionResult.Duplicate,
            await storage.ApplyAsync(
                "leaderboard-projection-v1", "settlement.committed.v1",
                ProjectionKind.Leaderboard, envelope, DateTimeOffset.UtcNow,
                CancellationToken.None));

        await using var count = dataSource.CreateCommand(
            "SELECT COUNT(*) FROM worker_projection.leaderboard_updates");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
        // Worker Schema 故意不包含资产或奖励表，重复结算事件只能更新读模型，不能产生发奖副作用。
        await using var forbidden = dataSource.CreateCommand(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema IN ('worker_integration','worker_projection') AND (table_name LIKE '%asset%' OR table_name LIKE '%reward%')");
        Assert.Equal(0L, await forbidden.ExecuteScalarAsync());
    }

    [WorkersNatsFact]
    public async Task JetStream_PublishIsAcknowledgedAndDuplicateMsgIdIsStoredOnce()
    {
        var url = Environment.GetEnvironmentVariable("WORKERS_TEST_NATS")!;
        await using var client = new NatsClient(url, "workers-integration-setup");
        var jetStream = client.CreateJetStreamContext();
        var stream = await jetStream.CreateOrUpdateStreamAsync(
            new StreamConfig(
                PlatformEventSubjects.StreamName,
                PlatformEventSubjects.All.Append(
                    PlatformEventSubjects.DeadLetterSubject).ToArray())
            {
                Storage = StreamConfigStorage.Memory,
                NumReplicas = 1
            });
        await stream.PurgeAsync(new StreamPurgeRequest());
        await using var publisher = new NatsJetStreamEventPublisher(
            url,
            "workers-integration-publisher");
        var envelope = CreateRoomEvent(1, "Waiting");

        await publisher.PublishAsync(envelope, CancellationToken.None);
        await publisher.PublishAsync(envelope, CancellationToken.None);
        await stream.RefreshAsync();

        Assert.Equal(1L, checked((long)stream.Info.State.Messages));
    }

    [Fact]
    public async Task JetStreamUnavailable_DoesNotReturnFalseSuccess()
    {
        await using var publisher = new NatsJetStreamEventPublisher(
            "nats://127.0.0.1:1",
            "workers-unavailable-test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            publisher.PublishAsync(CreateRoomEvent(1, "Waiting"), timeout.Token));
    }

    private static EventEnvelope CreateRoomEvent(long version, string state) =>
        EventEnvelope.Create(
            new RoomStateChanged(
                RoomId.Parse("room-workers-test"),
                RoomEpoch.Parse(1),
                "Waiting",
                state,
                StateVersion.Parse(version)),
            "room",
            "room-workers-test",
            version,
            "workers-tests",
            "0123456789abcdef0123456789abcdef",
            CorrelationId.Parse("workers-test-correlation"),
            DateTimeOffset.UtcNow);

    private static string PostgresConnection() =>
        Environment.GetEnvironmentVariable("WORKERS_TEST_POSTGRES")!;

    private static async Task ResetSchemaAsync(NpgsqlDataSource dataSource)
    {
        await using (var drop = dataSource.CreateCommand(
                         "DROP SCHEMA IF EXISTS worker_projection CASCADE; DROP SCHEMA IF EXISTS worker_integration CASCADE;"))
        {
            await drop.ExecuteNonQueryAsync();
        }
        var schemaPath = FindFile(
            "Services", "Apps", "GuiyangMahjong.Workers", "Storage", "schema.sql");
        await using var create = dataSource.CreateCommand(
            await File.ReadAllTextAsync(schemaPath));
        await create.ExecuteNonQueryAsync();
    }

    private static string FindFile(params string[] segments)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            var path = segments.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("无法定位 Workers 测试资源。");
    }
}
