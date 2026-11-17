using System;

namespace ArenaBoxBot;

/// <summary>
/// Resolves reward evidence only for an empty-session proof. Callers must first verify
/// a complete, mode-matching native session, and retain their active/chest/stability gates.
/// The current native client consumes HasState/State and does not consume IsInRewards.
/// This policy does not define active-run, entry-receipt, attachment or gameplay behavior.
/// </summary>
internal static class ArenaSessionStatePolicy
{
    internal static bool BlocksEmptySessionFromRewards(string managerState, string sessionState, bool legacyInRewards)
    {
        // A retained legacy bit cannot override two explicit canonical NO_RUN states.
        // Any other pair, including unknown or conflicting states, is not empty proof.
        return !string.Equals(managerState, "NO_RUN", StringComparison.Ordinal) ||
            !string.Equals(sessionState, "NO_RUN", StringComparison.Ordinal);
    }
}
