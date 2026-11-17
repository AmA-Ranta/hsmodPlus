using System;

namespace ArenaBoxBot;

/// <summary>
/// Decides whether a stalled friendly choice needs the plugin's own fallback
/// submission. Assemble ("拼装") is a GENERAL choice; when the box AI publishes
/// no recommendation HsMod never submits and the server closes the choice on
/// timeout, losing the assembled result. The fallback only applies to GENERAL
/// choices that stayed open well past HsMod's normal execution window and that
/// this plugin has not already handled.
/// </summary>
internal static class AssembleFallbackPolicy
{
    // The user asked for a much shorter guard: an assemble/拼装 choice that is
    // still unanswered after 8s is treated as stranded instead of waiting for the
    // server to close it around 24-27s. Box recommendations that arrive later than
    // 8s simply lose the race; a random candidate is better than a stalled match.
    internal const float FallbackDelaySeconds = 5f;

    // Any friendly choice that HsMod has not answered within this window gets a
    // random valid pick.  Gating on GENERAL alone left Assemble-style choices of
    // other types (and non-Arena modes) stalled until the server closed them.
    // The mulligan is the one exception: replacing cards is a real gameplay
    // decision, so a random mulligan would degrade play; HsMod owns that flow.
    internal static bool ShouldSubmit(string choiceType, bool assembleChoice, float ageSeconds, bool alreadySubmitted)
        => !alreadySubmitted && assembleChoice && ageSeconds >= FallbackDelaySeconds &&
           !string.Equals(choiceType, "MULLIGAN", StringComparison.Ordinal);
}
