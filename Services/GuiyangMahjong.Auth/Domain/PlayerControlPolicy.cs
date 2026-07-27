namespace GuiyangMahjong.Auth.Domain;

public static class PlayerControlPolicy
{
    public static PlayerControlState Empty =>
        new(
            0,
            "Active",
            null,
            null,
            [],
            null,
            DateTimeOffset.UnixEpoch);

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
