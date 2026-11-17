namespace ArenaController;

/// <summary>
/// Classifies the HsMod activity text used by the controller before stopping
/// a constructed-mode task. Unknown or empty activity is intentionally unsafe.
/// </summary>
public static class ConstructedStopBoundary
{
    public static bool CounterConfirmsCompletion(bool gameplayKnown, bool gameplayActive, int? baseline, int? current)
        => gameplayKnown && !gameplayActive && baseline.HasValue && current.HasValue && current.Value > baseline.Value;

    public static bool IsGameplayActivity(string? activity)
    {
        var normalized = activity?.Trim().ToLowerInvariant() ?? "";
        return normalized.Contains("gameplay", StringComparison.Ordinal) ||
               normalized.Contains("match", StringComparison.Ordinal) ||
               normalized.Contains("battle", StringComparison.Ordinal) ||
               normalized.Contains("对局", StringComparison.Ordinal) ||
               normalized.Contains("战斗", StringComparison.Ordinal);
    }

    public static bool IsKnownSafe(string? activity)
    {
        var normalized = activity?.Trim().ToLowerInvariant() ?? "";
        if (normalized.Length == 0) return false;

        if (IsGameplayActivity(activity)) return false;

        return normalized.Contains("idle", StringComparison.Ordinal) ||
               normalized.Contains("lobby", StringComparison.Ordinal) ||
               normalized.Contains("hub", StringComparison.Ordinal) ||
               normalized.Contains("menu", StringComparison.Ordinal) ||
               normalized.Contains("大厅", StringComparison.Ordinal) ||
               normalized.Contains("菜单", StringComparison.Ordinal);
    }

    /// <summary>
    /// HsMod reports “等待盒子建议” while the native script is idle, but
    /// that label is intentionally not safe on its own because it can be a
    /// stale snapshot during an active match. Once the controller has freshly
    /// confirmed the script is disabled and no gameplay activity is present,
    /// the same snapshot is a safe configuration/restart boundary.
    /// </summary>
    public static bool IsKnownSafe(string? activity, bool scriptEnabled)
    {
        if (!scriptEnabled && !IsGameplayActivity(activity))
        {
            var normalized = activity?.Trim().ToLowerInvariant() ?? "";
            if (normalized.Contains("等待盒子建议", StringComparison.Ordinal) ||
                // Mode-specific idle labels ("竞技场脚本已关闭", "脚本未开启") are
                // likewise safe once the script is off and no gameplay text exists.
                normalized.Contains("脚本已关闭", StringComparison.Ordinal) ||
                normalized.Contains("脚本未开启", StringComparison.Ordinal) ||
                normalized.Contains("尚未执行动作", StringComparison.Ordinal))
                return true;
            // A stopped script can leave the client on the post-game settlement
            // screen ("正在确认结算"/"确认结算") indefinitely. That is already the
            // safe boundary: no further action can start while the script is off.
            if (normalized.Contains("结算", StringComparison.Ordinal) ||
                normalized.Contains("奖励", StringComparison.Ordinal) ||
                normalized.Contains("胜利", StringComparison.Ordinal) ||
                normalized.Contains("失败", StringComparison.Ordinal) ||
                normalized.Contains("结果", StringComparison.Ordinal) ||
                normalized.Contains("victory", StringComparison.Ordinal) ||
                normalized.Contains("defeat", StringComparison.Ordinal) ||
                normalized.Contains("reward", StringComparison.Ordinal) ||
                normalized.Contains("endgame", StringComparison.Ordinal) ||
                // Client loading/hub transitions are safe once the script is off.
                normalized.Contains("进入主页大厅", StringComparison.Ordinal) ||
                normalized.Contains("等待进入", StringComparison.Ordinal) ||
                normalized.Contains("加载", StringComparison.Ordinal))
                return true;
        }
        return IsKnownSafe(activity);
    }
}
