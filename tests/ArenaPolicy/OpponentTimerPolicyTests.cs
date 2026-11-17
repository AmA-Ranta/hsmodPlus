using ArenaBoxBot;

internal static class OpponentTimerPolicyTests
{
    internal static void Run()
    {
        // Replay the actual R24 packet47 diagnostics: own OPTION, old
        // opponent TIMEOUT/remaining0; timer later switched friendly.
        Check(float.IsNaN(OpponentTimerPolicy.Remaining(0, true, true, false, true, true)), "expired opponent timer is unknown for the authoritative own turn");
        Check(OpponentTimerPolicy.Remaining(0, true, true, true, true, true) == 0, "actual own timeout remains expired");
        Check(OpponentTimerPolicy.Remaining(0, false, true, false, true, true) == 0, "non-own, gameover, choice, missing packet, and mulligan remain restricted");
        Check(OpponentTimerPolicy.Remaining(0, true, false, false, true, true) == 0, "unknown owner does not relax expiry");
        Check(OpponentTimerPolicy.Remaining(0, true, true, false, false, true) == 0, "unknown countdown authority remains restricted");
        Check(OpponentTimerPolicy.Remaining(0, true, true, false, true, false) == 0, "non-timeout state is unchanged");
        Check(OpponentTimerPolicy.Remaining(17, true, true, false, true, true) == 17, "positive clock unchanged");
        Check(OpponentTimerPolicy.Remaining(-1, true, true, false, true, true) == -1, "negative clock unchanged");
        Check(float.IsNaN(OpponentTimerPolicy.Remaining(float.NaN, true, true, false, true, true)), "unknown clock stays unknown");
        Console.WriteLine("PASS: actual stale-opponent timeout replay; own expiry, unknown owner/clock/scene/packet, choice and inactive game retain native restriction");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
