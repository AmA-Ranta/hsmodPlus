namespace ArenaController;

/// <summary>
/// Decides what to do when the native script reports "off" while the controller
/// still owns a task.  A freshly (re)started task legitimately sees the native
/// toggle off for a short while during client/plugin startup, so an immediate
/// detach would strand a running client with a "stopped" controller.
/// </summary>
public enum NativeStopAction
{
    /// <summary>Nothing to do: the native script is still on.</summary>
    Ignore,
    /// <summary>Script is off, but not long enough to act on yet.</summary>
    Wait,
    /// <summary>Script went off right after we enabled it: turn it back on.</summary>
    Reenable,
    /// <summary>Task quota was reached and the native script stopped itself.</summary>
    CompleteQuota,
    /// <summary>Genuine external stop: detach the controller.</summary>
    Detach
}

public static class ConstructedStopPolicy
{
    public static NativeStopAction Decide(bool apiReportsOff, double offSeconds,
        double secondsSinceScriptEnabled, bool quotaReached)
    {
        if (!apiReportsOff) return NativeStopAction.Ignore;
        // Two consecutive fresh reads plus 20 seconds of evidence keep F12 /
        // transient IO failures from being mistaken for a stop.
        if (offSeconds < 20) return NativeStopAction.Wait;
        if (quotaReached) return NativeStopAction.CompleteQuota;
        // The plugin/HsMod toggles the script off briefly while a restarted
        // client or watchdog is still initialising.  Re-enable instead of
        // abandoning the task; a real user stop stays off after this retry.
        if (secondsSinceScriptEnabled < 120) return NativeStopAction.Reenable;
        return NativeStopAction.Detach;
    }
}
