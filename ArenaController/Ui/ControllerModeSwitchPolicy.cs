namespace ArenaController;

/// <summary>
/// Pure decisions behind "switch mode while a task is running". The window only
/// wires UI events to these answers, which keeps the contract small enough to
/// reason about and to unit test without a game or a desktop session.
/// </summary>
public static class ControllerModeSwitchPolicy
{
    /// <summary>The stop reason written by the queued switch. It lets the stop
    /// path distinguish its own deferred stop from an explicit user stop.</summary>
    public const string QueueStopReasonPrefix = "模式切换";

    /// <summary>Running tasks accept a second start request as a mode switch;
    /// a start that is already in flight must not be disturbed.</summary>
    public static bool ShouldQueueStart(bool running, bool starting) => running && !starting;

    /// <summary>A queued target equal to the running mode is a no-op: the
    /// client would be restarted for nothing.</summary>
    public static bool IsSameMode(string? targetMode, string? activeMode)
        => !string.IsNullOrEmpty(activeMode) &&
           string.Equals(targetMode, activeMode, StringComparison.Ordinal);

    /// <summary>When the user keeps the current mode, a pending deferred stop
    /// must be withdrawn so the task keeps playing.</summary>
    public static bool ShouldCancelPendingStop(bool sameMode, bool stopPending)
        => sameMode && stopPending;

    /// <summary>A switch released by the stop contract at a proven post-game
    /// boundary must restart immediately; waiting again would leave the script
    /// off while HsMod queues the next match.</summary>
    public static bool ShouldSkipSecondMatchWait(bool boundaryConfirmed) => boundaryConfirmed;

    /// <summary>Stop reasons that come from the switch itself never cancel the
    /// queue; only an explicit user stop does.</summary>
    public static bool IsSwitchDrivenStop(string? reason)
        => (reason ?? "").StartsWith(QueueStopReasonPrefix, StringComparison.Ordinal);

    /// <summary>HsMod activity text that proves the client is in a menu or
    /// collection screen rather than a live match.</summary>
    public static bool ActivityLooksLikeLobby(string? activity)
    {
        var text = activity ?? "";
        // Observed lobby texts: 等待收藏加载 / 正在选择卡组 / 正在进入天梯 / 酒馆匹配中 …
        foreach (var keyword in new[] { "收藏", "菜单", "主界面", "等待进入", "已开启", "已关闭", "卡组", "选择", "匹配", "正在进入" })
            if (text.Contains(keyword, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Human readable target used in logs and the status line.</summary>
    public static string DescribeTarget(string? mode, string? arenaMode)
    {
        var normalized = mode ?? "";
        return string.Equals(normalized, "竞技场", StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(arenaMode)
            ? normalized + "/" + arenaMode
            : normalized;
    }
}
