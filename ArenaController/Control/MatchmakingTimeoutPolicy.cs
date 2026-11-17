namespace ArenaController;

/// <summary>
/// Keeps matchmaking recovery bounded. A single long queue can be a normal
/// server-side delay, so recovery requires two explicit native timeout events
/// within a short window and is never allowed while gameplay is active.
/// </summary>
public static class MatchmakingTimeoutPolicy
{
    public static bool IsConstructedTimeout(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return (line.Contains("匹配已持续", StringComparison.Ordinal) &&
                line.Contains("判定为匹配卡死", StringComparison.Ordinal)) ||
               line.Contains("constructed matchmaking handshake timed out", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("matchmaking handshake timed out", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldRecover(IEnumerable<DateTime> timeoutTimes, DateTime now,
        DateTime lastRecoveryAt, bool scriptEnabled, bool gameplayActive)
    {
        if (!scriptEnabled || gameplayActive) return false;
        var recent = timeoutTimes.Count(t => now >= t && now - t <= TimeSpan.FromMinutes(10));
        if (recent < 2) return false;
        return lastRecoveryAt == DateTime.MinValue || now - lastRecoveryAt >= TimeSpan.FromMinutes(10);
    }
}
