using ArenaBoxBot;

// Link the production partial activation and budget methods unchanged. Only
// Unity's page reset, diagnostic sink and stop side effects are replaced here.
// These regressions execute the logic Update calls, rather than modeling a
// process restart solely by round-tripping the ledger.
namespace ArenaBoxBot
{
    internal static class ArenaStatsStore
    {
        internal static ArenaStatsData Data = new();
    }

    internal sealed partial class ArenaController
    {
        private int _gamesThisRun;
        private bool _runStopRequested;
        private string _runStopReason = "";
        private static string s_arenaOverlayStopReason = "";
        internal int ResetCalls;
        internal int StopCalls;
        internal bool Stopped => _runStopRequested;
        internal int ProjectedGames => _gamesThisRun;
        internal string StopReason => _runStopReason;
        internal string OverlayStop => s_arenaOverlayStopReason;
        private void ResetArenaPageTransientState() => ResetCalls++;
        private void UpdateArenaBudgetOverlay() { }
        private void Log(string _) { }
        private void RequestArenaStop(string reason)
        {
            StopCalls++;
            _runStopRequested = true;
            _runStopReason = s_arenaOverlayStopReason = reason;
        }
        internal void Activation(bool selected, bool enabled) => ObserveNativeArenaActivation(selected, enabled);
        internal ArenaRunBudgetDecision Budget(string key, string state = "MIDRUN", bool ready = true,
            bool underground = true, bool active = true, bool rewards = false, int slot = 30, int max = 30,
            bool empty = false, bool receipt = false)
            => EvaluateNativeRunBudget(ready, underground, key, state, active, rewards, slot, max, empty, receipt);
        internal void Limit(ArenaRunBudgetDecision decision, int limit, string scene = "DRAFT", bool enabled = true)
            => RequestAlignedGameLimitStop(enabled, scene, limit, decision);
        internal bool ReceiptLimit(bool confirmed = true, bool modeMatches = true, string state = "NO_RUN",
            bool active = false, bool chest = false, bool enabled = true, string scene = "DRAFT", int limit = 5)
            => TryRequestConfirmedRewardBudgetStop(confirmed, modeMatches, state, active, chest, enabled, scene, limit);
    }
}

internal static class BudgetRuntimeTests
{
    private const string RunKey = "underground:151:2770485547";
    private const string NextRun = "underground:151:2770487777";
    private static int _checks;

