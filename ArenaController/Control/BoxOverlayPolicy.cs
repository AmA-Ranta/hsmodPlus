namespace ArenaController;

/// <summary>
/// The Box embeds a full-screen Qt overlay ("Deck Info Embedded Window") above
/// the game.  It carries only WS_EX_LAYERED, not WS_EX_TRANSPARENT, so mouse
/// clicks on its painted panels are swallowed by the overlay instead of
/// reaching the game or the in-game plugin settings underneath.
/// </summary>
public static class BoxOverlayPolicy
{
    public const long ExStyleTransparent = 0x00000020;
    public const long ExStyleNoActivate = 0x08000000;

    public static bool ShouldMakeClickThrough(string? className, string? title, long exStyle)
    {
        if (!LooksLikeOverlay(className, title)) return false;
        // Already click-through (or a window that cannot be layered safely) is
        // left untouched so repeated passes are idempotent.
        return (exStyle & ExStyleTransparent) == 0;
    }

    public static bool LooksLikeOverlay(string? className, string? title)
    {
        if (!string.Equals(className, "Qt5QWindowIcon", StringComparison.Ordinal)) return false;
        var text = (title ?? "").ToLowerInvariant();
        return text.Contains("deck info") || text.Contains("embedded") || text.Contains("overlay");
    }

    public static long ClickThroughStyle(long exStyle)
        => exStyle | ExStyleTransparent | ExStyleNoActivate;
}
