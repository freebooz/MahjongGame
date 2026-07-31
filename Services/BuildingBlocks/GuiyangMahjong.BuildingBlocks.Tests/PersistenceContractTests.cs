using GuiyangMahjong.BuildingBlocks.Messaging;
using GuiyangMahjong.BuildingBlocks.Persistence;
using GuiyangMahjong.Contracts.Common;
using GuiyangMahjong.Contracts.Events;
using Xunit;

namespace GuiyangMahjong.BuildingBlocks.Tests;

/// <summary>无需外部数据库即可验证迁移、表约束、消息模型和测试发布器契约。</summary>
public sealed class PersistenceContractTests
{
    /// <summary>升级和回滚 SQL 必须限定到服务自有 Schema，并包含全部唯一与领取约束。</summary>
    [Fact]
    public void SchemaMigration_DefinesOwnedTablesAndSafeRollback()
    {
        var names = new PersistenceTableNames("identity");
        var up = PlatformPersistenceSchema.BuildUpSql(names);
        var down = PlatformPersistenceSchema.BuildDownSql(names);

        Assert.Contains("\"identity\".\"platform_outbox\"", up);
        Assert.Contains("PRIMARY KEY (consumer_name, event_id)", up);
        Assert.Contains("PRIMARY KEY (scope, idempotency_key)", up);
        Assert.Contains("lease_expires_at", up);
        Assert.DoesNotContain("DROP SCHEMA", down, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(
            () => new PersistenceTableNames("identity;DROP SCHEMA public"));
    }

    /// <summary>迁移模板必须随构建输出发布，并保留显式 Schema 替换标记。</summary>
    [Fact]
    public void MigrationTemplates_ArePublishedWithSchemaPlaceholder()
    {
        foreach (var fileName in new[]
                 {
                     "0001_platform_building_blocks.up.sql",
                     "0001_platform_building_blocks.down.sql"
                 })
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "Migrations",
                fileName);
            Assert.True(File.Exists(path), $"缺少迁移模板：{path}");
            Assert.Contains("__SCHEMA__", File.ReadAllText(path));
        }
    }

    /// <summary>测试发布器只记录已发布事件，不改变信封 ID、版本或载荷。</summary>
    [Fact]
    public async Task InMemoryPublisher_PreservesEventEnvelope()
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = EventEnvelope.Create(
            new RoomTerminated(
                RoomId.Parse("room-publisher-001"),
                RoomEpoch.Parse(1),
                "normal_close",
                now),
            "Room",
            "room-publisher-001",
            1,
            "LobbyControl",
            "0123456789abcdef0123456789abcdef",
            CorrelationId.Parse("correlation-publisher-001"),
            now);
        var publisher = new InMemoryEventPublisher();

        await publisher.PublishAsync(
            envelope,
            CancellationToken.None);

        Assert.Equal(envelope, Assert.Single(publisher.Events));
    }
}
