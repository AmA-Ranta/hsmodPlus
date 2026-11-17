using System;
using ArenaController;

internal static class LiveMatchEvidenceTests
{
    internal static void Run()
    {
        Check(LiveMatchEvidence.ActivityLooksLikeGameplay("第 3 回合 · 对手回合"), "turn text is gameplay");
        Check(LiveMatchEvidence.ActivityLooksLikeGameplay("等待盒子建议（尚未收到或已过期）"), "waiting for a box hint is gameplay");
        Check(!LiveMatchEvidence.ActivityLooksLikeGameplay("匹配中（12 秒）"), "matchmaking is not gameplay");
        Check(!LiveMatchEvidence.ActivityLooksLikeGameplay("对局结算中"), "settlement is not gameplay");
        Check(!LiveMatchEvidence.ActivityLooksLikeGameplay(""), "empty activity is not gameplay");
        Check(!LiveMatchEvidence.ActivityLooksLikeGameplay(null), "null activity is not gameplay");

        long now = 1_000_000;
        Check(LiveMatchEvidence.IsRuntimeSnapshotFresh(now - 5_000, now), "a 5s old snapshot is fresh");
        Check(!LiveMatchEvidence.IsRuntimeSnapshotFresh(now - 30_001, now), "a 30s old snapshot is stale");
        Check(LiveMatchEvidence.IsRuntimeSnapshotFresh(now + 2_000, now), "small clock skew is tolerated");
        Check(!LiveMatchEvidence.IsRuntimeSnapshotFresh(now + 10_000, now), "a future timestamp is rejected");

        var started = new DateTime(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);
        Check(LiveMatchEvidence.IsSameGameInstance(true, true, 7, started, true, true, 7, started),
            "same process instance matches");
        Check(!LiveMatchEvidence.IsSameGameInstance(true, true, 7, started, true, true, 8, started),
            "different process id does not match");
        Check(!LiveMatchEvidence.IsSameGameInstance(true, true, 7, started, true, true, 7, started.AddSeconds(1)),
            "different start time does not match");
        Check(!LiveMatchEvidence.IsSameGameInstance(false, true, 7, started, true, true, 7, started),
            "unknown before-sample does not match");
        Console.WriteLine("PASS: live match evidence (14 assertions)");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
