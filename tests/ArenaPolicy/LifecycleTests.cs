using ArenaBoxBot;

internal static class LifecycleTests
{
    internal static void Run()
    {
        var gate = new ArenaLifecyclePolicy();
        gate.Observe("NO_RUN", 0);
        Check(!gate.CanRequestBegin(false, 1), "Unsettled NO_RUN must not spend a ticket");
        Check(gate.CanRequestBegin(false, 3), "Confirmed empty landing page may enter");
        gate.BeginRequested(3);
        gate.Observe("DRAFTING", 4);
        Check(!gate.CanSelect("DRAFTING", false, 30, 7), "Cached hero bucket needs an active server run");
        Check(!gate.CanConfirmRun("DRAFTING", true, 0, 30, 7), "Hero page alone cannot count successful entry");
        gate.Observe("NO_RUN", 8);
        Check(!gate.CanRequestBegin(false, 12), "NO_RUN bounce must retain the begin latch");
        Check(!gate.CanCloseRun("NO_RUN", false, 12), "NO_RUN bounce cannot close an active run");
        gate.Observe("DRAFTING", 13);
        Check(gate.CanSelect("DRAFTING", true, 30, 16), "Confirmed stable hero page must be operable");
        Check(gate.CanConfirmRun("DRAFTING", true, 1, 30, 16), "First accepted slot confirms entry");
        var data = new ArenaStatsData();
        Check(ArenaStatsLedger.StartRun(data, "underground:42:501", 0, 0, true), "Start first server deck");
        gate.ConfirmedRun();
        gate.Observe("NO_RUN", 20);
        Check(!gate.CanRequestBegin(data.RunActive, 55), "Tracked active run blocks another ticket");
        Check(!gate.CanDiscardLegacyPlaceholder("NO_RUN", false, data.RunKey, 55), "Never discard a identified run as legacy");
        var legacy = new ArenaStatsData { RunActive = true, RunCount = 25, TotalWins = 36, TotalLosses = 32 };
        Check(!gate.CanDiscardLegacyPlaceholder("NO_RUN", false, "", 25), "Legacy migration also waits through transient NO_RUN");
        Check(gate.CanDiscardLegacyPlaceholder("NO_RUN", false, "", 55), "Stable empty legacy state can release a phantom marker");
        ArenaStatsLedger.DiscardLegacyPlaceholder(legacy);
        Check(!legacy.RunActive && legacy.RunCount == 25 && legacy.TotalWins == 36 && legacy.TotalLosses == 32,
            "Legacy repair must preserve all historical counters without invented corrections");
        gate.Observe("DRAFTING", 56);
        Check(!ArenaStatsLedger.StartRun(data, "underground:42:501", 0, 0) && data.RunCount == 1,
            "NO_RUN/DRAFTING bounce must count only one run");

        ArenaStatsLedger.BeginGame(data, "501:game-A");
        Check(ArenaStatsLedger.FinishGame(data, "501:game-A", false), "Loss must persist before wrong-scene exit");
        Check(data.TotalLosses == 1 && data.CurrentRunLosses == 1, "Recorded one loss on immediate TOURNAMENT exit");
        Check(!ArenaStatsLedger.FinishGame(data, "501:game-A", false), "Repeated game-over callback must not duplicate");
        ArenaStatsLedger.Reconcile(data, 0, 1);
        Check(data.TotalLosses == 1, "Delayed server loss must not duplicate game callback");
        ArenaStatsLedger.Reconcile(data, 0, 0);
        Check(data.TotalLosses == 1 && data.CurrentRunLosses == 1, "Temporary server zero cannot reset result baseline");
        data = Restart(data);
        Check(!ArenaStatsLedger.FinishGame(data, "501:game-A", false), "Restart retains deduplication identity");
        ArenaStatsLedger.BeginGame(data, "501:game-B");
        ArenaStatsLedger.Reconcile(data, 1, 1);
        ArenaStatsLedger.FinishGame(data, "501:game-B", true);
        Check(data.TotalWins == 1 && data.TotalLosses == 1, "Server result before game callback still counts once");
        ArenaStatsLedger.BeginGame(data, "501:game-C");
        data = Restart(data);
        ArenaStatsLedger.FinishGame(data, "501:game-C", false);
        Check(data.TotalLosses == 2, "A reconnect retains the current game baseline");

        data.RewardPending = true;
        gate.Observe("NO_RUN", 100);
        Check(!gate.CanCloseRun("NO_RUN", true, 101), "Wait for reward acknowledgement to settle");
        Check(gate.CanCloseRun("NO_RUN", true, 103), "Settled reward acknowledgement completes run");
        Check(ArenaStatsLedger.CompleteRun(data), "Complete run once");
        Check(!ArenaStatsLedger.CompleteRun(data) && data.RewardsClaimed == 1, "Duplicate reward updates cannot count twice");
        Check(!ArenaStatsLedger.StartRun(data, "underground:42:501", 0, 0), "Stale finished deck cannot rehydrate as a new run");
        Check(ArenaStatsLedger.StartRun(data, "underground:42:502", 0, 0, true) && data.RunCount == 2,
            "A new server deck starts the next real run");
        Check(data.TotalWins == 1 && data.TotalLosses == 2 && data.CurrentRunLosses == 0, "Run transition preserves lifetime counters");

        var purchase = new ArenaStatsData { PurchasePending = true, PurchaseRequestedAtUnixMs = 1000,
            PurchaseTicketsBefore = 0, PurchaseGoldBefore = 3325 };
        Check(!ArenaStatsLedger.ReconcilePurchase(purchase, 0, 3325, 5000), "Purchase request alone is not a purchase");
        Check(ArenaStatsLedger.ReconcilePurchase(purchase, 1, 3325, 6000), "Ticket receipt can arrive first");
        Check(purchase.TicketsPurchased == 1 && purchase.GoldSpent == 0 && purchase.PurchasePending,
            "Wait for real gold receipt; do not invent a configured debit");
        purchase = Restart(purchase);
        ArenaStatsLedger.ReconcilePurchase(purchase, 1, 3175, 7000);
        Check(purchase.TicketsPurchased == 1 && purchase.GoldSpent == 150 && !purchase.PurchasePending,
            "Gold receipt after a restart updates the same purchase exactly once");
        Check(!ArenaStatsLedger.ReconcilePurchase(purchase, 1, 3175, 8000), "Repeated balance updates cannot duplicate receipt");
        var consumed = new ArenaStatsData { PurchasePending = true, PurchaseRequestedAtUnixMs = 1,
            PurchaseTicketsBefore = 0, PurchaseGoldBefore = 3325 };
        ArenaStatsLedger.ReconcilePurchase(consumed, 0, 3175, 100);
        Check(consumed.TicketsPurchased == 1 && consumed.GoldSpent == 150,
            "Immediately consumed ticket must still be recorded from its currency receipt");
        var passive = new ArenaStatsData();
        Check(!ArenaStatsLedger.ReconcilePurchase(passive, 1, 3175, 100), "Unrelated resource changes are not an owned purchase");
        var expired = new ArenaStatsData { PurchasePending = true, PurchaseRequestedAtUnixMs = 0,
            PurchaseTicketsBefore = 0, PurchaseGoldBefore = 3325 };
        ArenaStatsLedger.ReconcilePurchase(expired, 0, 3325, 90000);
        Check(!expired.PurchasePending && expired.TicketsPurchased == 0, "Failed request cannot claim a later unrelated debit");

        Check(ArenaLifecyclePolicy.ShouldReturnToHub("TOURNAMENT", "INVALID", false, 31), "Recover a sustained wrong scene");
        Check(!ArenaLifecyclePolicy.ShouldReturnToHub("GAMEPLAY", "INVALID", false, 3600), "Never leave a long healthy game");
        Check(!ArenaLifecyclePolicy.ShouldReturnToHub("TOURNAMENT", "DRAFT", false, 31), "Do not interrupt pending native transition");
        Check(!ArenaLifecyclePolicy.ShouldReturnToHub("TOURNAMENT", "INVALID", true, 31), "User stop has priority over recovery");
        Check(!ArenaLifecyclePolicy.ShouldCloseStaleReward("DRAFTING", false, false, 3),
            "Ordinary three-card draft must not restart animations or refresh choices");
        Check(!ArenaLifecyclePolicy.ShouldCloseStaleReward("DRAFTING", true, true, 3),
            "Unacknowledged reward cannot be hidden");
        Check(ArenaLifecyclePolicy.ShouldCloseStaleReward("DRAFTING", true, false, 3),
            "A genuinely stale reward visual may be cleaned after server acknowledgement");
        RewardReceiptSequences();
        Console.WriteLine("PASS: Arena lifecycle sequences, persisted result ledger, purchase receipt reconciliation");
    }

