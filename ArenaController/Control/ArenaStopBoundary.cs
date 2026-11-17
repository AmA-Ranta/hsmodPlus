namespace ArenaController;

/// <summary>
/// Conservative stop-boundary classification used by the external controller.
/// A missing or stale runtime snapshot is deliberately not treated as safe
/// while Hearthstone is still running.
/// </summary>
public static class ArenaStopBoundary
{
    public static bool CompleteFromEvidence(bool hearthstoneRunning, bool? currentSnapshotSafe, bool currentRequestLogConfirmed)
        => !hearthstoneRunning || (currentSnapshotSafe ?? currentRequestLogConfirmed);

    public static bool IsKnownSafe(
        bool hearthstoneRunning,
        bool arenaSelected,
        bool runtimeEnabled,
        bool inGameplay,
        string? scene,
        string? arenaState)
    {
        if (!hearthstoneRunning) return true;
        if (inGameplay || string.Equals(scene?.Trim(), "GAMEPLAY", StringComparison.OrdinalIgnoreCase)) return false;

        var normalizedState = arenaState?.Trim() ?? "";
        if (normalizedState is "NO_RUN" or "STOPPED" or "DRAFTING" or "MIDRUN" or
            "MIDRUN_REDRAFT_PENDING" or "REDRAFTING" or "EDITING_DECK")
            return true;

        // A fresh disabled snapshot is safe when the native runtime is off
        // and no gameplay is active.  HsMod keeps arenaSelected=true while a
        // user-selected Arena mode is merely disabled, so requiring
        // !arenaSelected would strand the controller in "waiting" forever
        // after a safe script stop.
        return string.Equals(scene?.Trim(), "DISABLED", StringComparison.OrdinalIgnoreCase) &&
               !runtimeEnabled;
    }
}