    internal static void Run()
    {
        // The actual failure: cold load sees runtime disabled, API later enables
        // it. Preserve the same round's five games before counting game six.
        var data = Games(5);
        var pending = data.PendingGameKey;
        ArenaStatsStore.Data = data;
        var runtime = new ArenaBoxBot.ArenaController();
        runtime.Activation(true, false);
        runtime.Activation(true, true);
        Check(data.AutomaticGamesInRun == 5 && runtime.ProjectedGames == 5 && data.PendingGameKey == pending,
            "Cold API re-enable must preserve the run budget and pending identity");
        Check(ArenaStatsLedger.BeginGame(data, Game(6)) && data.AutomaticGamesInRun == 6,
            "Game six after maintenance must be counted as six");
        runtime.Limit(runtime.Budget(RunKey), 6, "GAMEPLAY");
        Check(!runtime.Stopped, "A reached budget may not interrupt gameplay");
        runtime.Limit(runtime.Budget(RunKey), 6);
        Check(runtime.Stopped, "Game six must stop further games at the native boundary");

        // F12 and mode-off/on are pauses of the same run, including an already
        // latched stop. Re-enable clears the old latch but re-evaluates the
        // retained count; it never grants another six games silently.
        runtime.Activation(true, false);
        runtime.Activation(true, true);
        Check(!runtime.Stopped && runtime.StopReason == "" && runtime.OverlayStop == "",
            "Explicit re-enable must clear the old stop latch for re-evaluation");
        runtime.Limit(runtime.Budget(RunKey), 6);
        Check(runtime.Stopped && data.AutomaticGamesInRun == 6, "An unchanged limit must stop again");
        runtime.Activation(true, false);
        runtime.Activation(true, true);
        runtime.Limit(runtime.Budget(RunKey), 7);
        Check(!runtime.Stopped && data.PendingGameKey == Game(6), "Raising the limit allows the same run to continue");
        runtime.Activation(false, false);
        runtime.Activation(true, true);
        Check(data.AutomaticGamesInRun == 6 && data.PendingGameKey == Game(6),
            "Leaving Arena mode and returning must retain its ledger");
        var resets = runtime.ResetCalls;
        runtime.Activation(true, true);
        Check(runtime.ResetCalls == resets, "Unchanged heartbeat state must not repeatedly reset page state");

        // Restart during a counted game: execute the production cold-off/on
        // path after persistence, then verify the new client handle's same
        // server identity is not counted again.
        data = ArenaStatsPersistence.Read(ArenaStatsPersistence.Write(data), out _);
        ArenaStatsStore.Data = data;
        runtime = new ArenaBoxBot.ArenaController();
        runtime.Activation(true, false);
        runtime.Activation(true, true);
        Check(data.AutomaticGamesInRun == 6 && !ArenaStatsLedger.BeginGame(data, Game(6)),
            "Recovery of the same pending game must preserve six and avoid seven");
        Check(ArenaStatsLedger.FinishGame(data, Game(6), false) && !ArenaStatsLedger.FinishGame(data, Game(6), false),
            "The resumed game's result remains exactly once");

        // No previous key may decide a new native run's budget. Slot zero is
        // deliberately not bindable yet; native selection must remain possible.
        ArenaStatsStore.Data = data = Games(5);
        runtime = new ArenaBoxBot.ArenaController();
        runtime.Activation(true, false);
        runtime.Activation(true, true);
        var firstChoice = runtime.Budget(NextRun, "DRAFTING", slot: 0);
        runtime.Limit(firstChoice, 5);
        Check(firstChoice == ArenaRunBudgetDecision.DraftSelectionOnly && !runtime.Stopped,
            "A new first hero may be selected without applying the old reached budget");
        var lifecycle = new ArenaLifecyclePolicy();
        lifecycle.Observe("DRAFTING", 0);
        Check(!lifecycle.CanConfirmRun("DRAFTING", true, 0, 30, 4),
            "Production lifecycle must still defer first-slot run confirmation");
        var unbound = runtime.Budget(NextRun, "DRAFTING", slot: 1);
        runtime.Limit(unbound, 5);
        Check(unbound == ArenaRunBudgetDecision.WaitForIdentity && !runtime.Stopped,
            "Unbound new identities may neither queue nor stop using the previous budget");
        Check(lifecycle.CanConfirmRun("DRAFTING", true, 1, 30, 4) &&
            ArenaStatsLedger.StartRun(data, NextRun, 0, 0, false, true),
            "After the first native choice the existing lifecycle can replace the stale run");
        var aligned = runtime.Budget(NextRun, "DRAFTING", slot: 1);
        runtime.Limit(aligned, 5);
        Check(aligned == ArenaRunBudgetDecision.Ready && data.AutomaticGamesInRun == 0 &&
            data.PendingGameKey == "" && !runtime.Stopped && data.RunCount == 1 && runtime.ProjectedGames == 0,
            "Stable actual new identity resets its budget without crediting a manual entry");

        foreach (var observation in new[]
        {
            (Key: "", Ready: true, Underground: true),
            (Key: RunKey, Ready: false, Underground: true),
            (Key: RunKey, Ready: true, Underground: false),
            (Key: NextRun, Ready: true, Underground: true)
        })
        {
            ArenaStatsStore.Data = data = Games(5);
            runtime = new ArenaBoxBot.ArenaController();
            runtime.Activation(true, false);
            runtime.Activation(true, true);
            var decision = runtime.Budget(observation.Key, ready: observation.Ready, underground: observation.Underground);
            runtime.Limit(decision, 5);
            Check(decision == ArenaRunBudgetDecision.WaitForIdentity && !runtime.Stopped && data.AutomaticGamesInRun == 5,
                "Unknown, mismatched mode and unbound ready decks must retain budget and wait");
        }

        // Rewards still latch the reached budget before clearing the completed
        // ledger, including a fast ACK that removes the old deck in HUB.
        foreach (var scene in new[] { "DRAFT", "HUB" })
        {
            ArenaStatsStore.Data = data = Games(5);
            data.RewardPending = true;
            runtime = new ArenaBoxBot.ArenaController();
            runtime.Activation(true, false);
            runtime.Activation(true, true);
            var receipt = runtime.Budget("", "NO_RUN", active: false, receipt: true);
            runtime.Limit(receipt, 5, scene);
            Check(runtime.Stopped && ArenaStatsLedger.CompleteRun(data) && data.AutomaticGamesInRun == 0 &&
                ArenaStopPolicy.BlocksFurtherActions(runtime.Stopped, false),
                "Fast confirmed reward receipt must retain stop intent after budget reset: " + scene);
            runtime.Activation(true, true);
            Check(runtime.Stopped,
                "The post-receipt unchanged activation cannot erase this tick's newly latched stop");
            Check(!ArenaStopPolicy.BlocksFurtherActions(runtime.Stopped, true), "A limit stop must still allow rewards");
            runtime.Activation(true, false);
            runtime.Activation(true, true);
            Check(!runtime.Stopped && ArenaStatsLedger.StartRun(data, NextRun, 0, 0, true) &&
                data.AutomaticGamesInRun == 0, "A confirmed next run starts its own zero budget");
        }

        ArenaStatsStore.Data = data = Games(5);
        data.RewardPending = true;
        runtime = new ArenaBoxBot.ArenaController();
        runtime.Activation(true, false);
        runtime.Activation(true, true);
        var rewardsPage = runtime.Budget(RunKey, "REWARDS", rewards: true);
        runtime.Limit(rewardsPage, 5);
        Check(rewardsPage == ArenaRunBudgetDecision.Ready && runtime.Stopped &&
            !ArenaStopPolicy.BlocksFurtherActions(runtime.Stopped, true),
            "The corresponding native reward page must latch the limit and remain actionable");
        Check(runtime.Budget(NextRun, "REWARDS", rewards: true) == ArenaRunBudgetDecision.WaitForIdentity,
            "Another native run's rewards cannot prove the previous budget");
        data.RewardPending = false;
        Check(runtime.Budget("", "NO_RUN", active: false, receipt: true) == ArenaRunBudgetDecision.WaitForIdentity,
            "A receipt flag without a retained pending reward context cannot authorize the old budget");
        Check(runtime.Budget("", "NO_RUN", active: false, rewards: true, empty: true) == ArenaRunBudgetDecision.WaitForIdentity,
            "An in-rewards session cannot be treated as confirmed empty");
        Check(runtime.Budget(RunKey, active: false) == ArenaRunBudgetDecision.WaitForIdentity,
            "An inactive ready-deck placeholder cannot authorize the tracked active budget");

        // Local native IL proves a cleared packet can lack HasIsActive/HasState
        // while GetArenaState still says NO_RUN and HasCurrentlyActiveRun is
        // false. Execute the production receipt stop path with the stronger
        // normal identity gate unavailable, before the existing reward clear.
        ArenaStatsStore.Data = data = Games(5);
        data.RewardPending = true;
        runtime = new ArenaBoxBot.ArenaController();
        runtime.Activation(true, false);
        runtime.Activation(true, true);
        Check(runtime.Budget("", "NO_RUN", ready: false, active: false, receipt: true) == ArenaRunBudgetDecision.WaitForIdentity,
            "Missing Has fields must still block normal native identity actions");
        Check(runtime.ReceiptLimit(scene: "HUB") && runtime.Stopped,
            "The already confirmed same-mode pending reward must latch stop even without session Has fields");
        Check(ArenaStatsLedger.CompleteRun(data) && data.AutomaticGamesInRun == 0 && runtime.Stopped,
            "The missing-packet fast receipt cannot erase its reached-limit stop by clearing the ledger");
        foreach (var invalid in new[] { "unconfirmed", "other-mode", "not-empty", "active", "chest", "no-pending" })
        {
            ArenaStatsStore.Data = data = Games(5);
            data.RewardPending = invalid != "no-pending";
            runtime = new ArenaBoxBot.ArenaController();
            var accepted = runtime.ReceiptLimit(confirmed: invalid != "unconfirmed", modeMatches: invalid != "other-mode",
                state: invalid == "not-empty" ? "MIDRUN" : "NO_RUN", active: invalid == "active", chest: invalid == "chest");
            Check(!accepted && !runtime.Stopped && data.AutomaticGamesInRun == 5,
                "Receipt-only stop authority must reject an invalid retained/native context: " + invalid);
        }
        ArenaStatsStore.Data = data = Games(5);
        data.RewardPending = true;
        runtime = new ArenaBoxBot.ArenaController();
        Check(runtime.ReceiptLimit(enabled: false) && !runtime.Stopped,
            "A late confirmed reward after manual disable must not create an automation stop");

        // Transient NO_RUN cannot clear or misapply a live budget. Once the
        // existing native empty gate is stable, latch before RetireMissingRun.
        ArenaStatsStore.Data = data = Games(5);
        runtime = new ArenaBoxBot.ArenaController();
        var transient = runtime.Budget("", "NO_RUN", active: false);
        runtime.Limit(transient, 5);
        Check(transient == ArenaRunBudgetDecision.WaitForIdentity && !runtime.Stopped && data.AutomaticGamesInRun == 5,
            "Loading-time NO_RUN cannot alter the live run budget");
        var empty = runtime.Budget("", "NO_RUN", active: false, empty: true);
        runtime.Limit(empty, 5);
        Check(runtime.Stopped && ArenaStatsLedger.RetireMissingRun(data) && data.AutomaticGamesInRun == 0 &&
            ArenaStopPolicy.BlocksFurtherActions(runtime.Stopped, false),
            "Stable NO_RUN must latch the old reached budget before retirement can clear it");
        runtime.Limit(ArenaRunBudgetDecision.Ready, 5);
        Check(runtime.ProjectedGames == 0 && runtime.Stopped,
            "Retirement projects the new zero budget without erasing the reached stop");

        // Selecting a different Arena type restores its own ledger, rather than
        // treating that navigation or activation as a new financial/run event.
        ArenaStatsStore.Data = data = Games(5);
        ArenaStatsLedger.SelectMode(data, false);
        ArenaStatsLedger.StartRun(data, "normal:151:other", 0, 0, true);
        ArenaStatsLedger.BeginGame(data, "normal:game1");
        runtime = new ArenaBoxBot.ArenaController();
        runtime.Activation(true, false);
        runtime.Activation(true, true);
        Check(data.AutomaticGamesInRun == 1 && data.PendingGameKey == "normal:game1",
            "Normal activation preserves only its own context");
        ArenaStatsLedger.SelectMode(data, true);
        runtime.Activation(false, false);
        runtime.Activation(true, true);
        Check(data.AutomaticGamesInRun == 5 && data.PendingGameKey == Game(5) && runtime.ProjectedGames == 5,
            "Returning to underground restores its prior five-game budget");
        runtime.Limit(runtime.Budget(RunKey), 0);
        Check(!runtime.Stopped, "Unlimited configuration does not impose a new stop");
        Console.WriteLine("PASS: production Arena activation and identity-aware budget boundaries (" + _checks + " assertions)");
    }

    private static string Game(int index) => RunKey + ":server:host:3724:" + index;
    private static ArenaStatsData Games(int count)
    {
        var data = new ArenaStatsData();
        ArenaStatsLedger.SelectMode(data, true);
        ArenaStatsLedger.StartRun(data, RunKey, 0, 0, true);
        for (var i = 1; i <= count; i++) ArenaStatsLedger.BeginGame(data, Game(i));
        return data;
    }
    private static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(message);
    }
}
