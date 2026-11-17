namespace ArenaController;

/// <summary>Entry quotas finish their last admitted round before requesting the existing reward-preserving stop.</summary>
public static class ArenaRoundLimitStopPolicy
{
    public const int CriticalPollMilliseconds = 1000;
    public static bool ShouldRequestStop(bool quotaReached, bool selected, string statsKey, string runtimeKey,
        double ageSeconds, bool inGameplay, string state, bool active, bool rewardPending,
        bool purchasePending, string completedKey)
    {
        if (!quotaReached || !selected || inGameplay || purchasePending ||
            string.IsNullOrEmpty(statsKey) || statsKey != runtimeKey ||
            !double.IsFinite(ageSeconds) || ageSeconds < 0 || ageSeconds > 5) return false;
        if (state is "REWARDS" or "IN_REWARDS") return rewardPending;
        return state == "NO_RUN" && !active && !rewardPending && completedKey == statsKey;
    }
}
