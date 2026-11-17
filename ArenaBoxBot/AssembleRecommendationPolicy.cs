using System;

namespace ArenaBoxBot;

/// <summary>
/// Decides whether the plugin should submit the box's Assemble ("拼装")
/// recommendation itself.  HsMod's executor does not apply that action, so the
/// choice stays on screen until the server closes it and the player has to pick
/// manually.  The plugin only takes over a well-formed recommendation that is
/// still the current one and has been visible long enough for HsMod to have
/// acted on it.
/// </summary>
internal static class AssembleRecommendationPolicy
{
    internal const float GraceSeconds = 1.0f;

    internal static bool ShouldSubmit(bool isAssemble, string requestId, string lastRequestId,
        int position, int entityCount, float ageSeconds, bool alreadySubmitted)
    {
        if (!isAssemble || alreadySubmitted) return false;
        if (string.IsNullOrEmpty(requestId)) return false;
        if (!string.Equals(requestId, lastRequestId, StringComparison.Ordinal)) return false;
        if (position < 0 || entityCount <= 0 || position >= entityCount) return false;
        return ageSeconds >= GraceSeconds;
    }
}