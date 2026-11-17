using ArenaBoxBot;

internal static class DisabledRuntimeTests
{
    public static void Run()
    {
        // The same controller stop request must remain pending when the
        // runtime is disabled mid-match, including with a changed mode.
        foreach (var selected in new[] { true, false })
        {
            Check("GAMEPLAY", "INVALID", "MIDRUN", selected, true, "GAMEPLAY", false);
            Check("GAMEPLAY", "INVALID", "UNKNOWN", selected, true, "GAMEPLAY", false);
            Check("HUB", "GAMEPLAY", "NO_RUN", selected, true, "GAMEPLAY", false);
            Check("GAMEPLAY", "DRAFT", "REWARDS", selected, true, "GAMEPLAY", false);
            Check("DRAFT", "INVALID", "REWARDS", selected, false, "REWARDS", false);
            Check("HUB", "INVALID", "IN_REWARDS", selected, false, "IN_REWARDS", false);
            Check("HUB", "INVALID", "NO_RUN", selected, false, "STOPPED", true);
        }
        Check("DRAFT", "INVALID", "MIDRUN", true, false, "MIDRUN", true);
        Check("DRAFT", "INVALID", "REDRAFTING", true, false, "REDRAFTING", true);
        Check("DRAFT", "HUB", "NO_RUN", true, false, "UNKNOWN", false);
        Check("TOURNAMENT", "INVALID", "NO_RUN", true, false, "STOPPED", true);
        Check("HUB", "INVALID", "UNKNOWN", true, false, "UNKNOWN", false);
        Check("HUB", "UNKNOWN", "NO_RUN", true, false, "UNKNOWN", false);
        Check("LOGIN", "INVALID", "MIDRUN", true, false, "UNKNOWN", false);
        Check("UNKNOWN", "UNKNOWN", "NO_RUN", true, false, "UNKNOWN", false);
        Check("DRAFT", "INVALID", "UNKNOWN", true, false, "UNKNOWN", false);
        Check("HUB", "INVALID", "INVALID", true, false, "UNKNOWN", false);
        Console.WriteLine("PASS: disabled runtime preserves active games and rewards; unknown native states cannot authorize shutdown");
    }

    private static void Check(string scene, string nextScene, string state, bool selected,
        bool inGameplay, string expectedState, bool safe)
    {
        var observation = ArenaDisabledRuntimePolicy.Observe(scene, nextScene, state);
        if (observation.Scene != scene || observation.InGameplay != inGameplay || observation.ArenaState != expectedState)
            throw new InvalidOperationException("Disabled snapshot lost native state: " + scene + "/" + nextScene + "/" + state);
        var observedSafe = ArenaController.ArenaStopBoundary.IsKnownSafe(true, selected, false,
            observation.InGameplay, observation.Scene, observation.ArenaState);
        if (observedSafe != safe || ArenaController.ArenaStopBoundary.CompleteFromEvidence(true, observedSafe, true) != safe)
            throw new InvalidOperationException("Disabled snapshot allowed the wrong stop boundary: " + scene + "/" + state);
    }
}
