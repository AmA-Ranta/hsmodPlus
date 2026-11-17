using System;

internal static class AssembleFallbackPolicyTests
{
    internal static void Run()
    {
        Check(!ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("GENERAL", false, 4f, false), "4s is inside HsMod normal execution window");
        Check(!ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("GENERAL", true, 4.9f, false), "just under the threshold still waits");
        Check(ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("GENERAL", true, 5f, false), "5s stranded assemble choice gets a random pick");
        Check(ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("GENERAL", true, 30f, false), "long-stalled assemble choice keeps needing fallback");
        Check(!ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("GENERAL", false, 30f, false), "a non-assemble GENERAL choice is never auto-picked");
        Check(!ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("ADAPT", false, 30f, false), "a non-assemble ADAPT choice is never auto-picked");
        Check(!ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("GENERAL", true, 30f, true), "already submitted choice is never resubmitted");
        Check(!ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("MULLIGAN", true, 30f, false), "mulligan keeps its native flow");
        Check(!ArenaBoxBot.AssembleFallbackPolicy.ShouldSubmit("GENERAL", true, 0f, false), "fresh choice is left to HsMod");
        Console.WriteLine("PASS: assemble fallback policy (9 assertions)");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
