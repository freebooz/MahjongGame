using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;

namespace GuiyangMahjong.Admin.Storage;

public interface IAdminActionStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task CreateAsync(
        AdminActionRecord action,
        AdminAuditDraft audit,
        CancellationToken cancellationToken);
    Task<AdminActionRecord?> GetAsync(
        string actionRequestId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminActionRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken);
    Task<bool> TryTransitionAsync(
        int expectedVersion,
        AdminActionRecord action,
        AdminAuditDraft audit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        int limit,
        CancellationToken cancellationToken);
    Task AppendAuditAsync(
        AdminAuditDraft audit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminCommandOutboxRecord>> ListOutboxAsync(
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AdminCommandOutboxRecord>> ClaimOutboxAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);
    Task<bool> CompleteOutboxAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord completedAction,
        AdminAuditDraft audit,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
    Task<bool> FailOutboxAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord? failedAction,
        AdminAuditDraft audit,
        string error,
        DateTimeOffset nextAvailableAtUtc,
        bool terminal,
        CancellationToken cancellationToken);
}

public sealed class InMemoryAdminActionStore : IAdminActionStore
{
    private readonly Dictionary<string, AdminActionRecord> actions =
        new(StringComparer.Ordinal);
    private readonly List<AdminAuditRecord> audit = [];
    private readonly List<AdminCommandOutboxRecord> outbox = [];
    private readonly object gate = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task CreateAsync(
        AdminActionRecord action,
        AdminAuditDraft auditDraft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (actions.TryGetValue(action.ActionRequestId, out var existing))
            {
                EnsureSameCreate(existing, action);
                return Task.CompletedTask;
            }
            actions.Add(action.ActionRequestId, action);
            AppendAuditUnsafe(auditDraft);
        }
        return Task.CompletedTask;
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

    public Task<AdminActionRecord?> GetAsync(
        string actionRequestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            actions.TryGetValue(actionRequestId, out var action);
            return Task.FromResult(action);
        }
    }

    public Task<IReadOnlyList<AdminActionRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<AdminActionRecord> result = actions.Values
                .OrderByDescending(item => item.RequestedAtUtc)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<bool> TryTransitionAsync(
        int expectedVersion,
        AdminActionRecord action,
        AdminAuditDraft auditDraft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!actions.TryGetValue(action.ActionRequestId, out var current)
                || current.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }
            actions[action.ActionRequestId] = action;
            AppendAuditUnsafe(auditDraft);
            if (action.Status == AdminActionStatus.ApprovedAwaitingExecution)
                AppendOutboxUnsafe(action, auditDraft.OccurredAtUtc);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<AdminAuditRecord> result = audit
                .OrderByDescending(item => item.Sequence)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task AppendAuditAsync(
        AdminAuditDraft auditDraft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            AppendAuditUnsafe(auditDraft);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AdminCommandOutboxRecord>> ListOutboxAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<AdminCommandOutboxRecord> result = outbox
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<AdminCommandOutboxRecord>> ClaimOutboxAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var candidates = outbox
                .Where(item =>
                    (item.Status == "Pending" && item.AvailableAtUtc <= now)
                    || (item.Status == "Processing"
                        && item.LeaseExpiresAtUtc <= now))
                .OrderBy(item => item.AvailableAtUtc)
                .Take(limit)
                .ToArray();
            var claimed = new List<AdminCommandOutboxRecord>(candidates.Length);
            foreach (var candidate in candidates)
            {
                var replacement = candidate with
                {
                    Status = "Processing",
                    AttemptCount = candidate.AttemptCount + 1,
                    LockedAtUtc = now,
                    LockOwner = workerId,
                    LeaseExpiresAtUtc = leaseExpiresAtUtc
                };
                outbox[outbox.IndexOf(candidate)] = replacement;
                claimed.Add(replacement);
            }
            return Task.FromResult<IReadOnlyList<AdminCommandOutboxRecord>>(claimed);
        }
    }

    public Task<bool> CompleteOutboxAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord completedAction,
        AdminAuditDraft auditDraft,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var index = outbox.FindIndex(item =>
                item.OutboxId == command.OutboxId
                && item.Status == "Processing"
                && item.LockOwner == command.LockOwner);
            if (index < 0
                || !actions.TryGetValue(completedAction.ActionRequestId, out var current)
                || current.Version != completedAction.Version - 1)
                return Task.FromResult(false);
            actions[completedAction.ActionRequestId] = completedAction;
            outbox[index] = outbox[index] with
            {
                Status = "Completed",
                CompletedAtUtc = completedAtUtc,
                LockOwner = null,
                LeaseExpiresAtUtc = null,
                LastError = null
            };
            AppendAuditUnsafe(auditDraft);
            return Task.FromResult(true);
        }
    }

    public Task<bool> FailOutboxAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord? failedAction,
        AdminAuditDraft auditDraft,
        string error,
        DateTimeOffset nextAvailableAtUtc,
        bool terminal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var index = outbox.FindIndex(item =>
                item.OutboxId == command.OutboxId
                && item.Status == "Processing"
                && item.LockOwner == command.LockOwner);
            if (index < 0) return Task.FromResult(false);
            if (terminal)
            {
                if (failedAction is null
                    || !actions.TryGetValue(failedAction.ActionRequestId, out var current)
                    || current.Version != failedAction.Version - 1)
                    return Task.FromResult(false);
                actions[failedAction.ActionRequestId] = failedAction;
            }
            outbox[index] = outbox[index] with
            {
                Status = terminal ? "Failed" : "Pending",
                AvailableAtUtc = nextAvailableAtUtc,
                LockOwner = null,
                LeaseExpiresAtUtc = null,
                LastError = error.Length > 1000 ? error[..1000] : error
            };
            AppendAuditUnsafe(auditDraft);
            return Task.FromResult(true);
        }
    }

    private void AppendAuditUnsafe(AdminAuditDraft draft)
    {
        var sequence = audit.Count + 1L;
        var previousHash = audit.LastOrDefault()?.RecordHash;
        var recordHash = AdminAuditHash.Compute(sequence, draft, previousHash);
        audit.Add(new AdminAuditRecord(
            Guid.NewGuid().ToString(),
            sequence,
            draft.OccurredAtUtc,
            draft.OperatorId,
            draft.Operation,
            draft.TargetType,
            draft.TargetId,
            draft.Reason,
            draft.BeforeState,
            draft.AfterState,
            draft.ApprovalRecord,
            draft.TraceId,
            draft.TicketId,
            previousHash,
            recordHash));
    }

    private void AppendOutboxUnsafe(AdminActionRecord action, DateTimeOffset now)
    {
        if (outbox.Any(item => item.ActionRequestId == action.ActionRequestId)) return;
        outbox.Add(new AdminCommandOutboxRecord(
            Guid.NewGuid().ToString(),
            action.ActionRequestId,
            action.ActionType,
            action.TargetType,
            action.TargetId,
            JsonSerializer.SerializeToElement(action),
            action.TraceId,
            "Pending",
            0,
            now,
            now,
            null,
            null,
            null,
            null,
            null));
    }
}

public static class AdminAuditHash
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string Compute(
        long sequence,
        AdminAuditDraft draft,
        string? previousHash)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sequence,
            draft.OccurredAtUtc,
            draft.OperatorId,
            draft.Operation,
            draft.TargetType,
            draft.TargetId,
            draft.Reason,
            draft.BeforeState,
            draft.AfterState,
            draft.ApprovalRecord,
            draft.TraceId,
            draft.TicketId,
            previousHash
        }, JsonOptions);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
