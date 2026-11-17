namespace ArenaController;

/// <summary>
/// Decides whether the client is really in a live match.  The controller used to
/// mix three signals inline (HsMod activity text, the plugin runtime snapshot and
/// a log-marker probe), and a stale log marker stalled mode switches for up to 45
/// minutes.  Keeping the rules here makes each signal's trust level explicit and
/// testable.
/// </summary>
public static class LiveMatchEvidence
{
    /// <summary>How long a plugin runtime snapshot stays trustworthy.</summary>
    public const int RuntimeSnapshotMaxAgeMs = 30000;

    /// <summary>Activity phrases that only appear while a match is being played.</summary>
    private static readonly string[] GameplayKeywords =
    {
        "回合", "战斗", "对局中", "等待盒子", "选牌", "选项", "建议"
    };

    /// <summary>Activity phrases that prove the client is not in a match.</summary>
    private static readonly string[] NotInMatchKeywords = { "匹配中", "结算" };

    public static bool ActivityLooksLikeGameplay(string? activity)
    {
        if (string.IsNullOrWhiteSpace(activity)) return false;
        foreach (var keyword in NotInMatchKeywords)
            if (activity.Contains(keyword, StringComparison.Ordinal)) return false;
        foreach (var keyword in GameplayKeywords)
            if (activity.Contains(keyword, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>A snapshot is usable only when it was written recently; clock skew
    /// into the future is tolerated up to a few seconds.</summary>
    public static bool IsRuntimeSnapshotFresh(long updatedAtUnixMs, long nowUnixMs)
    {
        var age = nowUnixMs - updatedAtUnixMs;
        return age >= -5000 && age <= RuntimeSnapshotMaxAgeMs;
    }

    /// <summary>Same game process instance: a match started before a relaunch must
    /// never be attributed to the new client.</summary>
    public static bool IsSameGameInstance(
        bool beforeKnown, bool beforeRunning, int beforeProcessId, DateTime beforeStartedAtUtc,
        bool afterKnown, bool afterRunning, int afterProcessId, DateTime afterStartedAtUtc)
        => beforeKnown && afterKnown && beforeRunning && afterRunning &&
           beforeProcessId == afterProcessId && beforeStartedAtUtc == afterStartedAtUtc;
}
