namespace ArenaBoxBot;

internal enum ArenaModeNavigationAction { Wait, ToggleNative, RecreatePage, Ready }

internal static class ArenaModeNavigationPolicy
{
    internal static ArenaModeNavigationAction Decide(bool target, bool managerMode, bool viewMode,
        bool sessionBound, bool clientModeMatches, bool onLanding, bool visualStable)
    {
        if (!visualStable) return ArenaModeNavigationAction.Wait;
        // A loaded page built for another mode must never be accepted just
        // because SetUndergroundState changed the manager's data flag.
        if (managerMode != viewMode) return ArenaModeNavigationAction.RecreatePage;
        if (managerMode != target)
            return onLanding ? ArenaModeNavigationAction.ToggleNative : ArenaModeNavigationAction.RecreatePage;
        return sessionBound && clientModeMatches ? ArenaModeNavigationAction.Ready : ArenaModeNavigationAction.Wait;
    }

    internal static bool CanCompleteStop(bool stopRequested, string scene, string nextScene,
        bool findingGame, bool connectedToGame, bool rewardPending, bool safeArenaBoundary)
        => stopRequested && (scene == "HUB" || scene == "DRAFT") && (scene == nextScene || nextScene == "INVALID") &&
           !findingGame && !connectedToGame && !rewardPending && safeArenaBoundary;
}
