using ArenaController;

internal static class NativeActionDiagnosticsTests
{
    internal static void Run()
    {
        var d = new NativeActionDiagnostics();
        d.Observe("Gameplay.Awake() - CurrentMode=GAMEPLAY, PrevMode=DRAFT");
        d.Observe("BoxAI影子: requestId=box-1-1-play_minion, legal=True");
        var submit = "BoxAI链路提交: requestId=box-1-1-play_minion, optionsId=1, immediateEndTurn=False";
        d.Observe(submit); d.Observe(submit);
        d.Observe("BoxAI链路提交: requestId=box-1-2-end_turn, optionsId=2, immediateEndTurn=True");
        d.Observe("BoxAI自动执行选择: requestId=box-1--1-choice, entity=choice#52");
        d.Observe("BoxAI自动执行选择: requestId=box-1--1-choice, entity=choice#52");
        d.Observe("BoxAI自动执行选择: requestId=box-1--1-choice, entity=choice#53");
        d.Observe("BoxAI链路提交: requestId=none, optionsId=3");
        var s = d.Observe("WildLadderBot对局总结: result=胜利, actions=0, finalTurn=15")!;
        Check(s.Contains("observedSubmitRecords=2;observedChoiceRecords=2"), "actual records dedupe; same-turn choice request may identify different entities");
        Check(s.Contains("coverage=since-game-start;zeroSummaryConflict=True"), "zero native summary contradiction reported");
        Check(s.Contains("不代表服务端已确认"), "do not mislabel submissions as server confirmations");
        s = d.Observe("WildLadderBot对局总结: result=失败, actions=0, finalTurn=5")!;
        Check(s.Contains("observedSubmitRecords=0") && s.Contains("zeroSummaryConflict=False"), "summary resets game evidence");
        d.Observe(submit);
        s = d.Observe("WildLadderBot对局总结: result=胜利, actions=1")!;
        Check(s.Contains("coverage=partial-after-attachment;zeroSummaryConflict=False"), "midgame attachment does not claim complete coverage");
        d.Observe(submit); d.Reset();
        s = d.Observe("WildLadderBot对局总结: actions=invalid")!;
        Check(s.Contains("observedSubmitRecords=0") && s.Contains("nativeReportedActions=unknown"), "session rotation clears stale game and malformed count is unknown");
        Console.WriteLine("PASS: observed submission diagnostics; no server confirmation inference");
    }

    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
}
