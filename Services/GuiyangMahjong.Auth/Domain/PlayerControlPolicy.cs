// 玩家控制策略：集中判定封禁、冻结、禁言和会话撤销对登录及游戏访问的影响。
// 策略以服务端 UTC 时间和已持久化状态为准，调用方不得自行推导或绕过优先级。
namespace GuiyangMahjong.Auth.Domain;

/// <summary>
/// 玩家账号控制的纯领域策略。
/// 该类型不访问数据库或系统时钟，调用方必须显式传入当前 UTC 时间，
/// 以便重试、测试和多副本得到确定结果。
/// </summary>
public static class PlayerControlPolicy
{
    /// <summary>尚无控制记录时的初始有效状态；UnixEpoch 表示从未更新。</summary>
    public static PlayerControlState Empty =>
        new(
            0,
            "Active",
            null,
            null,
            [],
            null,
            DateTimeOffset.UnixEpoch);

    /// <summary>
    /// 将已到期冻结、禁言和风险标签惰性恢复为有效状态。
    /// 不递增版本也不写库；调用方决定是否持久化规范化结果。
    /// </summary>
    public static PlayerControlState Normalize(
        PlayerControlState state,
        DateTimeOffset now) =>
        state with
        {
            AccountStatus =
                state.AccountStatus == "Frozen"
                && state.FrozenUntilUtc <= now
                    ? "Active"
                    : state.AccountStatus,
            FrozenUntilUtc =
                state.AccountStatus == "Frozen"
                && state.FrozenUntilUtc <= now
                    ? null
                    : state.FrozenUntilUtc,
            MutedUntilUtc = state.MutedUntilUtc <= now
                ? null
                : state.MutedUntilUtc,
            RiskLabels = state.RiskLabelsExpireAtUtc <= now
                ? []
                : state.RiskLabels,
            RiskLabelsExpireAtUtc = state.RiskLabelsExpireAtUtc <= now
                ? null
                : state.RiskLabelsExpireAtUtc
        };

    /// <summary>
    /// 尝试把一个已授权动作应用到当前状态。
    /// 成功时版本递增并写入 effectiveAtUtc；非法前置状态返回错误且不产生新状态，
    /// 未知枚举表示程序缺陷并抛出异常。
    /// </summary>
    public static (PlayerControlState? State, string? Error) Apply(
        PlayerControlState before,
        AdminPlayerControlAction action,
        DateTimeOffset effectiveAtUtc,
        DateTimeOffset? expiresAtUtc,
        string? riskLabel)
    {
        if (action == AdminPlayerControlAction.TemporaryFreezePlayer
            && before.AccountStatus == "Banned")
            return (null, "A permanently banned player cannot be temporarily frozen.");
        if (action == AdminPlayerControlAction.LiftPlayerBan
            && before.AccountStatus != "Banned")
            return (null, "The player is not permanently banned.");
        if (action == AdminPlayerControlAction.UnmutePlayer
            && before.MutedUntilUtc is null)
            return (null, "The player is not muted.");
        var after = action switch
        {
            AdminPlayerControlAction.TemporaryFreezePlayer => before with
            {
                AccountStatus = "Frozen",
                FrozenUntilUtc = expiresAtUtc
            },
            AdminPlayerControlAction.PermanentBanPlayer => before with
            {
                AccountStatus = "Banned",
                FrozenUntilUtc = null
            },
            AdminPlayerControlAction.LiftPlayerBan => before with
            {
                AccountStatus = "Active",
                FrozenUntilUtc = null
            },
            AdminPlayerControlAction.MutePlayer => before with
            {
                MutedUntilUtc = expiresAtUtc
            },
            AdminPlayerControlAction.UnmutePlayer => before with
            {
                MutedUntilUtc = null
            },
            AdminPlayerControlAction.MarkRiskAccount => before with
            {
                RiskLabels = before.RiskLabels
                    .Append(riskLabel!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                RiskLabelsExpireAtUtc = expiresAtUtc
            },
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
        return (after with
        {
            Version = before.Version + 1,
            UpdatedAtUtc = effectiveAtUtc
        }, null);
    }
}
