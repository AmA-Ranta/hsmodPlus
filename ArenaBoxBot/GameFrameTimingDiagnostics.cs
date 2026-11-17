using System;
using System.Diagnostics;

namespace ArenaBoxBot;

internal static class GameFrameTimingDiagnostics
{
    private static readonly FrameTimingWindow s_window = new FrameTimingWindow();

    internal static void ObserveFrame()
    {
        try
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (!s_window.Observe(now)) return;
            var scene = SceneMgr.Get();
            // One small aggregate every 30 seconds; no screenshot, full-object scan,
            // resource eviction, forced GC or execution-state change.
            ArenaBoxBotPlugin.LogSource?.LogInfo("Game frame timing diagnostic: scene=" + (scene?.GetMode().ToString() ?? "UNKNOWN") +
                "; windowSeconds=" + s_window.Seconds.ToString("0.0") +
                "; updateCount=" + s_window.Frames + "; maxGapMs=" + s_window.MaximumMs.ToString("0.0") +
                "; gapsOver250ms=" + s_window.SlowFrames + "; gapsOver1000ms=" + s_window.SevereFrames +
                "; managedMemoryMB=" + (GC.GetTotalMemory(false) / 1048576d).ToString("0.0") +
                "; modeConfigLoads/cacheHits=" + HsModNativeArena.ConfigReadDiagnostic +
                "; observationalMainLoopIntervals=true; cause=unverified");
        }
        catch { /* Observation must never interfere with game actions. */ }
    }
}
