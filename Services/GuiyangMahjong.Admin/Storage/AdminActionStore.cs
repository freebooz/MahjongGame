// Admin 操作内存存储：为开发和测试保存操作、审批和审计状态，并模拟生产事务约束。
// 状态转换必须单调、幂等且保留前后快照；该实现不适用于多副本生产部署。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.Admin.Domain;

namespace GuiyangMahjong.Admin.Storage;

/// <summary>
/// 管理动作、审计链和命令 Outbox 的一致性存储边界。
/// 生产实现必须把动作状态、对应审计和 Outbox 变化提交在同一数据库事务中，
/// 并通过版本、租约所有者和幂等主键支持多副本安全执行。
/// </summary>
public interface IAdminActionStore
{
    /// <summary>创建表结构或验证存储版本；失败时服务不得进入就绪状态。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>检查关键表和审计链写入依赖是否可用，不产生业务数据。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 原子创建动作及首条审计记录；相同 ActionRequestId 仅允许参数完全相同的幂等重放。
    /// </summary>
    Task CreateAsync(
        AdminActionRecord action,
        AdminAuditDraft audit,
        CancellationToken cancellationToken);

    /// <summary>按动作标识读取当前快照；不存在时返回空，不附带修改跟踪。</summary>
    Task<AdminActionRecord?> GetAsync(
        string actionRequestId,
        CancellationToken cancellationToken);

    /// <summary>按申请时间倒序返回有界动作列表；调用方负责权限过滤。</summary>
    Task<IReadOnlyList<AdminActionRecord>> ListAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// 以 expectedVersion 乐观并发迁移动作并追加审计；
    /// 进入待执行状态时必须在同一事务写入唯一 Outbox，版本不匹配返回 false。
    /// </summary>
    Task<bool> TryTransitionAsync(
        int expectedVersion,
        AdminActionRecord action,
        AdminAuditDraft audit,
        CancellationToken cancellationToken);

    /// <summary>按全局序号倒序读取有界审计记录；返回值保持哈希链字段完整。</summary>
    Task<IReadOnlyList<AdminAuditRecord>> ListAuditAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>追加独立只读操作审计，并由存储层分配序号、前置哈希和记录哈希。</summary>
    Task AppendAuditAsync(
        AdminAuditDraft audit,
        CancellationToken cancellationToken);

    /// <summary>按创建时间读取 Outbox 观察视图，不领取或改变命令状态。</summary>
    Task<IReadOnlyList<AdminCommandOutboxRecord>> ListOutboxAsync(
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// 领取到期命令并建立 UTC 租约；多工作线程不能同时获得同一命令，
    /// 已过期 Processing 命令允许重新领取。
    /// </summary>
    Task<IReadOnlyList<AdminCommandOutboxRecord>> ClaimOutboxAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 仅由当前租约所有者原子完成命令、动作终态和审计；租约或版本失配返回 false。
    /// </summary>
    Task<bool> CompleteOutboxAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord completedAction,
        AdminAuditDraft audit,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 记录执行失败并释放租约；瞬态失败按 nextAvailableAtUtc 回队，
    /// terminal=true 时同时写入动作失败终态及审计。
    /// </summary>
    Task<bool> FailOutboxAsync(
        AdminCommandOutboxRecord command,
        AdminActionRecord? failedAction,
        AdminAuditDraft audit,
        string error,
        DateTimeOffset nextAvailableAtUtc,
        bool terminal,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单进程开发/测试用管理存储。
/// gate 保护三个集合的原子变化并模拟生产乐观锁和 Outbox 租约语义；
/// 数据不持久化，因此生产配置不得注册此实现。
/// </summary>
public sealed class InMemoryAdminActionStore : IAdminActionStore
{
    // 三个集合分别拥有动作聚合、追加式审计链和命令外箱；元素只在 gate 内读写。
    private readonly Dictionary<string, AdminActionRecord> actions =
        new(StringComparer.Ordinal);
    private readonly List<AdminAuditRecord> audit = [];
    private readonly List<AdminCommandOutboxRecord> outbox = [];
    private readonly object gate = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <inheritdoc/>
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

    /// <summary>验证幂等重放的业务字段完全一致；冲突复用标识会显式失败。</summary>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

/// <summary>
/// 管理审计链的确定性哈希工具。
/// 输入字段按稳定顺序和统一 JSON 序列化连接，前一记录哈希把单条审计连接为可验证链。
/// </summary>
public static class AdminAuditHash
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>计算审计记录的 SHA-256 十六进制哈希；调用方必须传入持久化前的最终字段值。</summary>
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
