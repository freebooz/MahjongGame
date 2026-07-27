using System.Text.Json;
using GuiyangMahjong.Admin.Domain;
using Npgsql;
using NpgsqlTypes;

namespace GuiyangMahjong.Admin.Storage;

public interface IPlayerAssetOperationStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task<PlayerAssetOperationCreateResult> CreateAsync(
        string sourceCommandId,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PlayerAssetOperationRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken);
    Task<PlayerAssetOperationRecord> SetStatusAsync(
        string sourceCommandId,
        string status,
        CancellationToken cancellationToken);
}

public sealed class InMemoryPlayerAssetOperationStore
    : IPlayerAssetOperationStore
{
    private readonly Dictionary<string, PlayerAssetOperationRecord> operations =
        new(StringComparer.Ordinal);
    private readonly object gate = new();

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task<PlayerAssetOperationCreateResult> CreateAsync(
        string sourceCommandId,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (operations.TryGetValue(sourceCommandId, out var existing))
            {
                EnsureSame(existing, operationType, action, compensationCase);
                return Task.FromResult(
                    new PlayerAssetOperationCreateResult(existing, true));
            }
            var created = CreateRecord(
                sourceCommandId,
                operationType,
                action,
                compensationCase,
                createdAtUtc);
            operations.Add(sourceCommandId, created);
            return Task.FromResult(
                new PlayerAssetOperationCreateResult(created, false));
        }
    }

    public Task<IReadOnlyList<PlayerAssetOperationRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<PlayerAssetOperationRecord>>(
                operations.Values
                    .OrderByDescending(item => item.CreatedAtUtc)
                    .Take(limit)
                    .ToArray());
        }
    }

    public Task<PlayerAssetOperationRecord> SetStatusAsync(
        string sourceCommandId,
        string status,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFinalStatus(status);
        lock (gate)
        {
            if (!operations.TryGetValue(sourceCommandId, out var current))
                throw new InvalidOperationException(
                    "Asset operation was not found.");
            if (current.Status != "ApprovedPendingWalletExecution"
                && current.Status != status)
                throw new InvalidOperationException(
                    "Asset operation already has a different terminal status.");
            var updated = current with { Status = status };
            operations[sourceCommandId] = updated;
            return Task.FromResult(updated);
        }
    }

    internal static void ValidateFinalStatus(string status)
    {
        if (status is not ("WalletCompleted" or "WalletRejected"))
            throw new InvalidOperationException(
                "Asset operation terminal status is invalid.");
    }

    internal static PlayerAssetOperationRecord CreateRecord(
        string sourceCommandId,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase,
        DateTimeOffset createdAtUtc)
    {
        var approval = action.Approval
            ?? throw new InvalidOperationException(
                "An approved action is required for an asset operation.");
        if (compensationCase.CaseType != AdminCaseType.CompensationReview
            || compensationCase.Status != "Open")
        {
            throw new InvalidOperationException(
                "An open compensation review case is required.");
        }
        if (!action.Parameters.HasValue
            || action.Parameters.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Structured asset operation parameters are required.");
        }
        var parameters = action.Parameters.Value;
        var caseId = RequireString(parameters, "caseId");
        if (caseId != compensationCase.CaseId)
        {
            throw new InvalidOperationException(
                "The approved compensation case does not match the action.");
        }
        string? assetCode = null;
        long? amount = null;
        string? rewardGrantId = null;
        if (operationType == PlayerAssetOperationType.GrantCompensation)
        {
            assetCode = RequireString(parameters, "assetCode");
            if (!parameters.TryGetProperty("amount", out var amountElement)
                || !amountElement.TryGetInt64(out var parsedAmount)
                || parsedAmount <= 0)
            {
                throw new InvalidOperationException(
                    "A positive compensation amount is required.");
            }
            amount = parsedAmount;
        }
        else
        {
            rewardGrantId = RequireString(parameters, "rewardGrantId");
        }
        return new PlayerAssetOperationRecord(
            Guid.NewGuid().ToString(),
            sourceCommandId,
            action.ActionRequestId,
            compensationCase.CaseId,
            operationType,
            action.TargetId,
            assetCode,
            amount,
            rewardGrantId,
            action.RequestedBy,
            approval.ApprovedBy,
            createdAtUtc,
            action.Reason,
            action.TicketId,
            action.TraceId,
            action.BeforeState,
            "ApprovedPendingWalletExecution");
    }

    internal static void EnsureSame(
        PlayerAssetOperationRecord existing,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase)
    {
        if (existing.OperationType != operationType
            || existing.ActionRequestId != action.ActionRequestId
            || existing.CaseId != compensationCase.CaseId
            || existing.PlayerId != action.TargetId
            || existing.TraceId != action.TraceId
            || existing.TicketId != action.TicketId)
        {
            throw new InvalidOperationException(
                "Asset command id was reused with different parameters.");
        }
    }

    private static string RequireString(
        JsonElement parameters,
        string propertyName)
    {
        if (!parameters.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException(
                $"Asset operation parameter {propertyName} is required.");
        }
        return element.GetString()!;
    }
}

