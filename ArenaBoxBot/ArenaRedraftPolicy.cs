namespace ArenaBoxBot;

/// <summary>
/// Guards underground redraft actions until the native slot metadata has
/// arrived.  The native default max value is zero; it is not an unlimited
/// redraft budget.  In the current client the slot is zero based and
/// GetMaxRedraftSlot() is inclusive: the fifth visual choice is reported as
/// slot 4/4.  Keeping the upper bound inclusive is required to submit the
/// final replacement card instead of waiting forever on 4/4.
/// </summary>
internal static class ArenaRedraftPolicy
{
    internal static bool IsSlotReady(int slot, int maxSlot) =>
        slot >= 0 && maxSlot > 0 && slot <= maxSlot;

    // Server choices may be cached while the native client is still on the
    // landing/deck page. Only the client page, not choices.Count, tells us
    // whether the native redraft navigation entry point is required.
    internal static bool ShouldOpenPage(string arenaState, string clientState) =>
        (arenaState == "MIDRUN_REDRAFT_PENDING" || arenaState == "REDRAFTING") &&
        (clientState == "Normal_Landing" || clientState == "Underground_Landing" ||
         clientState == "Normal_Ready" || clientState == "Underground_Ready");
}
