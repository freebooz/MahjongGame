using GuiyangMahjong.Economy.Domain;

namespace GuiyangMahjong.Economy.Storage;

/// <summary>资产与奖励权威存储边界；所有写操作必须在单个本地事务中完成余额、流水和 Outbox。</summary>
public interface IEconomyStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken);
    Task<RewardClaimResult> ClaimRewardAsync(RewardClaimRequest request, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<WalletOperationResult> ApplyWalletOperationAsync(Guid commandId,
        AdminWalletOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(string playerId,
        CancellationToken cancellationToken);
}

/// <summary>开发和测试用线程安全存储；其行为与 PostgreSQL 的幂等和非负余额约束一致。</summary>
public sealed class InMemoryEconomyStore : IEconomyStore
{
    private readonly object gate = new();
    private readonly Dictionary<(string Player, string Asset), WalletBalance> balances = new();
    private readonly Dictionary<string, RewardClaimRequest> rewards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> rewardEvents = new(StringComparer.Ordinal);
    private readonly HashSet<string> revokedRewards = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, (AdminWalletOperationRequest Request, WalletOperationResult Result)> commands = new();

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<RewardClaimResult> ClaimRewardAsync(RewardClaimRequest request, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (rewards.TryGetValue(request.RewardGrantId, out var existing))
            {
                if (existing != request) throw Conflict("RewardGrantId was reused with another payload.");
                return Task.FromResult(new RewardClaimResult(request.EventId, true));
            }
            if (rewardEvents.TryGetValue(request.EventId, out var grantId))
            {
                if (grantId != request.RewardGrantId) throw Conflict("EventId was reused with another reward.");
                return Task.FromResult(new RewardClaimResult(request.EventId, true));
            }
            var key = (request.PlayerId, request.AssetCode);
            var current = balances.GetValueOrDefault(key,
                new WalletBalance(request.PlayerId, request.AssetCode, 0, 0, now));
            checked
            {
                balances[key] = current with { Balance = current.Balance + request.Amount,
                    Version = current.Version + 1, UpdatedAtUtc = now };
            }
            rewards.Add(request.RewardGrantId, request);
            rewardEvents.Add(request.EventId, request.RewardGrantId);
            return Task.FromResult(new RewardClaimResult(request.EventId, false));
        }
    }

    public Task<WalletOperationResult> ApplyWalletOperationAsync(Guid commandId,
        AdminWalletOperationRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (commands.TryGetValue(commandId, out var existing))
            {
                if (existing.Request != request) throw Conflict("Idempotency key was reused with another payload.");
                return Task.FromResult(existing.Result with { Duplicate = true });
            }
            string asset; long delta;
            if (request.OperationType == "GrantCompensation")
            {
                asset = request.AssetCode!; delta = request.Amount!.Value;
            }
            else
            {
                if (!rewards.TryGetValue(request.RewardGrantId!, out var reward))
                    throw new EconomyOperationException("REWARD_NOT_FOUND", "Reward grant was not found.", 404);
                if (!revokedRewards.Add(request.RewardGrantId!)) throw Conflict("Reward was already revoked.");
                asset = reward.AssetCode; delta = -reward.Amount;
            }
            var key = (request.PlayerId, asset);
            var current = balances.GetValueOrDefault(key,
                new WalletBalance(request.PlayerId, asset, 0, 0, now));
            var after = checked(current.Balance + delta);
            if (after < 0) throw Conflict("Wallet balance cannot become negative.");
            var updated = current with { Balance = after, Version = current.Version + 1, UpdatedAtUtc = now };
            balances[key] = updated;
            var result = new WalletOperationResult(commandId.ToString(), Guid.NewGuid().ToString(),
                request.OperationType, request.PlayerId, asset, delta, after, updated.Version,
                "Completed", false, now);
            commands.Add(commandId, (request, result));
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<WalletBalance>> ListBalancesAsync(string playerId,
        CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<WalletBalance>>(balances.Values
                .Where(value => value.PlayerId == playerId).OrderBy(value => value.AssetCode).ToArray());
    }

    private static EconomyOperationException Conflict(string message) =>
        new("ECONOMY_CONFLICT", message, StatusCodes.Status409Conflict);
}
