using GuiyangMahjong.Configuration.Infrastructure;
using GuiyangMahjong.Configuration.Domain;
using GuiyangMahjong.Configuration.Options;
using GuiyangMahjong.Configuration.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace GuiyangMahjong.Configuration.Tests;

/// <summary>验证阶段 11 PostgreSQL Expand 迁移可重复执行，且运行存储能读取迁移产物。</summary>
public sealed class ConfigurationPostgresTests
{
    /// <summary>外部 PostgreSQL 仅由 CI 显式启用；普通单元测试不依赖本机数据库。</summary>
    [Fact, Trait("Category", "ExternalPersistence")]
    public async Task Schema_IsIdempotent_AndStoreBecomesReady()
    {
        var connectionString = Environment.GetEnvironmentVariable("CONFIGURATION_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schemaPath = FindSchema();
        await using var source = NpgsqlDataSource.Create(connectionString);
        var sql = await File.ReadAllTextAsync(schemaPath);
        await using (var first = source.CreateCommand(sql)) await first.ExecuteNonQueryAsync();
        await using (var second = source.CreateCommand(sql)) await second.ExecuteNonQueryAsync();
        var store = new PostgresConfigurationStore(source);
        Assert.True(await store.CheckHealthAsync(default));
        var service = new PlatformConfigurationService(
            store,
            Microsoft.Extensions.Options.Options.Create(new ConfigurationOptions
            {
                SigningKey = "postgres-test-signing-key-at-least-32-chars",
                AdminCommandToken = "postgres-test-admin-token-at-least-32-chars",
                ServiceReadToken = "postgres-test-reader-token-at-least-32-chars"
            }), TimeProvider.System, NullLogger<PlatformConfigurationService>.Instance);
        var suffix = Guid.NewGuid().ToString("N");
        var payload = new PlatformConfigurationPayload(
            new("1.0.0", "1.0.0", [], ["1"]), 1, new Dictionary<string, bool>(), [],
            [new($"route-{suffix}", "fleet-stable", $"build-{suffix}", $"sha256:{new string('a', 64)}",
                $"rules-{suffix}", new string('b', 64), "1", "cn-test", "cell-test", "stable", null, false)],
            [], "risk-v1");
        var draft = await service.CreateDraftAsync(new(PlatformConfigurationService.PlatformConfigKey, 1, payload, "ci", $"ticket-{suffix}"),
            "ci-operator", $"trace-{suffix}", $"draft-{suffix}", default);
        await service.ValidateDraftAsync(draft.DraftId, "ci-operator", default);
        var published = await service.PublishAsync(draft.DraftId,
            new("ci-operator", "ci-approver", $"approval-{suffix}", "ci", $"ticket-{suffix}", $"trace-{suffix}", $"publish-{suffix}"), default);
        Assert.True(service.Verify(published));
        await using var outbox = source.CreateCommand("SELECT count(*) FROM configuration_integration.platform_outbox WHERE aggregate_id=$1");
        outbox.Parameters.AddWithValue(PlatformConfigurationService.PlatformConfigKey);
        Assert.True((long)(await outbox.ExecuteScalarAsync() ?? 0L) >= 1);

        // 发布正本的 UPDATE 被数据库触发器拒绝，证明 Contract 阶段不能误覆盖历史。
        await using var overwrite = source.CreateCommand("UPDATE configuration.config_versions SET payload_hash=$1 WHERE version_id=$2");
        overwrite.Parameters.AddWithValue(new string('0', 64));
        overwrite.Parameters.AddWithValue(Guid.Parse(published.VersionId));
        await Assert.ThrowsAsync<PostgresException>(() => overwrite.ExecuteNonQueryAsync());
    }

    private static string FindSchema()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "GuiyangMahjong.Services.slnx"))) current = current.Parent;
        return Path.Combine(current?.FullName ?? throw new InvalidOperationException("无法定位 Services 根目录。"),
            "Apps", "GuiyangMahjong.Configuration", "Storage", "schema.sql");
    }
}
