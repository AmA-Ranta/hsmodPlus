using System;

internal static class ArenaSceneRecoveryPolicyTests
{
    internal static void Run()
    {
        // Finished foreign match: safe to go back to the Hub.
        Check(ArenaBoxBot.ArenaSceneRecoveryPolicy.ShouldLeaveForeignGameplay(
                "GAMEPLAY", "INVALID", false, false, false, 25f),
            "finished foreign gameplay after the grace period returns to Hub");
        // A live match must never be abandoned.
        Check(!ArenaBoxBot.ArenaSceneRecoveryPolicy.ShouldLeaveForeignGameplay(
                "GAMEPLAY", "INVALID", false, true, false, 300f),
            "running match is never abandoned");
        // Arena's own gameplay is not foreign.
        Check(!ArenaBoxBot.ArenaSceneRecoveryPolicy.ShouldLeaveForeignGameplay(
                "GAMEPLAY", "INVALID", true, false, false, 300f),
            "arena gameplay is not a foreign scene");
        // Stop request wins: the stop contract owns the boundary.
        Check(!ArenaBoxBot.ArenaSceneRecoveryPolicy.ShouldLeaveForeignGameplay(
                "GAMEPLAY", "INVALID", false, false, true, 300f),
            "stop request suppresses scene recovery");
        // Not stable long enough yet.
        Check(!ArenaBoxBot.ArenaSceneRecoveryPolicy.ShouldLeaveForeignGameplay(
                "GAMEPLAY", "INVALID", false, false, false, 5f),
            "grace period avoids reacting to scene transitions");
        // A pending next mode means a transition is already under way.
        Check(!ArenaBoxBot.ArenaSceneRecoveryPolicy.ShouldLeaveForeignGameplay(
                "GAMEPLAY", "HUB", false, false, false, 300f),
            "pending transition suppresses duplicate navigation");
        // Foreign lobbies have no match attached.
        Check(ArenaBoxBot.ArenaSceneRecoveryPolicy.ShouldLeaveForeignLobby("TOURNAMENT", "INVALID", false, 25f),
            "foreign tournament lobby returns to Hub");
        Check(!ArenaBoxBot.ArenaSceneRecoveryPolicy.ShouldLeaveForeignLobby("HUB", "INVALID", false, 25f),
            "the Hub itself is never treated as foreign");
        Console.WriteLine("PASS: arena scene recovery policy (8 assertions)");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
