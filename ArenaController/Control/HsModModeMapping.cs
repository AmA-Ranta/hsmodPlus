namespace ArenaController;

/// <summary>
/// Converts the Chinese labels used by the controller into the enum values
/// persisted by the original HsMod configuration. Arena is virtual on the
/// 1004 build and therefore keeps a valid native Standard value.
/// </summary>
public static class HsModModeMapping
{
    // The Arena bridge exposes Arena in /api/status while the original
    // four-value HsMod config enum must still persist Standard underneath.
    // Runtime comparisons must not mistake this projection for a mode change.
    public static string ToRuntime(string? displayMode)
        => displayMode == "竞技场" ? "Arena" : ToNative(displayMode);

    public static string ToNative(string? displayMode)
    {
        return displayMode switch
        {
            "标准" => "Standard",
            "狂野" => "Wild",
            "休闲" => "Casual",
            "酒馆战棋" => "Battlegrounds",
            "竞技场" => "Standard",
            _ => string.IsNullOrWhiteSpace(displayMode) ? "Standard" : displayMode
        };
    }

    public static string ToDisplay(string? nativeMode)
    {
        return nativeMode switch
        {
            "Standard" => "标准",
            "Wild" => "狂野",
            "Casual" => "休闲",
            "Battlegrounds" => "酒馆战棋",
            "Arena" => "竞技场",
            _ => string.IsNullOrWhiteSpace(nativeMode) ? "标准" : nativeMode
        };
    }
}
