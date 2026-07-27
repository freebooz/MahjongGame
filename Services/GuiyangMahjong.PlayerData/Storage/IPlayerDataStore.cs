using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuiyangMahjong.PlayerData.Domain;

namespace GuiyangMahjong.PlayerData.Storage;

public interface IPlayerDataStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task<EvidenceRecordResult> RecordEvidenceAsync(
        RecordEvidenceRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken);
    Task<EvidenceRecordResult> RecordRewardClaimAsync(
        RewardClaimRequest request,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken);
    Task<WalletOperationResult> ApplyWalletOperationAsync(
        string commandId,
        AdminWalletOperationRequest request,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(
        string playerId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectionOutboxRecord>> ClaimProjectionsAsync(
        string workerId,
        int limit,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);
    Task CompleteProjectionAsync(
        string eventId,
        string workerId,
        CancellationToken cancellationToken);
    Task FailProjectionAsync(
        string eventId,
        string workerId,
        string error,
        DateTimeOffset availableAtUtc,
        bool terminal,
        CancellationToken cancellationToken);
}

public sealed class InMemoryPlayerDataStore : IPlayerDataStore
{
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

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(true);

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

    internal static string CreateDerivedId(string id, string discriminator)
    {
        if (!Guid.TryParse(id, out _))
            throw PlayerDataOperationException.Invalid(
                "Source id must be a UUID.");
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{id}\n{discriminator}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString();
    }

    internal static DateTimeOffset NormalizeTimestamp(
        DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

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

    internal static void EnsureSame(
        AdminWalletOperationRequest existing,
        AdminWalletOperationRequest proposed)
    {
        if (existing != proposed)
            throw PlayerDataOperationException.Conflict(
                "Wallet command id was reused with different parameters.");
    }

    internal sealed record StoredEvidence(
        string EventId,
        string PlayerId,
        PlayerEvidenceType EvidenceType,
        DateTimeOffset OccurredAtUtc,
        string SourceReference,
        JsonElement Data,
        PlayerEvidenceSensitivity Sensitivity,
        DateTimeOffset RecordedAtUtc);

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

public sealed class PlayerDataOperationException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static PlayerDataOperationException Invalid(string message) =>
        new("PLAYER_DATA_INVALID_REQUEST", message, 400);
    public static PlayerDataOperationException Conflict(string message) =>
        new("PLAYER_DATA_CONFLICT", message, 409);
}
