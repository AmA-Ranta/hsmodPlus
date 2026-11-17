using ArenaController;
using ArenaControl.Contracts;

internal static class Program
{
    private static int _checks;

    private static void Check(bool value, string reason)
    {
        _checks++;
        if (!value) throw new Exception(reason);
    }

    private static void Main()
    {
        // The native script is still on: never act.
        Check(ConstructedStopPolicy.Decide(false, 300, 0, false) == NativeStopAction.Ignore,
            "an enabled native script is never treated as a stop");
        Check(ConstructedStopPolicy.Decide(false, 300, 0, true) == NativeStopAction.Ignore,
            "quota completion still requires an actual native stop");

        // Off evidence must be sustained; short blips are ignored.
        Check(ConstructedStopPolicy.Decide(true, 5, 600, false) == NativeStopAction.Wait,
            "five seconds of off evidence only waits");
        Check(ConstructedStopPolicy.Decide(true, 19, 600, false) == NativeStopAction.Wait,
            "sub-20-second off evidence only waits");
        Check(ConstructedStopPolicy.Decide(true, 10, 600, true) == NativeStopAction.Wait,
            "quota completion also waits for sustained evidence");

        // Right after we enabled the script, an off observation is a transient
        // plugin/client startup state, not a user stop: re-enable instead.
        Check(ConstructedStopPolicy.Decide(true, 25, 5, false) == NativeStopAction.Reenable,
            "native off seconds after enabling is re-enabled");
        Check(ConstructedStopPolicy.Decide(true, 25, 119, false) == NativeStopAction.Reenable,
            "the full two-minute startup window re-enables");
        Check(ConstructedStopPolicy.Decide(true, 25, 121, false) == NativeStopAction.Detach,
            "outside the startup window an external stop detaches");
        Check(ConstructedStopPolicy.Decide(true, 25, double.MaxValue, false) == NativeStopAction.Detach,
            "an unknown enable time falls back to detach");

        // Quota completion wins over the startup window once it is reached.
        Check(ConstructedStopPolicy.Decide(true, 30, 100, true) == NativeStopAction.CompleteQuota,
            "reaching the task quota completes the task");
        Check(ConstructedStopPolicy.Decide(true, 30, 600, true) == NativeStopAction.CompleteQuota,
            "quota completion does not depend on the startup window");

        // Settlement/reward screens are safe once the script is off; the old
        // classifier stalled the stop flow there forever.
        Check(ConstructedStopBoundary.IsKnownSafe("正在确认结算", false),
            "post-game settlement with a stopped script is a safe boundary");
        Check(ConstructedStopBoundary.IsKnownSafe("确认结算", false),
            "settlement confirmation is safe when the script is off");
        Check(!ConstructedStopBoundary.IsKnownSafe("正在确认结算", true),
            "settlement text must not be trusted while the script is still on");
        Check(!ConstructedStopBoundary.IsKnownSafe("匹配中（0秒）", false),
            "matchmaking stays unsafe even with a stopped script");
        Check(!ConstructedStopBoundary.IsKnownSafe("等待动画落定后映射", false),
            "in-match mapping still counts as gameplay");
        Check(ConstructedStopBoundary.IsKnownSafe("等待收藏加载", false),
            "collection loading is safe when the script is off");
        Check(ConstructedStopBoundary.IsKnownSafe("等待进入主页大厅", false),
            "a loading/hub transition is safe when the script is off");
        Check(ConstructedStopBoundary.IsKnownSafe("等待盒子建议", false),
            "existing idle label remains safe with a stopped script");
        Check(!ConstructedStopBoundary.IsKnownSafe("等待盒子建议", true),
            "idle label is unsafe while the script may still act");

        // Box overlay click-through predicate (regression for blocked clicks).
        Check(BoxOverlayPolicy.ShouldMakeClickThrough("Qt5QWindowIcon", "Deck Info Embedded Window", 0x80000),
            "box embedded overlay becomes click-through");
        Check(!BoxOverlayPolicy.ShouldMakeClickThrough("Qt5QWindowIcon", "Deck Info Embedded Window", 0x80020),
            "already click-through overlay is left untouched");
        Check(!BoxOverlayPolicy.ShouldMakeClickThrough("UnityWndClass", "炉石传说", 0x80000),
            "the game window is never touched");
        Check(!BoxOverlayPolicy.ShouldMakeClickThrough("Qt5QWindowIcon", "MainWindow", 0x80000),
            "unrelated Box windows stay interactive");
        Check((BoxOverlayPolicy.ClickThroughStyle(0x80000) & 0x20) != 0,
            "click-through style sets WS_EX_TRANSPARENT");

        // Stop completion must not depend on a stale GAMEPLAY marker once the
        // fresh API reports the script off at a safe post-game boundary.
        Check(ConstructedStopContract.CanCompleteStop(true, true, true, true, true),
            "fresh script-off safe boundary completes a stop despite stale gameplay");
        Check(!ConstructedStopContract.CanCompleteStop(true, true, true, true, false),
            "active gameplay without a safe script-off boundary keeps waiting");
        Check(ConstructedStopContract.CanCompleteStop(true, false, false, false, true),
            "a settled match with a stopped script completes even without a receipt");
        Check(ConstructedStopContract.CanCompleteStop(false, true, true, false, false),
            "a closed client completes the stop");

        Console.WriteLine("PASS: constructed stop policy ({0} assertions): sustained off evidence, startup re-enable window, quota completion prefers completion over detach", _checks);
    }
}
