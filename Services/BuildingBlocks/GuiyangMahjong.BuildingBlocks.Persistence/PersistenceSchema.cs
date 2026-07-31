using System.Text.RegularExpressions;
using Npgsql;

namespace GuiyangMahjong.BuildingBlocks.Persistence;

/// <summary>
/// 消费服务自有 PostgreSQL Schema 下的基础构件表名。
/// Schema 名必须来自受控配置并通过白名单校验，绝不能接受请求参数。
/// </summary>
public sealed partial record PersistenceTableNames
{
    /// <summary>验证并保存消费服务拥有的 Schema；不会自动创建跨服务共享 Schema。</summary>
    public PersistenceTableNames(string schema)
    {
        if (!SafeIdentifierPattern().IsMatch(schema))
            throw new ArgumentException("PostgreSQL Schema 名格式无效。", nameof(schema));
        Schema = schema;
    }

    /// <summary>由消费服务独占写入的 Schema。</summary>
    public string Schema { get; }

    internal string Outbox => Qualified("platform_outbox");
    internal string OutboxArchive => Qualified("platform_outbox_archive");
    internal string Inbox => Qualified("platform_inbox");
    internal string Idempotency => Qualified("platform_idempotency");

    private string Qualified(string table) => $"\"{Schema}\".\"{table}\"";

    [GeneratedRegex(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifierPattern();
}

/// <summary>
/// 基础构件 PostgreSQL 迁移入口。
/// 生产应由迁移身份显式调用；运行服务默认不应具有 CREATE/DROP 权限。
/// </summary>
public static class PlatformPersistenceSchema
{
    /// <summary>
    /// 在调用方已拥有的数据库中创建服务自有 Schema 和基础表。
    /// DDL 使用 IF NOT EXISTS 支持可重复升级，但不负责业务 Schema 版本编排。
    /// </summary>
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource,
        PersistenceTableNames names,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildUpSql(names);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 回滚时只删除本基础构件创建的表，保留消费服务 Schema 和所有业务表。
    /// 该操作会永久删除去重历史，执行前必须停写并完成数据保留审批。
    /// </summary>
    public static async Task RollbackAsync(
        NpgsqlDataSource dataSource,
        PersistenceTableNames names,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildDownSql(names);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>生成经过安全标识校验的升级 SQL，供迁移工具预览和审计。</summary>
    public static string BuildUpSql(PersistenceTableNames names) =>
        $"""
        CREATE SCHEMA IF NOT EXISTS "{names.Schema}";

        CREATE TABLE IF NOT EXISTS {names.Outbox} (
            event_id TEXT PRIMARY KEY,
            event_type TEXT NOT NULL,
            schema_version INTEGER NOT NULL CHECK (schema_version > 0),
            aggregate_type TEXT NOT NULL,
            aggregate_id TEXT NOT NULL,
            aggregate_version BIGINT NOT NULL CHECK (aggregate_version >= 0),
            payload_json JSONB NOT NULL,
            occurred_at TIMESTAMPTZ NOT NULL,
            created_at TIMESTAMPTZ NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Pending','Processing','Published','Failed')),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            next_attempt_at TIMESTAMPTZ NOT NULL,
            lock_owner TEXT NULL,
            lease_expires_at TIMESTAMPTZ NULL,
            published_at TIMESTAMPTZ NULL,
            error_summary TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_platform_outbox_dispatch
            ON {names.Outbox}(status, next_attempt_at, lease_expires_at);

        CREATE TABLE IF NOT EXISTS {names.OutboxArchive}
            (LIKE {names.Outbox} INCLUDING ALL);

        CREATE TABLE IF NOT EXISTS {names.Inbox} (
            consumer_name TEXT NOT NULL,
            event_id TEXT NOT NULL,
            event_type TEXT NOT NULL,
            schema_version INTEGER NOT NULL CHECK (schema_version > 0),
            status TEXT NOT NULL CHECK (status IN ('Processing','Completed','Failed')),
            received_at TIMESTAMPTZ NOT NULL,
            completed_at TIMESTAMPTZ NULL,
            failure_count INTEGER NOT NULL DEFAULT 0 CHECK (failure_count >= 0),
            error_summary TEXT NULL,
            PRIMARY KEY (consumer_name, event_id)
        );
        CREATE INDEX IF NOT EXISTS ix_platform_inbox_cleanup
            ON {names.Inbox}(status, completed_at);

        CREATE TABLE IF NOT EXISTS {names.Idempotency} (
            scope TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            request_fingerprint CHAR(64) NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Processing','Completed','Failed')),
            created_at TIMESTAMPTZ NOT NULL,
            expires_at TIMESTAMPTZ NOT NULL,
            response_status INTEGER NULL,
            response_content_type TEXT NULL,
            response_body BYTEA NULL,
            error_summary TEXT NULL,
            PRIMARY KEY (scope, idempotency_key)
        );
        CREATE INDEX IF NOT EXISTS ix_platform_idempotency_expiry
            ON {names.Idempotency}(expires_at);
        """;

    /// <summary>生成只删除基础构件表的逆向迁移 SQL。</summary>
    public static string BuildDownSql(PersistenceTableNames names) =>
        $"""
        DROP TABLE IF EXISTS {names.Idempotency};
        DROP TABLE IF EXISTS {names.Inbox};
        DROP TABLE IF EXISTS {names.OutboxArchive};
        DROP TABLE IF EXISTS {names.Outbox};
        """;
}
