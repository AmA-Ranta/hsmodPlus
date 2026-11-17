using System;

internal static class ControllerModeSwitchPolicyTests
{
    internal static void Run()
    {
        Check(ArenaController.ControllerModeSwitchPolicy.ShouldQueueStart(true, false),
            "a running task accepts start as a switch request");
        Check(!ArenaController.ControllerModeSwitchPolicy.ShouldQueueStart(true, true),
            "an in-flight start is never queued twice");
        Check(!ArenaController.ControllerModeSwitchPolicy.ShouldQueueStart(false, false),
            "an idle controller starts normally instead of queueing");

        Check(ArenaController.ControllerModeSwitchPolicy.IsSameMode("酒馆战棋", "酒馆战棋"),
            "same mode is a no-op");
        Check(!ArenaController.ControllerModeSwitchPolicy.IsSameMode("竞技场", "酒馆战棋"),
            "different mode queues a real switch");
        Check(!ArenaController.ControllerModeSwitchPolicy.IsSameMode("竞技场", ""),
            "unknown running mode always allows a switch");

        Check(ArenaController.ControllerModeSwitchPolicy.ShouldCancelPendingStop(true, true),
            "keeping the current mode withdraws a pending stop");
        Check(!ArenaController.ControllerModeSwitchPolicy.ShouldCancelPendingStop(false, true),
            "different mode must keep the pending stop");

        Check(ArenaController.ControllerModeSwitchPolicy.ShouldSkipSecondMatchWait(true),
            "boundary-confirmed switch restarts immediately");
        Check(!ArenaController.ControllerModeSwitchPolicy.ShouldSkipSecondMatchWait(false),
            "unknown boundary waits for the match to end");

        Check(ArenaController.ControllerModeSwitchPolicy.IsSwitchDrivenStop("模式切换排队：竞技场"),
            "switch stop reason is recognised");
        Check(!ArenaController.ControllerModeSwitchPolicy.IsSwitchDrivenStop("WPF 界面停止"),
            "explicit user stop is not a switch stop");

        Check(ArenaController.ControllerModeSwitchPolicy.DescribeTarget("竞技场", "普通竞技场") == "竞技场/普通竞技场",
            "arena label keeps its sub mode");
        Check(ArenaController.ControllerModeSwitchPolicy.ActivityLooksLikeLobby("等待收藏加载"),
            "collection loading is a lobby state, not a live match");
        Check(ArenaController.ControllerModeSwitchPolicy.ActivityLooksLikeLobby("正在选择卡组"),
            "deck selection is a lobby state");
        Check(ArenaController.ControllerModeSwitchPolicy.ActivityLooksLikeLobby("酒馆匹配中（12秒）"),
            "matchmaking is a lobby state");
        Check(!ArenaController.ControllerModeSwitchPolicy.ActivityLooksLikeLobby("第 5 回合 · 对手回合"),
            "turn activity is not treated as a lobby");
        Check(ArenaController.ControllerModeSwitchPolicy.DescribeTarget("标准", "普通竞技场") == "标准",
            "non-arena label ignores the arena sub mode");
        Console.WriteLine("PASS: controller mode switch policy (18 assertions)");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
