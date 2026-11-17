namespace ArenaBoxBot;

/// <summary>
/// Single source of truth for the five Hearthstone modes the plugin drives:
/// Standard, Wild, Casual, Battlegrounds and Arena.  HsMod's native settings
/// only enumerate the first four, so Arena is carried by the plugin's own
/// bridge; keeping the list, the display text and the native mapping in one
/// place guarantees that the F11 cycle, the settings page and the status feed
/// always agree, and lets the controller stay a pure monitor/control shell.
/// </summary>
internal static class ArenaModeCatalog
{
    internal const string PluginVersion = "2.0.0";

    /// <summary>Display order used by the F11 mode cycle and the status feed.</summary>
    internal static readonly string[] All = { "标准", "狂野", "休闲", "酒馆战棋", "竞技场" };

    internal static bool IsKnown(string mode)
    {
        if (string.IsNullOrEmpty(mode)) return false;
        foreach (var candidate in All)
            if (string.Equals(candidate, mode, System.StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Native HsMod config value for a display mode.</summary>
    internal static string ToNative(string mode)
    {
        switch (mode)
        {
            case "标准": return "Standard";
            case "狂野": return "Wild";
            case "休闲": return "Casual";
            case "酒馆战棋": return "Battlegrounds";
            // Supported HsMod builds do not have an Arena enum value; the
            // persisted native config always stays on a legal value while the
            // plugin carries Arena through its own bridge file.
            case "竞技场": return "Standard";
            default: return mode ?? "Standard";
        }
    }

    /// <summary>Display mode for a native HsMod value.</summary>
    internal static string ToDisplay(string native)
    {
        switch (native)
        {
            case "Standard": return "标准";
            case "Wild": return "狂野";
            case "Casual": return "休闲";
            case "Battlegrounds": return "酒馆战棋";
            case "Arena": return "竞技场";
            default: return string.IsNullOrEmpty(native) ? "标准" : native;
        }
    }

    /// <summary>Stable human-readable list published to the controller.</summary>
    internal static string Summary => string.Join("/", All);

    /// <summary>Next mode in the five-mode cycle (unknown input starts at 标准).</summary>
    internal static string Next(string mode)
    {
        var index = System.Array.IndexOf(All, mode);
        if (index < 0) index = 0;
        return All[(index + 1) % All.Length];
    }
}
