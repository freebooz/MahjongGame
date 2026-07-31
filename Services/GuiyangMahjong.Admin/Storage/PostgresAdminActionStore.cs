// PostgreSQL Admin 操作存储：以事务方式保存请求、双人审批、派发结果和不可变审计记录。
// SQL 必须使用参数化查询和最小权限身份；事务边界内失败时不得留下部分审批或执行状态。
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using Npgsql;
using NpgsqlTypes;

namespace GuiyangMahjong.Admin.Storage;

/// <summary>
/// PostgreSQL 管理动作生产存储。
/// 依赖数据库唯一键、乐观版本和行级锁保证多副本一致性，
/// 动作迁移、审计链及命令 Outbox 在同一事务提交；该实例拥有数据源生命周期。
/// </summary>
public sealed class PostgresAdminActionStore(
    NpgsqlDataSource postgres) : IAdminActionStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var path = AdminStoragePaths.SchemaPath;
        await using var command = postgres.CreateCommand(
            await File.ReadAllTextAsync(path, cancellationToken));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = postgres.CreateCommand("SELECT 1");
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task CreateAsync(
        AdminActionRecord action,
        AdminAuditDraft audit,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))",
            connection,
            transaction))
        {
            advisoryLock.Parameters.AddWithValue(action.ActionRequestId);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken);
        }
        var existing = await GetActionAsync(
            connection,
            transaction,
            action.ActionRequestId,
            cancellationToken);
        if (existing is not null)
        {
            EnsureSameCreate(existing, action);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        await InsertActionAsync(connection, transaction, action, cancellationToken);
        await AppendAuditAsync(connection, transaction, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<AdminActionRecord?> GetActionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string actionRequestId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"{ActionSelectSql} WHERE action.action_request_id=$1",
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(actionRequestId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAction(reader)
            : null;
    }

    private static void EnsureSameCreate(
        AdminActionRecord existing,
        AdminActionRecord proposed)
    {
        if (existing.ActionType != proposed.ActionType
            || existing.TargetType != proposed.TargetType
            || existing.TargetId != proposed.TargetId
            || existing.RequestedBy != proposed.RequestedBy
            || existing.Reason != proposed.Reason
            || existing.TicketId != proposed.TicketId
            || existing.ReasonCode != proposed.ReasonCode
            || existing.OperationDescription != proposed.OperationDescription
            || existing.IdempotencyKey != proposed.IdempotencyKey
            || existing.ExpectedStateSequence != proposed.ExpectedStateSequence
            || existing.Parameters.HasValue != proposed.Parameters.HasValue
            || (existing.Parameters.HasValue
                && !JsonElement.DeepEquals(
                    existing.Parameters.Value,
                    proposed.Parameters!.Value)))
        {
            throw new InvalidOperationException(
                "Action request id was reused with different parameters.");
        }
    }

    /// <inheritdoc/>
    public async Task<AdminActionRecord?> GetAsync(
        string actionRequestId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(actionRequestId, out var id)) return null;
        await using var command = postgres.CreateCommand(
            $"{ActionSelectSql} WHERE action.action_request_id=$1");
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAction(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AdminActionRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            $"{ActionSelectSql} ORDER BY action.requested_at_utc DESC LIMIT $1");
        command.Parameters.AddWithValue(limit);
        var result = new List<AdminActionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadAction(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> TryTransitionAsync(
        int expectedVersion,
        AdminActionRecord action,
        AdminAuditDraft audit,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = new NpgsqlCommand(
            """
            UPDATE admin_monitor.action_requests
            SET confirmed_at_utc=$1, status=$2, version=$3, confirmation=$6
            WHERE action_request_id=$4 AND version=$5
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue((object?)action.ConfirmedAtUtc ?? DBNull.Value);
        update.Parameters.AddWithValue(action.Status.ToString());
        update.Parameters.AddWithValue(action.Version);
        update.Parameters.AddWithValue(Guid.Parse(action.ActionRequestId));
        update.Parameters.AddWithValue(expectedVersion);
        update.Parameters.AddWithValue((object?)action.Confirmation ?? DBNull.Value);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (action.Approval is not null)
            await InsertApprovalAsync(connection, transaction, action, cancellationToken);
        if (action.Status == AdminActionStatus.ApprovedAwaitingExecution)
            await InsertOutboxAsync(connection, transaction, action, audit.OccurredAtUtc, cancellationToken);
        await AppendAuditAsync(connection, transaction, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            SELECT audit_id, sequence, occurred_at_utc, operator_id, operation,
                   target_type, target_id, reason, before_state::text, after_state::text,
                   approval_record::text, trace_id, ticket_id, previous_hash, record_hash
            FROM admin_monitor.audit_ledger
            ORDER BY sequence DESC
            LIMIT $1
            """);
        command.Parameters.AddWithValue(limit);
        var result = new List<AdminAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AdminAuditRecord(
                reader.GetGuid(0).ToString(),
                reader.GetInt64(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                ReadNullableJson(reader, 8),
                ReadNullableJson(reader, 9),
                ReadNullableJson(reader, 10),
                reader.GetString(11),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetString(14)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task AppendAuditAsync(
        AdminAuditDraft audit,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await AppendAuditAsync(
            connection,
            transaction,
            audit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AdminCommandOutboxRecord>> ListOutboxAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            SELECT outbox_id, action_request_id, action_type, target_type, target_id,
                   payload::text, trace_id, status, attempt_count, available_at_utc,
                   created_at_utc, locked_at_utc, lock_owner, lease_expires_at_utc,
                   completed_at_utc, last_error
            FROM admin_monitor.command_outbox
            ORDER BY created_at_utc DESC
            LIMIT $1
            """);
        command.Parameters.AddWithValue(limit);
        var result = new List<AdminCommandOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AdminCommandOutboxRecord(
                reader.GetGuid(0).ToString(),
                reader.GetGuid(1).ToString(),
                Enum.Parse<AdminManagementActionType>(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                ReadJson(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }
        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AdminCommandOutboxRecord>> ClaimOutboxAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            """
            WITH candidates AS (
                SELECT outbox_id
                FROM admin_monitor.command_outbox
                WHERE (status='Pending' AND available_at_utc <= $1)
                   OR (status='Processing' AND lease_expires_at_utc <= $1)
                ORDER BY available_at_utc
                FOR UPDATE SKIP LOCKED
                LIMIT $2
            )
            UPDATE admin_monitor.command_outbox AS outbox
            SET status='Processing',
                attempt_count=outbox.attempt_count + 1,
                locked_at_utc=$1,
                lock_owner=$3,
                lease_expires_at_utc=$4
            FROM candidates
            WHERE outbox.outbox_id=candidates.outbox_id
            RETURNING outbox.outbox_id, outbox.action_request_id, outbox.action_type,
                      outbox.target_type, outbox.target_id, outbox.payload::text,
                      outbox.trace_id, outbox.status, outbox.attempt_count,
                      outbox.available_at_utc, outbox.created_at_utc, outbox.locked_at_utc,
                      outbox.lock_owner, outbox.lease_expires_at_utc,
                      outbox.completed_at_utc, outbox.last_error
            """);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(leaseExpiresAtUtc);
        var result = new List<AdminCommandOutboxRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadOutbox(reader));
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> CompleteOutboxAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord completedAction,
        AdminAuditDraft audit,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await UpdateActionStatusAsync(
                connection, transaction, completedAction, cancellationToken)
            || !await UpdateOutboxAsync(
                connection, transaction, command, "Completed", completedAtUtc,
                null, null, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await AppendAuditAsync(connection, transaction, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> FailOutboxAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord? failedAction,
        AdminAuditDraft audit,
        string error,
        DateTimeOffset nextAvailableAtUtc,
        bool terminal,
        CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (terminal && (failedAction is null
            || !await UpdateActionStatusAsync(
                connection, transaction, failedAction, cancellationToken)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        if (!await UpdateOutboxAsync(
                connection,
                transaction,
                command,
                terminal ? "Failed" : "Pending",
                null,
                nextAvailableAtUtc,
                error,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await AppendAuditAsync(connection, transaction, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> UpdateActionStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminActionRecord action,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            """
            UPDATE admin_monitor.action_requests
            SET status=$1, version=$2
            WHERE action_request_id=$3 AND version=$4
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue(action.Status.ToString());
        update.Parameters.AddWithValue(action.Version);
        update.Parameters.AddWithValue(Guid.Parse(action.ActionRequestId));
        update.Parameters.AddWithValue(action.Version - 1);
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<bool> UpdateOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminCommandOutboxRecord command,
        string status,
        DateTimeOffset? completedAtUtc,
        DateTimeOffset? availableAtUtc,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand(
            """
            UPDATE admin_monitor.command_outbox
            SET status=$1,
                completed_at_utc=$2,
                available_at_utc=COALESCE($3, available_at_utc),
                lock_owner=NULL,
                lease_expires_at_utc=NULL,
                last_error=$4
            WHERE outbox_id=$5 AND status='Processing' AND lock_owner=$6
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue(status);
        update.Parameters.AddWithValue((object?)completedAtUtc ?? DBNull.Value);
        update.Parameters.AddWithValue((object?)availableAtUtc ?? DBNull.Value);
        update.Parameters.AddWithValue((object?)TruncateError(error) ?? DBNull.Value);
        update.Parameters.AddWithValue(Guid.Parse(command.OutboxId));
        update.Parameters.AddWithValue(command.LockOwner!);
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertActionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminActionRecord action,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.action_requests(
                action_request_id, action_type, target_type, target_id, requested_by,
                requested_at_utc, confirmation_expires_at_utc, confirmed_at_utc,
                reason, ticket_id, trace_id, expected_state_sequence, expected_state_hash,
                before_state, status, expires_at_utc, version, action_parameters,
                reason_code, operation_description, confirmation, idempotency_key)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,$22)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(action.ActionRequestId));
        command.Parameters.AddWithValue(action.ActionType.ToString());
        command.Parameters.AddWithValue(action.TargetType);
        command.Parameters.AddWithValue(action.TargetId);
        command.Parameters.AddWithValue(action.RequestedBy);
        command.Parameters.AddWithValue(action.RequestedAtUtc);
        command.Parameters.AddWithValue(action.ConfirmationExpiresAtUtc);
        command.Parameters.AddWithValue((object?)action.ConfirmedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(action.Reason);
        command.Parameters.AddWithValue(action.TicketId);
        command.Parameters.AddWithValue(action.TraceId);
        command.Parameters.AddWithValue((object?)action.ExpectedStateSequence ?? DBNull.Value);
        command.Parameters.AddWithValue(action.ExpectedStateHash);
        AddJson(command, action.BeforeState);
        command.Parameters.AddWithValue(action.Status.ToString());
        command.Parameters.AddWithValue(action.ExpiresAtUtc);
        command.Parameters.AddWithValue(action.Version);
        AddNullableJson(command, action.Parameters);
        command.Parameters.AddWithValue(action.ReasonCode);
        command.Parameters.AddWithValue(action.OperationDescription);
        command.Parameters.AddWithValue((object?)action.Confirmation ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)action.IdempotencyKey ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertApprovalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminActionRecord action,
        CancellationToken cancellationToken)
    {
        var approval = action.Approval!;
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.action_approvals(
                approval_id, action_request_id, approved_by, approved_at_utc, decision, comment)
            VALUES ($1,$2,$3,$4,$5,$6)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(approval.ApprovalId));
        command.Parameters.AddWithValue(Guid.Parse(action.ActionRequestId));
        command.Parameters.AddWithValue(approval.ApprovedBy);
        command.Parameters.AddWithValue(approval.ApprovedAtUtc);
        command.Parameters.AddWithValue(approval.Decision.ToString());
        command.Parameters.AddWithValue(approval.Comment);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminActionRecord action,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.command_outbox(
                outbox_id, action_request_id, action_type, target_type, target_id,
                payload, trace_id, status, attempt_count, available_at_utc, created_at_utc)
            VALUES ($1,$2,$3,$4,$5,$6,$7,'Pending',0,$8,$8)
            ON CONFLICT (action_request_id) DO NOTHING
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(Guid.Parse(action.ActionRequestId));
        command.Parameters.AddWithValue(action.ActionType.ToString());
        command.Parameters.AddWithValue(action.TargetType);
        command.Parameters.AddWithValue(action.TargetId);
        AddJson(command, JsonSerializer.SerializeToElement(action, JsonOptions));
        command.Parameters.AddWithValue(action.TraceId);
        command.Parameters.AddWithValue(now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AppendAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AdminAuditDraft draft,
        CancellationToken cancellationToken)
    {
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(782347219)", connection, transaction))
        {
            _ = await advisoryLock.ExecuteScalarAsync(cancellationToken);
        }

        long sequence;
        string? previousHash;
        await using (var previous = new NpgsqlCommand(
            """
            SELECT sequence, record_hash
            FROM admin_monitor.audit_ledger
            ORDER BY sequence DESC
            LIMIT 1
            """,
            connection,
            transaction))
        await using (var reader = await previous.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                sequence = reader.GetInt64(0) + 1;
                previousHash = reader.GetString(1);
            }
            else
            {
                sequence = 1;
                previousHash = null;
            }
        }

        var recordHash = AdminAuditHash.Compute(sequence, draft, previousHash);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.audit_ledger(
                audit_id, sequence, occurred_at_utc, operator_id, operation,
                target_type, target_id, reason, before_state, after_state,
                approval_record, trace_id, ticket_id, previous_hash, record_hash)
            OVERRIDING SYSTEM VALUE
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue(Guid.NewGuid());
        insert.Parameters.AddWithValue(sequence);
        insert.Parameters.AddWithValue(draft.OccurredAtUtc);
        insert.Parameters.AddWithValue(draft.OperatorId);
        insert.Parameters.AddWithValue(draft.Operation);
        insert.Parameters.AddWithValue(draft.TargetType);
        insert.Parameters.AddWithValue(draft.TargetId);
        insert.Parameters.AddWithValue(draft.Reason);
        AddNullableJson(insert, draft.BeforeState);
        AddNullableJson(insert, draft.AfterState);
        AddNullableJson(insert, draft.ApprovalRecord);
        insert.Parameters.AddWithValue(draft.TraceId);
        insert.Parameters.AddWithValue(draft.TicketId);
        insert.Parameters.AddWithValue((object?)previousHash ?? DBNull.Value);
        insert.Parameters.AddWithValue(recordHash);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string ActionSelectSql =
        """
        SELECT action.action_request_id, action.action_type, action.target_type,
               action.target_id, action.requested_by, action.requested_at_utc,
               action.confirmation_expires_at_utc, action.expires_at_utc,
               action.confirmed_at_utc, action.reason, action.ticket_id, action.trace_id,
               action.expected_state_sequence, action.expected_state_hash,
               action.before_state::text, action.status, action.version,
               action.action_parameters::text, action.reason_code,
               action.operation_description, action.confirmation, action.idempotency_key,
               approval.approval_id, approval.approved_by, approval.approved_at_utc,
               approval.decision, approval.comment
        FROM admin_monitor.action_requests AS action
        LEFT JOIN admin_monitor.action_approvals AS approval
          ON approval.action_request_id=action.action_request_id
        """;

    private static AdminActionRecord ReadAction(NpgsqlDataReader reader)
    {
        AdminActionApproval? approval = reader.IsDBNull(22)
            ? null
            : new AdminActionApproval(
                reader.GetGuid(22).ToString(),
                reader.GetString(23),
                reader.GetFieldValue<DateTimeOffset>(24),
                Enum.Parse<ApprovalDecision>(reader.GetString(25)),
                reader.GetString(26));
        return new AdminActionRecord(
            reader.GetGuid(0).ToString(),
            Enum.Parse<AdminManagementActionType>(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            ReadJson(reader.GetString(14)),
            Enum.Parse<AdminActionStatus>(reader.GetString(15)),
            approval,
            reader.GetInt32(16),
            ReadNullableJson(reader, 17),
            reader.GetString(18),
            reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21));
    }

    private static AdminCommandOutboxRecord ReadOutbox(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0).ToString(),
            reader.GetGuid(1).ToString(),
            Enum.Parse<AdminManagementActionType>(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            ReadJson(reader.GetString(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
            reader.IsDBNull(15) ? null : reader.GetString(15));

    private static string? TruncateError(string? error) =>
        error is null || error.Length <= 1000 ? error : error[..1000];

    private static JsonElement ReadJson(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement? ReadNullableJson(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadJson(reader.GetString(ordinal));

    private static void AddJson(NpgsqlCommand command, JsonElement value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value.GetRawText()
        });

    private static void AddNullableJson(NpgsqlCommand command, JsonElement? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = value.HasValue ? value.Value.GetRawText() : DBNull.Value
        });

    /// <summary>异步释放该存储独占的 PostgreSQL 数据源和连接池。</summary>
    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
