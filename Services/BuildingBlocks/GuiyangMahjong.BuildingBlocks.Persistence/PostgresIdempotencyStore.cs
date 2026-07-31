using System.Security.Cryptography;
using System.Text;
using GuiyangMahjong.BuildingBlocks.Idempotency;
using GuiyangMahjong.Contracts.Common;
using Npgsql;

namespace GuiyangMahjong.BuildingBlocks.Persistence;

/// <summary>
/// PostgreSQL API 幂等实现，以 (scope,idempotency_key) 主键协调多副本请求。
/// EdgeGateway 不应注册此存储；记录必须属于执行实际业务写入的服务。
/// </summary>
public sealed class PostgresIdempotencyStore(
    NpgsqlDataSource dataSource,
    PersistenceTableNames names) : IIdempotencyStore
{
    /// <inheritdoc/>
    public async Task<IdempotencyResult> TryBeginAsync(
        string scope,
        IdempotencyKey key,
        string requestFingerprint,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        Validate(scope, requestFingerprint, expiresAt, now);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO {names.Idempotency}(
                scope,idempotency_key,request_fingerprint,status,created_at,
                expires_at,response_status,response_content_type,response_body,error_summary)
            VALUES ($1,$2,$3,'Processing',$4,$5,NULL,NULL,NULL,NULL)
            ON CONFLICT (scope,idempotency_key) DO NOTHING
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue(scope);
        insert.Parameters.AddWithValue(key.Value);
        insert.Parameters.AddWithValue(requestFingerprint);
        insert.Parameters.AddWithValue(now);
        insert.Parameters.AddWithValue(expiresAt);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return new IdempotencyResult(IdempotencyDecision.Acquired, null);
        }

        // 到期记录可以由一个竞争者原子重置；请求指纹同时更新，旧响应不再可见。
        await using var recycle = new NpgsqlCommand(
            $"""
            UPDATE {names.Idempotency}
            SET request_fingerprint=$1,status='Processing',created_at=$2,expires_at=$3,
                response_status=NULL,response_content_type=NULL,response_body=NULL,error_summary=NULL
            WHERE scope=$4 AND idempotency_key=$5 AND expires_at <= $2
            """,
            connection,
            transaction);
        recycle.Parameters.AddWithValue(requestFingerprint);
        recycle.Parameters.AddWithValue(now);
        recycle.Parameters.AddWithValue(expiresAt);
        recycle.Parameters.AddWithValue(scope);
        recycle.Parameters.AddWithValue(key.Value);
        if (await recycle.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return new IdempotencyResult(IdempotencyDecision.Acquired, null);
        }

        await using var select = new NpgsqlCommand(
            $"""
            SELECT request_fingerprint,status,response_status,
                   response_content_type,response_body
            FROM {names.Idempotency}
            WHERE scope=$1 AND idempotency_key=$2
            FOR UPDATE
            """,
            connection,
            transaction);
        select.Parameters.AddWithValue(scope);
        select.Parameters.AddWithValue(key.Value);
        await using var reader =
            await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("幂等唯一冲突后记录不可见。");
        var existingFingerprint = reader.GetString(0);
        var status = reader.GetString(1);
        var matches = FixedEquals(existingFingerprint, requestFingerprint);
        IdempotentResponse? response = null;
        if (matches && status == "Completed")
        {
            response = new IdempotentResponse(
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetFieldValue<byte[]>(4));
        }
        await reader.CloseAsync();
        await transaction.CommitAsync(cancellationToken);
        return !matches
            ? new IdempotencyResult(IdempotencyDecision.Conflict, null)
            : response is not null
                ? new IdempotencyResult(IdempotencyDecision.Replay, response)
                : new IdempotencyResult(IdempotencyDecision.InProgress, null);
    }

    /// <inheritdoc/>
    public async Task CompleteAsync(
        string scope,
        IdempotencyKey key,
        string requestFingerprint,
        IdempotentResponse response,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await CompleteAsync(
            connection,
            transaction,
            scope,
            key,
            requestFingerprint,
            response,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 在调用方业务事务中保存首次响应；业务写入回滚时响应也必须回滚。
    /// 响应正文不得超过消费服务自行配置的请求/响应上限。
    /// </summary>
    public async Task CompleteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scope,
        IdempotencyKey key,
        string requestFingerprint,
        IdempotentResponse response,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {names.Idempotency}
            SET status='Completed',response_status=$1,response_content_type=$2,
                response_body=$3,error_summary=NULL
            WHERE scope=$4 AND idempotency_key=$5
              AND request_fingerprint=$6 AND status IN ('Processing','Completed')
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(response.StatusCode);
        command.Parameters.AddWithValue(response.ContentType);
        command.Parameters.AddWithValue(response.Body);
        command.Parameters.AddWithValue(scope);
        command.Parameters.AddWithValue(key.Value);
        command.Parameters.AddWithValue(requestFingerprint);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("幂等完成记录不存在或指纹冲突。");
    }

    /// <inheritdoc/>
    public async Task FailAsync(
        string scope,
        IdempotencyKey key,
        string errorSummary,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE {names.Idempotency}
            SET status='Failed',error_summary=$1
            WHERE scope=$2 AND idempotency_key=$3 AND status='Processing'
            """);
        command.Parameters.AddWithValue(Truncate(errorSummary));
        command.Parameters.AddWithValue(scope);
        command.Parameters.AddWithValue(key.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteExpiredAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var command = dataSource.CreateCommand(
            $"""
            WITH candidates AS (
                SELECT scope,idempotency_key
                FROM {names.Idempotency}
                WHERE expires_at <= $1
                ORDER BY expires_at
                LIMIT $2
            )
            DELETE FROM {names.Idempotency} AS item
            USING candidates
            WHERE item.scope=candidates.scope
              AND item.idempotency_key=candidates.idempotency_key
            """);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(limit);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static void Validate(
        string scope,
        string fingerprint,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        if (!StrongValueValidation.IsIdentifier(scope)
            || fingerprint.Length != 64
            || expiresAt <= now)
            throw new ArgumentException("幂等记录参数无效。");
    }

    private static string Truncate(string value) =>
        value.Length <= 512 ? value : value[..512];
}
