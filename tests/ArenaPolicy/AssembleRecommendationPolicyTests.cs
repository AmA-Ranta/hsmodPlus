internal static class AssembleRecommendationPolicyTests
{
    internal static void Run()
    {
        Check(!ArenaBoxBot.AssembleRecommendationPolicy.ShouldSubmit(false, "req", "req", 0, 3, 5f, false),
            "a non-assemble recommendation is never executed by the plugin");
        Check(!ArenaBoxBot.AssembleRecommendationPolicy.ShouldSubmit(true, "req", "old", 0, 3, 5f, false),
            "only the current recommendation request is executed");
        Check(!ArenaBoxBot.AssembleRecommendationPolicy.ShouldSubmit(true, "req", "req", 0, 3, 0.5f, false),
            "HsMod gets a grace window before the plugin steps in");
        Check(ArenaBoxBot.AssembleRecommendationPolicy.ShouldSubmit(true, "req", "req", 0, 3, 1.0f, false),
            "an unexecuted assemble recommendation is submitted after the grace window");
        Check(!ArenaBoxBot.AssembleRecommendationPolicy.ShouldSubmit(true, "req", "req", 3, 3, 5f, false),
            "an out-of-range choice position is refused");
        Check(!ArenaBoxBot.AssembleRecommendationPolicy.ShouldSubmit(true, "req", "req", -1, 3, 5f, false),
            "a missing choice position is refused");
        Check(!ArenaBoxBot.AssembleRecommendationPolicy.ShouldSubmit(true, "req", "req", 0, 3, 5f, true),
            "an already submitted choice is never resubmitted");
        Check(!ArenaBoxBot.AssembleRecommendationPolicy.ShouldSubmit(true, "", "", 0, 3, 5f, false),
            "a recommendation without a request id is refused");
        System.Console.WriteLine("PASS: assemble recommendation policy (8 assertions)");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new System.InvalidOperationException("FAIL: " + description);
    }
}