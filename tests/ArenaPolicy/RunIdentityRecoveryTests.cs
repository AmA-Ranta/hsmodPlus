using ArenaBoxBot;

internal static class RunIdentityRecoveryTests
{
    internal static void Run()
    {
        var data = Original();
        data.RewardPending = true;
        data.PendingGameKey = "old-unfinished-game";
        var oldKey = data.RunKey;
        const string newKey = "underground:151:2770999999";
        var gate = new ArenaLifecyclePolicy();
        Check(!gate.CanAttachNativeRun(newKey, "MIDRUN", 2, 1, true, false, 0), "New identity needs a stable complete snapshot");
        Check(!gate.CanAttachNativeRun(newKey, "MIDRUN", 3, 1, true, false, 2), "Changing native score restarts stability");
        Check(!gate.CanAttachNativeRun(newKey, "MIDRUN", 3, 1, true, false, 4), "Partial elapsed time is not enough");
        Check(!ArenaStatsLedger.StartRun(data, newKey, 3, 1), "Ledger rejects active replacement without an explicit verified gate");
        Check(data.RunKey == oldKey && data.RewardPending, "Unstable observation cannot overwrite the old context");
        Check(gate.CanAttachNativeRun(newKey, "MIDRUN", 3, 1, true, false, 5), "Stable same-mode identity permits cold replacement");
        Check(ArenaStatsLedger.StartRun(data, newKey, 3, 1, true, true), "Replace the stale active identity after verification");
        Check(data.RunKey == newKey && data.CurrentRunWins == 3 && data.CurrentRunLosses == 1 &&
            data.RunPreviousKey == oldKey && data.RunOrigin == "replaced-existing-baseline" &&
            !data.RewardPending && data.PendingGameKey == "", "Replacement preserves provenance and clears only stale live-run state");
        UnchangedHistory(data);
        data = Restart(data);
        Check(data.UndergroundRun.PreviousKey == oldKey && data.UndergroundRun.Origin == "replaced-existing-baseline",
            "Replacement identity and provenance survive the production serializer");
        Check(!ArenaStatsLedger.StartRun(data, newKey, 3, 1, true, true), "Repeated same identity does not restart the attached run");
        ArenaStatsLedger.SelectMode(data, false);
        ArenaStatsLedger.StartRun(data, "normal:151:other", 1, 1);
        ArenaStatsLedger.SelectMode(data, true);
        Check(data.RunKey == newKey && data.CurrentRunWins == 3 && data.CurrentRunLosses == 1,
            "Replaced underground context remains independent of normal Arena");
        UnchangedHistory(data);
        ArenaStatsLedger.BeginGame(data, newKey + ":next");
        ArenaStatsLedger.FinishGame(data, newKey + ":next", false);
        ArenaStatsLedger.Reconcile(data, 3, 2);
        Check(data.TotalWins == 38 && data.TotalLosses == 33 && data.RunCount == 26 && data.RewardsClaimed == 15,
            "Only a new result after replacement changes cumulative history");

        gate = new ArenaLifecyclePolicy();
        Check(!gate.CanAttachNativeRun(newKey, "MIDRUN", 3, 1, true, false, 0), "Candidate starts pending");
        gate.ResetNativeEvidence();
        Check(!gate.CanAttachNativeRun(newKey, "MIDRUN", 3, 1, true, false, 50), "Unavailable session time cannot satisfy stability");
        Check(!gate.CanAttachNativeRun(newKey, "MIDRUN", 3, 1, false, false, 53), "Inactive native data cannot replace a run");
        Check(!gate.CanAttachNativeRun(newKey, "REWARDS", 3, 1, true, true, 60), "Reward flow cannot be reinterpreted as a new run");
        Check(!gate.CanAttachNativeRun("", "MIDRUN", 3, 1, true, false, 70), "Absent deck identity cannot replace a run");

        data = Original();
        oldKey = data.RunKey;
        gate = new ArenaLifecyclePolicy();
        gate.Observe("NO_RUN", 0);
        Check(!gate.CanRetireMissingRun("NO_RUN", false, false, false, false, 30), "Loading NO_RUN does not release a persisted active run");
        Check(!gate.CanRetireMissingRun("NO_RUN", true, false, false, false, 31), "Complete server emptiness starts its own confirmation window");
        Check(!gate.CanRetireMissingRun("NO_RUN", true, false, false, false, 39), "Confirmed emptiness must persist for ten seconds");
        Check(!gate.CanRetireMissingRun("NO_RUN", true, false, false, true, 40), "A visible reward chest interrupts retirement confirmation");
        Check(!gate.CanRetireMissingRun("NO_RUN", true, false, false, false, 41), "The window starts over after conflicting evidence");
        Check(gate.CanRetireMissingRun("NO_RUN", true, false, false, false, 51), "Settled complete empty session can release the obsolete run");
        // Even an old pending-reward flag cannot manufacture a claim through
        // this stale-context repair operation.
        data.RewardPending = true;
        Check(ArenaStatsLedger.RetireMissingRun(data), "Retire stale active snapshot without opening another run");
        Check(!data.RunActive && !data.RewardPending && data.RunKey == oldKey && data.LastCompletedRunKey == "" &&
            data.RunOrigin == "detached-server-empty", "Retirement remains distinguishable from observed reward completion");
        UnchangedHistory(data);
        Check(!ArenaStatsLedger.RetireMissingRun(data), "Repeating empty state has no second retirement effect");
        ArenaStatsLedger.ObserveEmptyRun(data, oldKey, 500000);
        data = Restart(data);
        Check(gate.CanRequestBegin(data.RunActive, 52), "Stale RunActive no longer blocks an authorized next entry");
        Check(!ArenaStatsLedger.HasNewRunEvidence(data, newKey, 500100), "Empty state alone cannot assign an unknown new entry");
        EntryReceiptTests.ConfirmEntry(data, newKey, 500100);
        ArenaStatsLedger.StartRun(data, newKey, 0, 0, ArenaStatsLedger.HasNewRunEvidence(data, newKey, 503200));
        Check(data.RunCount == 27 && data.TotalWins == 38 && data.TotalLosses == 32 && data.RewardsClaimed == 15,
            "A real subsequent entry counts once without inventing the old reward");
        Console.WriteLine("PASS: stale active run replacement and confirmed empty-run recovery preserve historical counters");
    }

    private static ArenaStatsData Original()
        => ArenaStatsPersistence.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures",
            "arena-stats-missing-contexts-20261007.json")), out _);

    private static ArenaStatsData Restart(ArenaStatsData data)
        => ArenaStatsPersistence.Read(ArenaStatsPersistence.Write(data), out _);

    private static void UnchangedHistory(ArenaStatsData data)
        => Check(data.TotalWins == 38 && data.TotalLosses == 32 && data.RunCount == 26 && data.RewardsClaimed == 15 &&
            data.TicketsPurchased == 6 && data.GoldSpent == 900, "Identity repair must not invent historical games, entries, purchases or rewards");

    private static void Check(bool pass, string message)
    {
        if (!pass) throw new InvalidOperationException(message);
    }
}
