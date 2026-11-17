using System;

namespace ArenaBoxBot;

/// <summary>
/// Pure stop-boundary rules shared by the Arena controller and its tests.
/// Keeping this decision free of Unity objects makes the most important safety
/// invariant executable without requiring a live Hearthstone process.
/// </summary>
public static class ArenaStopPolicy
{
    public static bool ShouldRequestGameLimitStop(bool runtimeEnabled, string scene,
        int gamesThisRun, int limit)
        => runtimeEnabled && limit > 0 && gamesThisRun >= limit &&
           (string.Equals(scene, "DRAFT", StringComparison.Ordinal) ||
            string.Equals(scene, "HUB", StringComparison.Ordinal));

    public static bool BlocksFurtherActions(bool stopRequested, bool rewardState)
        => stopRequested && !rewardState;

    public static bool CanConsumeControllerMarker(string scene, string nextScene,
        bool transitioning, bool nativePageBound, string arenaState, bool nativeRunActive)
        => string.Equals(scene, "DRAFT", StringComparison.Ordinal) &&
           string.Equals(nextScene, "INVALID", StringComparison.Ordinal) &&
           !transitioning && nativePageBound && IsSafeBoundary(arenaState, nativeRunActive);

    public static bool IsSafeBoundary(string arenaState, bool nativeRunActive)
    {
        if (string.Equals(arenaState, "NO_RUN", StringComparison.Ordinal)) return true;
        if (string.Equals(arenaState, "INVALID", StringComparison.Ordinal) && !nativeRunActive) return true;
        // A redraft or completed deck is outside the just-finished gameplay
        // session. It is safe to leave that page visible when a controller
        // stop arrives; starting another match is the action the stop must
        // prevent. DRAFTING is likewise safe because no match has started.
        if (string.Equals(arenaState, "MIDRUN", StringComparison.Ordinal) ||
            string.Equals(arenaState, "MIDRUN_REDRAFT_PENDING", StringComparison.Ordinal) ||
            string.Equals(arenaState, "REDRAFTING", StringComparison.Ordinal) ||
            string.Equals(arenaState, "EDITING_DECK", StringComparison.Ordinal) ||
            string.Equals(arenaState, "DRAFTING", StringComparison.Ordinal)) return true;
        return false;
    }
}
