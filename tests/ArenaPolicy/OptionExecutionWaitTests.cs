using ArenaBoxBot;

internal static class OptionExecutionWaitTests
{
    internal static void Run()
    {
        var trace = new OptionExecutionWait();
        Check(!trace.Observe("g1:t8:53", 0f, false, true), "first attempt starts timing");
        Check(!trace.Observe("g1:t8:53", 2.9f, false, true), "short preparation is not warned");
        Check(trace.Observe("g1:t8:53", 3f, false, true), "sustained unsuccessful same packet is diagnosed");
        Check(!trace.Observe("g1:t8:53", 4f, false, true), "not every tick");
        Check(!trace.Observe("g1:t8:53", 5f, true, true), "successful return clears timing");
        Check(!trace.Observe("g1:t8:53", 6f, false, true), "same packet starts fresh after success");
        Check(!trace.Observe("g2:t1:1", 20f, false, true), "new game does not inherit stalled time");
        Check(!trace.Observe("g2:t1:1", 30f, false, false), "inactive/opponent turn cancels observer");
        Check(!trace.Observe("g2:t1:1", 31f, false, true), "reenter active turn starts fresh");
        Console.WriteLine("PASS: unsuccessful option timing, progress and scene resets; observation only");
    }
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
}