public sealed class PostgresPlayerAssetOperationStore(NpgsqlDataSource postgres)
    : IPlayerAssetOperationStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Storage", "schema.sql");
        await using var command = postgres.CreateCommand(
            await File.ReadAllTextAsync(path, cancellationToken));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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

    public async Task<PlayerAssetOperationCreateResult> CreateAsync(
        string sourceCommandId,
        PlayerAssetOperationType operationType,
        AdminActionRecord action,
        AdminCaseRecord compensationCase,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var proposed = InMemoryPlayerAssetOperationStore.CreateRecord(
            sourceCommandId,
            operationType,
            action,
            compensationCase,
            createdAtUtc);
        await using var connection =
            await postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using (var commandLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))",
            connection,
            transaction))
        {
            commandLock.Parameters.AddWithValue(sourceCommandId);
            await commandLock.ExecuteNonQueryAsync(cancellationToken);
        }
        var existing = await GetByCommandAsync(
            connection,
            transaction,
            sourceCommandId,
            cancellationToken);
        if (existing is not null)
        {
            InMemoryPlayerAssetOperationStore.EnsureSame(
                existing,
                operationType,
                action,
                compensationCase);
            await transaction.CommitAsync(cancellationToken);
            return new PlayerAssetOperationCreateResult(existing, true);
        }
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO admin_monitor.player_asset_operations(
                operation_id, source_command_id, action_request_id, case_id,
                operation_type, player_id, asset_code, amount, reward_grant_id,
                requested_by, approved_by, created_at_utc, reason, ticket_id,
                trace_id, before_state, status)
            VALUES (
                $1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,
                'ApprovedPendingWalletExecution')
            """,
            connection,
            transaction);
        AddParameters(insert, proposed);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PlayerAssetOperationCreateResult(proposed, false);
    }

    public async Task<IReadOnlyList<PlayerAssetOperationRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = postgres.CreateCommand(
            $"""
            {SelectSql}
            ORDER BY created_at_utc DESC
            LIMIT $1
            """);
        command.Parameters.AddWithValue(limit);
        var result = new List<PlayerAssetOperationRecord>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(Read(reader));
        return result;
    }

    public async Task<PlayerAssetOperationRecord> SetStatusAsync(
        string sourceCommandId,
        string status,
        CancellationToken cancellationToken)
    {
        InMemoryPlayerAssetOperationStore.ValidateFinalStatus(status);
        await using var command = postgres.CreateCommand(
            $"""
            UPDATE admin_monitor.player_asset_operations
            SET status=$1
            WHERE source_command_id=$2
              AND status IN ('ApprovedPendingWalletExecution', $1)
            RETURNING operation_id, source_command_id, action_request_id,
                      case_id, operation_type, player_id, asset_code, amount,
                      reward_grant_id, requested_by, approved_by,
                      created_at_utc, reason, ticket_id, trace_id,
                      before_state::text, status
            """);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(Guid.Parse(sourceCommandId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            return Read(reader);
        await reader.DisposeAsync();
        await using var lookup = postgres.CreateCommand(
            $"{SelectSql} WHERE source_command_id=$1");
        lookup.Parameters.AddWithValue(Guid.Parse(sourceCommandId));
        await using var lookupReader =
            await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await lookupReader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                "Asset operation was not found.");
        throw new InvalidOperationException(
            "Asset operation already has a different terminal status.");
    }

    private static async Task<PlayerAssetOperationRecord?> GetByCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceCommandId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"{SelectSql} WHERE source_command_id=$1",
            connection,
            transaction);
        command.Parameters.AddWithValue(Guid.Parse(sourceCommandId));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static void AddParameters(
        NpgsqlCommand command,
        PlayerAssetOperationRecord record)
    {
        command.Parameters.AddWithValue(Guid.Parse(record.OperationId));
        command.Parameters.AddWithValue(Guid.Parse(record.SourceCommandId));
        command.Parameters.AddWithValue(Guid.Parse(record.ActionRequestId));
        command.Parameters.AddWithValue(Guid.Parse(record.CaseId));
        command.Parameters.AddWithValue(record.OperationType.ToString());
        command.Parameters.AddWithValue(record.PlayerId);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Varchar,
            Value = (object?)record.AssetCode ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Bigint,
            Value = (object?)record.Amount ?? DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Varchar,
            Value = (object?)record.RewardGrantId ?? DBNull.Value
        });
        command.Parameters.AddWithValue(record.RequestedBy);
        command.Parameters.AddWithValue(record.ApprovedBy);
        command.Parameters.AddWithValue(record.CreatedAtUtc);
        command.Parameters.AddWithValue(record.Reason);
        command.Parameters.AddWithValue(record.TicketId);
        command.Parameters.AddWithValue(record.TraceId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(record.BeforeState, JsonOptions));
    }

    private static PlayerAssetOperationRecord Read(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0).ToString(),
            reader.GetGuid(1).ToString(),
            reader.GetGuid(2).ToString(),
            reader.GetGuid(3).ToString(),
            Enum.Parse<PlayerAssetOperationType>(reader.GetString(4)),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            JsonSerializer.Deserialize<JsonElement>(reader.GetString(15)),
            reader.GetString(16));

    private const string SelectColumns =
        """
        SELECT operation_id, source_command_id, action_request_id, case_id,
               operation_type, player_id, asset_code, amount, reward_grant_id,
               requested_by, approved_by, created_at_utc, reason, ticket_id,
               trace_id, before_state::text, status
        """;

    private const string SelectSql =
        $"""
        {SelectColumns}
        FROM admin_monitor.player_asset_operations
        """;

    public ValueTask DisposeAsync() => postgres.DisposeAsync();
}
