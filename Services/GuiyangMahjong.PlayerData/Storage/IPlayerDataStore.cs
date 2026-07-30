// PlayerData 存储契约与内存实现：定义资产事务、奖励领取、支付投影和管理补偿边界。
// 所有写入都必须幂等并保留来源证据；内存实现仅供开发测试，不提供跨进程持久性。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.PlayerData.Domain;

namespace GuiyangMahjong.PlayerData.Storage;

/// <summary>
/// 玩家资产、奖励、调查证据与投影 Outbox 的一致性存储边界。
/// 奖励/钱包写入及其证据、余额版本、投影任务必须位于同一事务；
/// 多副本领取通过 workerId 和租约隔离。
/// </summary>
public interface IPlayerDataStore
{
    /// <summary>初始化或验证数据库结构；失败时 PlayerData 不得进入就绪状态。</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>检查权威资产和 Outbox 所需存储可用性，不改变玩家数据。</summary>
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>按 EventId 和来源引用幂等记录调查证据，并原子创建唯一投影任务。</summary>
    Task<EvidenceRecordResult> RecordEvidenceAsync(
        RecordEvidenceRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 原子领取奖励、增加权威余额、记录奖励及资产证据并创建投影任务；
    /// 任一幂等键冲突时整个事务失败。
    /// </summary>
    Task<EvidenceRecordResult> RecordRewardClaimAsync(
        RewardClaimRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// 执行已经双人审批的钱包增量操作；commandId 保证全链路幂等，
    /// 撤销奖励不得使权威余额为负。
    /// </summary>
    Task<WalletOperationResult> ApplyWalletOperationAsync(
        string commandId,
        AdminWalletOperationRequest request,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>返回指定玩家全部资产的当前权威余额，按资产代码稳定排序。</summary>
    Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(
        string playerId,
        CancellationToken cancellationToken);

    /// <summary>领取到期投影并建立 UTC 租约；过期 Processing 项允许其他工作者恢复。</summary>
    Task<IReadOnlyList<ProjectionOutboxRecord>> ClaimProjectionsAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    /// <summary>当前租约所有者确认投影成功；所有者不匹配时不得改变记录。</summary>
    Task CompleteProjectionAsync(
        string eventId,
        string workerId,
        CancellationToken cancellationToken);

    /// <summary>当前租约所有者记录失败并释放租约；永久失败停止自动重试。</summary>
    Task FailProjectionAsync(
        string eventId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken);
}

/// <summary>
/// 单进程开发/测试用 PlayerData 实现。
/// gate 将奖励、余额、证据和 Outbox 变化组成单个临界区以模拟事务；
/// 所有数据随进程退出丢失，禁止在多副本或生产经济系统启用。
/// </summary>
public sealed class InMemoryPlayerDataStore : IPlayerDataStore
{
    // 各字典分别保存权威证据、奖励、余额、命令回执和投影任务，只能在 gate 内联合修改。
    private readonly Dictionary<string, StoredEvidence> evidence =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredReward> rewards =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, WalletBalance> balances =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, WalletOperationEntry> walletOperations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProjectionOutboxRecord> projections =
        new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <inheritdoc/>
    public Task<EvidenceRecordResult> RecordEvidenceAsync(
        RecordEvidenceRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(RecordEvidenceUnsafe(
                request,
                recordedAtUtc));
        }
    }

    /// <inheritdoc/>
    public Task<EvidenceRecordResult> RecordRewardClaimAsync(
        RewardClaimRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (evidence.TryGetValue(request.EventId, out var existing))
            {
                if (existing.EvidenceType != PlayerEvidenceType.RewardClaim
                    || existing.PlayerId != request.PlayerId
                    || existing.SourceReference != request.SourceReference)
                {
                    throw PlayerDataOperationException.Conflict(
                        "Reward event id was reused with different parameters.");
                }
                return Task.FromResult(
                    new EvidenceRecordResult(request.EventId, true));
            }
            if (rewards.TryGetValue(request.RewardGrantId, out var reward))
            {
                if (reward.PlayerId != request.PlayerId
                    || reward.AssetCode != request.AssetCode
                    || reward.Amount != request.Amount)
                {
                    throw PlayerDataOperationException.Conflict(
                        "Reward grant id was reused with different parameters.");
                }
                return Task.FromResult(
                    new EvidenceRecordResult(request.EventId, true));
            }

            var now = NormalizeTimestamp(recordedAtUtc);
            var assetCode = request.AssetCode.ToUpperInvariant();
            var updated = AddBalanceUnsafe(
                request.PlayerId,
                assetCode,
                request.Amount,
                now);
            rewards.Add(
                request.RewardGrantId,
                new StoredReward(
                    request.RewardGrantId,
                    request.PlayerId,
                    assetCode,
                    request.Amount,
                    "Claimed",
                    NormalizeTimestamp(request.OccurredAtUtc)));
            var rewardEvidence = new RecordEvidenceRequest(
                request.EventId,
                request.PlayerId,
                PlayerEvidenceType.RewardClaim,
                request.OccurredAtUtc,
                request.SourceReference,
                JsonSerializer.SerializeToElement(new
                {
                    request.RewardGrantId,
                    assetCode,
                    request.Amount,
                    status = "Claimed",
                    request.TraceId
                }),
                PlayerEvidenceSensitivity.Financial);
            RecordEvidenceUnsafe(rewardEvidence, now);
            RecordEvidenceUnsafe(
                new RecordEvidenceRequest(
                    CreateDerivedId(request.EventId, "asset-change"),
                    request.PlayerId,
                    PlayerEvidenceType.AssetChange,
                    request.OccurredAtUtc,
                    $"reward-asset:{request.RewardGrantId}",
                    JsonSerializer.SerializeToElement(new
                    {
                        transactionType = "RewardClaim",
                        request.RewardGrantId,
                        assetCode,
                        amount = request.Amount,
                        balanceAfter = updated.Balance,
                        balanceVersion = updated.Version,
                        request.TraceId
                    }),
                    PlayerEvidenceSensitivity.Financial),
                now);
            return Task.FromResult(
                new EvidenceRecordResult(request.EventId, false));
        }
    }

    /// <inheritdoc/>
    public Task<WalletOperationResult> ApplyWalletOperationAsync(
        string commandId,
        AdminWalletOperationRequest request,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (walletOperations.TryGetValue(commandId, out var existing))
            {
                EnsureSame(existing.Request, request);
                return Task.FromResult(existing.Result with { Duplicate = true });
            }
            if (request.RequestedBy == request.ApprovedBy)
            {
                throw PlayerDataOperationException.Conflict(
                    "Wallet operations require a separate approver.");
            }

            string assetCode;
            long amount;
            if (request.OperationType == "GrantCompensation")
            {
                assetCode = request.AssetCode?.ToUpperInvariant()
                    ?? throw PlayerDataOperationException.Invalid(
                        "assetCode is required for compensation.");
                amount = request.Amount
                    ?? throw PlayerDataOperationException.Invalid(
                        "amount is required for compensation.");
                if (amount <= 0)
                    throw PlayerDataOperationException.Invalid(
                        "Compensation amount must be positive.");
            }
            else if (request.OperationType == "RevokeReward")
            {
                var rewardId = request.RewardGrantId
                    ?? throw PlayerDataOperationException.Invalid(
                        "rewardGrantId is required for reward reversal.");
                if (!rewards.TryGetValue(rewardId, out var reward)
                    || reward.PlayerId != request.PlayerId
                    || reward.Status != "Claimed")
                {
                    throw PlayerDataOperationException.Conflict(
                        "The referenced claimed reward was not found.");
                }
                assetCode = reward.AssetCode;
                amount = -reward.Amount;
                var current = GetBalanceUnsafe(request.PlayerId, assetCode);
                if (current.Balance + amount < 0)
                {
                    throw PlayerDataOperationException.Conflict(
                        "Reward reversal would make the authoritative balance negative.");
                }
                rewards[rewardId] = reward with { Status = "Revoked" };
            }
            else
            {
                throw PlayerDataOperationException.Invalid(
                    "Wallet operation type is invalid.");
            }

            var now = NormalizeTimestamp(completedAtUtc);
            var balance = AddBalanceUnsafe(
                request.PlayerId,
                assetCode,
                amount,
                now);
            var result = new WalletOperationResult(
                commandId,
                Guid.NewGuid().ToString(),
                request.OperationType,
                request.PlayerId,
                assetCode,
                amount,
                balance.Balance,
                balance.Version,
                "Completed",
                false,
                now);
            walletOperations.Add(
                commandId,
                new WalletOperationEntry(request, result));
            RecordEvidenceUnsafe(
                new RecordEvidenceRequest(
                    CreateDerivedId(commandId, "asset-change"),
                    request.PlayerId,
                    PlayerEvidenceType.AssetChange,
                    now,
                    $"admin-wallet:{commandId}",
                    JsonSerializer.SerializeToElement(new
                    {
                        result.TransactionId,
                        result.OperationType,
                        result.AssetCode,
                        result.Amount,
                        result.BalanceAfter,
                        result.BalanceVersion,
                        request.CaseId,
                        request.TicketId,
                        request.TraceId
                    }),
                    PlayerEvidenceSensitivity.Financial),
                now);
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(
        string playerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<WalletBalance>>(
                balances.Values
                    .Where(item => item.PlayerId == playerId)
                    .OrderBy(item => item.AssetCode, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProjectionOutboxRecord>> ClaimProjectionsAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var claimed = projections.Values
                .Where(item =>
                    (item.Status == "Pending"
                        && item.AvailableAtUtc <= now)
                    || (item.Status == "Processing"
                        && item.LeaseExpiresAtUtc <= now))
                .OrderBy(item => item.AvailableAtUtc)
                .Take(limit)
                .Select(item => item with
                {
                    Status = "Processing",
                    AttemptCount = item.AttemptCount + 1,
                    LockOwner = workerId,
                    LeaseExpiresAtUtc = leaseExpiresAtUtc
                })
                .ToArray();
            foreach (var item in claimed) projections[item.EventId] = item;
            return Task.FromResult<IReadOnlyList<ProjectionOutboxRecord>>(
                claimed);
        }
    }

    /// <inheritdoc/>
    public Task CompleteProjectionAsync(
        string eventId,
        string workerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (projections.TryGetValue(eventId, out var current)
                && current.Status == "Processing"
                && current.LockOwner == workerId)
            {
                projections[eventId] = current with
                {
                    Status = "Completed",
                    LockOwner = null,
                    LeaseExpiresAtUtc = null,
                    LastError = null
                };
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FailProjectionAsync(
        string eventId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (projections.TryGetValue(eventId, out var current)
                && current.Status == "Processing"
                && current.LockOwner == workerId)
            {
                projections[eventId] = current with
                {
                    Status = terminal ? "Failed" : "Pending",
                    AvailableAtUtc = availableAtUtc,
                    LockOwner = null,
                    LeaseExpiresAtUtc = null,
                    LastError = error.Length > 1000
                        ? error[..1000]
                        : error
                };
            }
        }
        return Task.CompletedTask;
    }

    private EvidenceRecordResult RecordEvidenceUnsafe(
        RecordEvidenceRequest request,
        DateTimeOffset recordedAtUtc)
    {
        var normalized = new StoredEvidence(
            request.EventId,
            request.PlayerId,
            request.EvidenceType,
            NormalizeTimestamp(request.OccurredAtUtc),
            request.SourceReference,
            request.Data.Clone(),
            request.Sensitivity,
            NormalizeTimestamp(recordedAtUtc));
        if (evidence.TryGetValue(request.EventId, out var existing))
        {
            EnsureSame(existing, normalized);
            return new EvidenceRecordResult(existing.EventId, true);
        }
        if (evidence.Values.Any(item =>
            item.EvidenceType == normalized.EvidenceType
            && item.SourceReference == normalized.SourceReference))
        {
            throw PlayerDataOperationException.Conflict(
                "Evidence source reference was already recorded.");
        }
        evidence.Add(request.EventId, normalized);
        projections.Add(
            request.EventId,
            new ProjectionOutboxRecord(
                request.EventId,
                ToProjectionPayload(normalized),
                "Pending",
                0,
                normalized.RecordedAtUtc,
                null,
                null,
                null));
        return new EvidenceRecordResult(request.EventId, false);
    }

    private WalletBalance AddBalanceUnsafe(
        string playerId,
        string assetCode,
        long delta,
        DateTimeOffset now)
    {
        var current = GetBalanceUnsafe(playerId, assetCode);
        var nextBalance = checked(current.Balance + delta);
        if (nextBalance < 0)
            throw PlayerDataOperationException.Conflict(
                "Wallet balance cannot become negative.");
        var updated = current with
        {
            Balance = nextBalance,
            Version = current.Version + 1,
            UpdatedAtUtc = now
        };
        balances[BalanceKey(playerId, assetCode)] = updated;
        return updated;
    }

    private WalletBalance GetBalanceUnsafe(
        string playerId,
        string assetCode) =>
        balances.TryGetValue(BalanceKey(playerId, assetCode), out var existing)
            ? existing
            : new WalletBalance(
                playerId,
                assetCode,
                0,
                0,
                DateTimeOffset.UnixEpoch);

    private static string BalanceKey(string playerId, string assetCode) =>
        $"{playerId}\n{assetCode}";

    /// <summary>生成发送给 Admin 的最小脱敏证据投影，不包含存储内部状态。</summary>
    internal static JsonElement ToProjectionPayload(
        StoredEvidence evidence) =>
        JsonSerializer.SerializeToElement(new
        {
            eventId = evidence.EventId,
            playerId = evidence.PlayerId,
            evidenceType = evidence.EvidenceType.ToString(),
            occurredAtUtc = evidence.OccurredAtUtc,
            sourceReference = evidence.SourceReference,
            data = evidence.Data,
            sensitivity = evidence.Sensitivity.ToString()
        });

    /// <summary>从 UUID 来源和稳定用途派生确定性 UUID，使重试生成同一关联事件。</summary>
    internal static string CreateDerivedId(string id, string discriminator)
    {
        if (!Guid.TryParse(id, out _))
            throw PlayerDataOperationException.Invalid(
                "Source id must be a UUID.");
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{id}\n{discriminator}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString();
    }

    /// <summary>规范化为 UTC 且裁剪到 PostgreSQL 可稳定往返的微秒精度。</summary>
    internal static DateTimeOffset NormalizeTimestamp(
        DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    /// <summary>验证 EventId 重放的所有业务字段相同，冲突复用时拒绝写入。</summary>
    internal static void EnsureSame(
        StoredEvidence existing,
        StoredEvidence proposed)
    {
        if (existing.PlayerId != proposed.PlayerId
            || existing.EvidenceType != proposed.EvidenceType
            || existing.OccurredAtUtc != proposed.OccurredAtUtc
            || existing.SourceReference != proposed.SourceReference
            || existing.Sensitivity != proposed.Sensitivity
            || !JsonElement.DeepEquals(existing.Data, proposed.Data))
        {
            throw PlayerDataOperationException.Conflict(
                "Evidence event id was reused with different parameters.");
        }
    }

    /// <summary>验证钱包 CommandId 重放的审批与业务参数完全相同。</summary>
    internal static void EnsureSame(
        AdminWalletOperationRequest existing,
        AdminWalletOperationRequest proposed)
    {
        if (existing != proposed)
            throw PlayerDataOperationException.Conflict(
                "Wallet command id was reused with different parameters.");
    }

    /// <summary>内存中的完整证据值；Data 在创建时克隆，RecordedAtUtc 由服务端生成。</summary>
    internal sealed record StoredEvidence(
        string EventId,
        string PlayerId,
        PlayerEvidenceType EvidenceType,
        DateTimeOffset OccurredAtUtc,
        string SourceReference,
        JsonElement Data,
        PlayerEvidenceSensitivity Sensitivity,
        DateTimeOffset RecordedAtUtc);

    /// <summary>奖励权威状态；Amount 为最小整数单位，Status 只能在领取和撤销间迁移。</summary>
    internal sealed record StoredReward(
        string RewardGrantId,
        string PlayerId,
        string AssetCode,
        long Amount,
        string Status,
        DateTimeOffset ClaimedAtUtc);

    private sealed record WalletOperationEntry(
        AdminWalletOperationRequest Request,
        WalletOperationResult Result);
}

/// <summary>可安全映射为 API 错误码与 HTTP 状态的 PlayerData 领域异常。</summary>
public sealed class PlayerDataOperationException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    /// <summary>稳定机器错误码，供 Admin/客户端按类型处理。</summary>
    public string Code { get; } = code;

    /// <summary>由统一异常处理器采用的 HTTP 状态码。</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>创建不满足字段或策略约束的 400 错误。</summary>
    public static PlayerDataOperationException Invalid(string message) =>
        new("PLAYER_DATA_INVALID_REQUEST", message, 400);

    /// <summary>创建幂等键复用、状态冲突或余额约束对应的 409 错误。</summary>
    public static PlayerDataOperationException Conflict(string message) =>
        new("PLAYER_DATA_CONFLICT", message, 409);
}