    private static void RewardReceiptSequences()
    {
        var receipt = new ArenaRewardReceiptPolicy();
        Check(!receipt.Observe("run-A", true, "LOGIN", "INVALID", false, "NO_RUN", false, false, 0),
            "Cold login placeholder is not a reward receipt");
        Check(!receipt.Observe("run-A", true, "LOGIN", "INVALID", true, "NO_RUN", false, false, 300),
            "A season cache alone cannot complete rewards during login");
        Check(!receipt.Observe("run-A", true, "HUB", "INVALID", false, "NO_RUN", false, false, 301),
            "Menu without the matching initialized season remains unconfirmed");
        Check(!receipt.Observe("run-A", true, "HUB", "DRAFT", true, "NO_RUN", false, false, 305),
            "A pending scene transition resets reward confirmation");
        Check(!receipt.Observe("run-A", true, "DRAFT", "INVALID", true, "NO_RUN", true, false, 310),
            "An active server run contradicts empty cached state");
        Check(!receipt.Observe("run-A", true, "DRAFT", "INVALID", true, "NO_RUN", false, true, 315),
            "An unclaimed reward chest contradicts empty cached state");
        Check(!receipt.Observe("run-A", true, "DRAFT", "INVALID", true, "NO_RUN", false, false, 320),
            "Confirmation starts only after all authoritative signals agree");
        Check(!receipt.Observe("run-A", true, "LOGIN", "INVALID", true, "NO_RUN", false, false, 322),
            "Reconnect interrupts the confirmation window");
        Check(!receipt.Observe("run-A", true, "DRAFT", "INVALID", true, "NO_RUN", false, false, 340),
            "Reconnect elapsed time cannot count as a receipt");
        Check(!receipt.Observe("run-B", true, "DRAFT", "INVALID", true, "NO_RUN", false, false, 343),
            "Switching run context starts an independent confirmation window");
        Check(!receipt.Observe("run-B", true, "DRAFT", "INVALID", true, "NO_RUN", false, false, 345),
            "A real acknowledgement must settle for all three seconds");
        Check(receipt.Observe("run-B", true, "HUB", "INVALID", true, "NO_RUN", false, false, 346),
            "Settled authenticated empty run with no chest completes rewards");
        Check(!receipt.Observe("run-B", false, "HUB", "INVALID", true, "NO_RUN", false, false, 350),
            "Persisted completion cannot count the same reward again");
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
