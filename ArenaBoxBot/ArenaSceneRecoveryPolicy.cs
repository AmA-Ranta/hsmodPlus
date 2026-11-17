namespace ArenaBoxBot;

/// <summary>
/// The controller can only change HsMod's configured mode; the client keeps
/// whatever scene it was on. When the target is Arena but the client stays in a
/// foreign gameplay scene (for example the Battlegrounds result screen), the
/// Arena controller has nothing to drive and waits forever. This policy decides
/// when it is safe to ask the client to return to the main menu so the normal
/// Arena navigation can take over.
///
/// It is deliberately conservative: a live match must never be abandoned, so a
/// return is only allowed when the game manager reports no game in progress and
/// the scene has been stable for a while.
/// </summary>
public static class ArenaSceneRecoveryPolicy
{
    /// <summary>How long the foreign scene must stay unchanged before we act.</summary>
    public const float ForeignSceneGraceSeconds = 20f;

    public static bool ShouldLeaveForeignGameplay(
        string scene,
        string nextScene,
        bool arenaGameplay,
        bool gameInProgress,
        bool stopRequested,
        float stableSeconds)
        => !stopRequested &&
           string.Equals(scene, "GAMEPLAY", System.StringComparison.Ordinal) &&
           string.Equals(nextScene, "INVALID", System.StringComparison.Ordinal) &&
           !arenaGameplay &&
           !gameInProgress &&
           stableSeconds >= ForeignSceneGraceSeconds;

    /// <summary>
    /// A foreign lobby (for example the constructed tournament screen) also
    /// blocks Arena navigation; it has no match attached so it is always safe.
    /// </summary>
    public static bool ShouldLeaveForeignLobby(
        string scene,
        string nextScene,
        bool stopRequested,
        float stableSeconds)
        => !stopRequested &&
           (string.Equals(scene, "TOURNAMENT", System.StringComparison.Ordinal) ||
            string.Equals(scene, "ADVENTURE", System.StringComparison.Ordinal) ||
            string.Equals(scene, "TAVERN_BRAWL", System.StringComparison.Ordinal)) &&
           string.Equals(nextScene, "INVALID", System.StringComparison.Ordinal) &&
           stableSeconds >= ForeignSceneGraceSeconds;
}
