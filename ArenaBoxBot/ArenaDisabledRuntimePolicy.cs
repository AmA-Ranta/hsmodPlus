using System;

namespace ArenaBoxBot;

/// <summary>Turning automation off does not end the native game or reward flow.</summary>
public static class ArenaDisabledRuntimePolicy
{
    public sealed class Observation
    {
        public string Scene = "UNKNOWN";
        public bool InGameplay;
        public string ArenaState = "UNKNOWN";
    }

    public static Observation Observe(string scene, string nextScene, string arenaState)
    {
        var current = Normalize(scene);
        var next = Normalize(nextScene);
        var native = Normalize(arenaState);
        var result = new Observation { Scene = current };
        if (current == "GAMEPLAY" || next == "GAMEPLAY")
        {
            // Covers all game types, including a live game whose configured
            // script mode was changed before F12 was switched off.
            result.InGameplay = true;
            result.ArenaState = "GAMEPLAY";
            return result;
        }
        if (native == "REWARDS" || native == "IN_REWARDS")
        {
            // Reward acknowledgement can lag behind a transition to HUB.
            result.ArenaState = native;
            return result;
        }
        if (next != "INVALID") return result;
        if (current == "DRAFT") result.ArenaState = native;
        else if ((current == "HUB" || current == "TOURNAMENT") &&
                 (native == "NO_RUN" || native == "MIDRUN" || native == "MIDRUN_REDRAFT_PENDING" ||
                  native == "DRAFTING" || native == "REDRAFTING" || native == "EDITING_DECK"))
            result.ArenaState = "STOPPED";
        return result;
    }

    private static string Normalize(string value)
        => string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim().ToUpperInvariant();
}
