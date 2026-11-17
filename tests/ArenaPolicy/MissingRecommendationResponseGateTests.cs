using ArenaBoxBot;

internal static class MissingRecommendationResponseGateTests
{
    internal static void Run()
    {
        var gate = new MissingRecommendationResponseGate();
        Check(!gate.Observe("game1:turn3:packet7", 100f, true, out _), "start waiting for missing recommendation");
        Check(!gate.Observe("game1:turn3:packet7", 100.2f, true, out _), "preserve short acknowledgement window");
        Check(gate.Observe("game1:turn3:packet7", 100.3f, true, out var notice) && notice, "resume native polling at three hundred milliseconds");
        Check(gate.Observe("game1:turn3:packet7", 101.3f, true, out notice) && !notice, "same empty packet keeps polling without log spam");
        Check(!gate.Observe("game1:turn3:packet8", 104f, true, out _), "new packet gets a fresh observation");
        Check(!gate.Observe("game1:turn3:packet8", 107f, false, out _), "opponent, mulligan, game over, or unknown strategy never releases");
        Check(!gate.Observe("game1:turn3:packet8", 108f, true, out _), "inactivity clears old authority");
        gate.Reset(); // A recommendation became available; existing action/ACK gate resumes.
        Check(!gate.Observe("game1:turn3:packet8", 111f, true, out _), "available recommendation reset is respected");
        Check(!gate.Observe("game2:turn3:packet8", 115f, true, out _), "reused packet in another game cannot inherit wait");
        Check(!gate.Observe("game2:turn3:packet8", 114f, true, out _), "backwards clock restarts bounded observation");
        Check(!gate.Observe("game2:turn3:packet8", 130f, true, out _), "long lost observation cannot release on return");
        Check(!gate.Observe("", 133f, true, out _), "unknown game identity cannot release");
        Check(!gate.Observe("game2:turn3:packet8", float.NaN, true, out _), "nonfinite clock never releases");
        Console.WriteLine("PASS: empty recommendation gate bounded at300ms, progress/game/packet/turn authority resets, no action dispatch");
    }
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
}
