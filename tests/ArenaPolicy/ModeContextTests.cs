using ArenaBoxBot;

internal static class ModeContextTests
{
    internal static void Run()
    {
        Check(ArenaLifecyclePolicy.ResolveStatsMode(false, true, true),
            "Changing the setting to normal midgame cannot reclassify a live underground game");
        Check(!ArenaLifecyclePolicy.ResolveStatsMode(false, false, true),
            "After leaving gameplay, selected normal mode overrides stale previous GameMgr type");
        var data = new ArenaStatsData();
        ArenaStatsLedger.SelectMode(data, true);
        ArenaStatsLedger.StartRun(data, "underground:42:701", 0, 0, true);
        ArenaStatsLedger.BeginGame(data, "701:first");
        ArenaStatsLedger.FinishGame(data, "701:first", false);
        data.RewardPending = true;
        Check(data.RunCount == 1 && data.TotalLosses == 1 && data.AutomaticGamesInRun == 1, "Underground run begins once");

        ArenaStatsLedger.SelectMode(data, false);
        Check(!data.RunActive && !data.RewardPending && data.RunKey == "" && data.CurrentRunLosses == 0,
            "An unfinished underground run must not block normal Arena entry or leak its score");
        Check(data.UndergroundRun!.RunKey == "underground:42:701" && data.UndergroundRun.RewardPending,
            "Suspended underground rewards remain saved");
        ArenaStatsLedger.StartRun(data, "normal:42:801", 0, 0, true);
        ArenaStatsLedger.BeginGame(data, "801:first");
        ArenaStatsLedger.FinishGame(data, "801:first", true);
        Check(data.RunCount == 2 && data.TotalWins == 1 && data.TotalLosses == 1 && data.CurrentRunWins == 1,
            "Normal game contributes only its own result and one new run");
        data = Restart(data);

        ArenaStatsLedger.SelectMode(data, true);
        Check(data.RunActive && data.RewardPending && data.CurrentRunWins == 0 && data.CurrentRunLosses == 1 &&
            data.RunKey == "underground:42:701" && data.AutomaticGamesInRun == 1,
            "Restart then switch restores underground score, reward and game budget");
        Check(!ArenaStatsLedger.StartRun(data, "underground:42:701", 0, 1) && data.RunCount == 2,
            "Restoring an existing mode cannot recount its run or import its score again");
        Check(!ArenaStatsLedger.FinishGame(data, "701:first", false) && data.TotalLosses == 1,
            "Result deduplication survives mode switch and restart");
        ArenaStatsLedger.CompleteRun(data);
        Check(data.RewardsClaimed == 1 && !data.RunActive, "Only underground run is completed");

        ArenaStatsLedger.SelectMode(data, false);
        Check(data.RunActive && !data.RewardPending && data.CurrentRunWins == 1 && data.CurrentRunLosses == 0 &&
            data.RunKey == "normal:42:801", "Completing underground cannot finish normal Arena");
        ArenaStatsLedger.BeginGame(data, "801:second");
        Check(data.AutomaticGamesInRun == 2, "Normal budget resumes from its own previous game");
        ArenaStatsLedger.SelectMode(data, true);
        data = Restart(data);
        ArenaStatsLedger.SelectMode(data, false);
        Check(data.PendingGameKey == "801:second" && data.PendingGameWinsBefore == 1 && data.PendingGameLossesBefore == 0,
            "Pending result baseline belongs to its own mode across restarts");
        ArenaStatsLedger.FinishGame(data, "801:second", false);
        Check(data.TotalWins == 1 && data.TotalLosses == 2 && data.CurrentRunLosses == 1,
            "Delayed normal result is counted once in normal context");
        data.RewardPending = true;
        ArenaStatsLedger.CompleteRun(data);
        data = Restart(data);
        Check(data.RewardsClaimed == 2 && data.RunCount == 2, "Both rewards complete without new run counts");
        ArenaStatsLedger.SelectMode(data, true);
        Check(!ArenaStatsLedger.StartRun(data, "underground:42:701", 0, 0), "Finished underground deck is remembered independently");
        ArenaStatsLedger.SelectMode(data, false);
        Check(!ArenaStatsLedger.StartRun(data, "normal:42:801", 0, 0), "Finished normal deck is remembered independently");

        data.EntryRequestedAtUnixMs = 100000;
        ArenaStatsLedger.SelectMode(data, true);
        Check(data.EntryRequestedAtUnixMs == 0, "Normal pending entry cannot throttle underground entry");
        ArenaStatsLedger.SelectMode(data, false);
        data = Restart(data);
        var gate = new ArenaLifecyclePolicy();
        gate.Observe("NO_RUN", 0);
        Check(!gate.CanRequestBegin(false, 5, data.EntryRequestedAtUnixMs, 105000),
            "Persisted entry request blocks duplicate spending after restart");
        Check(gate.CanRequestBegin(false, 35, data.EntryRequestedAtUnixMs, 135000),
            "Entry timeout eventually allows a safe retry");

        data.PurchasePending = true;
        data.PurchaseTicketsBefore = 0;
        data.PurchaseGoldBefore = 1000;
        data.PurchaseRequestedAtUnixMs = 200000;
        ArenaStatsLedger.SelectMode(data, true);
        ArenaStatsLedger.ReconcilePurchase(data, 1, 850, 205000);
        ArenaStatsLedger.SelectMode(data, false);
        Check(data.TicketsPurchased == 1 && data.GoldSpent == 150,
            "Shared tavern-ticket receipt is counted once regardless of selected Arena type");

        var legacy = ArenaStatsPersistence.Read(
            "{\"TotalWins\":36,\"TotalLosses\":32,\"RunCount\":25,\"RunActive\":true,\"CurrentRunWins\":3,\"CurrentRunLosses\":2}",
            out _);
        ArenaStatsLedger.SelectMode(legacy, true);
        ArenaStatsLedger.SelectMode(legacy, false);
        ArenaStatsLedger.SelectMode(legacy, true);
        Check(legacy.TotalWins == 36 && legacy.TotalLosses == 32 && legacy.RunCount == 25 &&
            legacy.CurrentRunWins == 3 && legacy.CurrentRunLosses == 2,
            "Pre-context file migration preserves historical counters and the old current run");
        ArenaStatsLedger.StartRun(legacy, "underground:42:902", 0, 0);
        Check(legacy.CurrentRunWins == 0 && legacy.CurrentRunLosses == 0 && legacy.TotalWins == 36 &&
            legacy.TotalLosses == 32 && legacy.RunCount == 25,
            "Binding a legacy marker to a verified native run rebases only its current score, never lifetime history");
        var keyedLegacy = new ArenaStatsData { RunActive = true, RunKey = "underground:42:999", RunCount = 5,
            CurrentRunWins = 2, TotalWins = 12 };
        ArenaStatsLedger.SelectMode(keyedLegacy, false);
        Check(!keyedLegacy.RunActive && keyedLegacy.UndergroundRun!.Wins == 2 && keyedLegacy.RunCount == 5 && keyedLegacy.TotalWins == 12,
            "A legacy deck identity overrides a newly selected different mode during migration");
        Console.WriteLine("PASS: independent normal/underground contexts, switches, shared receipts, restart and legacy migration");
    }

    private static ArenaStatsData Restart(ArenaStatsData data)
    {
        return ArenaStatsPersistence.Read(ArenaStatsPersistence.Write(data), out _);
    }

    private static void Check(bool pass, string message)
    {
        if (!pass) throw new InvalidOperationException(message);
    }
}
