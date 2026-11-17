using ArenaBoxBot;

internal static class BgPositionProgressTests
{
    internal static void Run()
    {
        var gate = new BgPositionProgress();
        const string board = "1:1,2:2,3:3,4:4,5:5,14516:6,7:7";
        Require(gate.Observe("1152", 23, board, 0, out _) == BgPositionProgress.Decision.Allow, "first native move remains available");
        gate.Sent("1152", 23, board, 14516, 6, 2, 0);
        // Captured options kept incrementing (145..179) and stations were
        // republished while the accepted position2 was not processed for17s.
        var attempts = 1;
        for (var optionsId = 146; optionsId <= 179; ++optionsId)
        {
            var time = (optionsId - 145) * .55;
            if (gate.Observe("1152", 23, board, time, out _) == BgPositionProgress.Decision.Allow)
            {
                gate.Sent("1152", 23, board, 14516, 6, 2, time);
                ++attempts;
            }
        }
        Require(attempts == 2, "new options/publications cannot repeatedly reset an unchanged board's retry budget");
        Require(gate.Observe("1152", 23, board, 60, out _) == BgPositionProgress.Decision.RetryLimit, "elapsed time alone cannot invent board progress");
        Require(gate.Observe("1152", 23, "1:1,14516:2,2:3,3:4,4:5,5:6,7:7", 61, out _) == BgPositionProgress.Decision.Allow, "processed board movement releases the next native position pass");
        gate.Sent("1152", 23, board, 14516, 6, 2, 62);
        Require(gate.Observe("1152", 25, board, 63, out _) == BgPositionProgress.Decision.Allow, "next recruit round gets independent progress tracking");
        gate.Sent("1152", 25, board, 14516, 6, 2, 64);
        Require(gate.Observe("new-game", 25, board, 65, out _) == BgPositionProgress.Decision.Allow, "new game cannot inherit old pending moves");
        Require(gate.Observe(null, 0, null, 66, out _) == BgPositionProgress.Decision.Allow, "unknown native state keeps original behavior");
        Console.WriteLine("PASS: captured BG unchanged-board retries, actual movement acknowledgement, round/game isolation");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception("BG position progress: " + reason);
    }
}
