using ArenaBoxBot;

internal static class SessionStateTests
{
    private static int _assertions;

    private static void Check(bool condition, string reason)
    {
        _assertions++;
        if (!condition) throw new Exception(reason);
    }

    internal static void Run()
    {
        foreach (var legacy in new[] { false, true })
        {
            Check(!ArenaSessionStatePolicy.BlocksEmptySessionFromRewards("NO_RUN", "NO_RUN", legacy),
                "Complete canonical NO_RUN pair must not be overridden by retained legacy reward bit");
            foreach (var other in new[] { "REWARDS", "IN_REWARDS", "DRAFTING", "MIDRUN", "REDRAFTING", "EDITING_DECK", "INVALID", "UNKNOWN", "", "no_run", " NO_RUN" })
            {
                Check(ArenaSessionStatePolicy.BlocksEmptySessionFromRewards("NO_RUN", other, legacy),
                    "A nonempty/unknown session cannot inherit manager NO_RUN emptiness: " + other);
                Check(ArenaSessionStatePolicy.BlocksEmptySessionFromRewards(other, "NO_RUN", legacy),
                    "A nonempty/unknown manager cannot inherit session NO_RUN emptiness: " + other);
                Check(ArenaSessionStatePolicy.BlocksEmptySessionFromRewards(other, other, legacy),
                    "Matching nonempty states are not an empty-session reward proof: " + other);
            }
            Check(ArenaSessionStatePolicy.BlocksEmptySessionFromRewards(null!, "NO_RUN", legacy), "Missing manager state must block");
            Check(ArenaSessionStatePolicy.BlocksEmptySessionFromRewards("NO_RUN", null!, legacy), "Missing session state must block");
        }

        // Replay the fresh PID15116 observation: complete NO_RUN, inactive, no chest,
        // legacy reward=true. The unchanged primitive blocks forever with the raw bit.
        var raw = new ArenaLifecyclePolicy();
        raw.Observe("NO_RUN", 0);
        Check(!raw.CanObserveEmptyRun("NO_RUN", true, false, true, false, 10), "Raw legacy bit reproduces observed failure");
        Check(!raw.CanObserveEmptyRun("NO_RUN", true, false, true, false, 70), "Time alone cannot resolve the original stale-bit failure");

        var staleRecovery = new ArenaLifecyclePolicy();
        staleRecovery.Observe("NO_RUN", 0);
        Check(!staleRecovery.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, false, false, 3),
            "Stale reward recovery must wait for a complete stable boundary");
        Check(!staleRecovery.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, false, false, 12.9f),
            "Stale reward recovery cannot shorten its ten-second confirmation window");
        Check(staleRecovery.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, false, false, 13),
            "Server NO_RUN plus inactive reward residue can release the next-entry gate after ten seconds");
        Check(!staleRecovery.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, true, false, 14),
            "Visible reward UI must block stale-session recovery");
        Check(!staleRecovery.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, false, true, 25),
            "A reward chest must block stale-session recovery");
        Check(!staleRecovery.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, true, false, false, 40),
            "An active native session must block stale-session recovery");
        Check(!staleRecovery.CanRecoverStaleRewardSession("DRAFTING", "REWARDS", true, false, false, false, 50),
            "Manager state disagreement must reset stale-session recovery");

        var gate = new ArenaLifecyclePolicy();
        gate.Observe("NO_RUN", 0);
        var blocked = ArenaSessionStatePolicy.BlocksEmptySessionFromRewards("NO_RUN", "NO_RUN", true);
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, blocked, false, 10), "Canonical proof still starts a fresh confirmation window");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, blocked, false, 12.9f), "Canonical proof cannot shorten the three-second wait");
        Check(gate.CanObserveEmptyRun("NO_RUN", true, false, blocked, false, 13), "The actual production primitive accepts stable complete canonical emptiness");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, blocked, true, 14), "A chest still overrides canonical empty states");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, blocked, false, 15), "Chest conflict resets the complete-empty evidence clock");
        Check(gate.CanObserveEmptyRun("NO_RUN", true, false, blocked, false, 18), "Fresh stable empty evidence recovers after chest removal");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, true, blocked, false, 19), "Native active state cannot be bypassed by canonical reward interpretation");
        Check(!gate.CanObserveEmptyRun("NO_RUN", false, false, blocked, false, 20), "Missing complete session cannot be bypassed");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false,
            ArenaSessionStatePolicy.BlocksEmptySessionFromRewards("REWARDS", "NO_RUN", false), false, 21),
            "State disagreement interrupts the same production evidence clock");
        Check(!gate.CanRetireMissingRun("NO_RUN", true, false, blocked, false, 22), "Missing-run retirement retains its own ten-second wait");
        Check(!gate.CanRetireMissingRun("NO_RUN", true, false, blocked, false, 31.9f), "Canonical proof cannot shorten missing-run retirement");
        Check(gate.CanRetireMissingRun("NO_RUN", true, false, blocked, false, 32), "Ten seconds of complete canonical proof can retire an obsolete tracked run");
        gate.Observe("REWARDS", 33);
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, blocked, false, 40), "Lifecycle manager state disagreement remains a separate gate");
        RetainedBudgetAtCanonicalEmpty();
        Console.WriteLine($"PASS: {_assertions} canonical session state assertions; unchanged lifecycle primitive, active/chest/readiness and confirmation windows");
    }

    private static void RetainedBudgetAtCanonicalEmpty()
    {
        const string playedRun = "underground:151:2770485547";
        var savedData = ArenaStatsStore.Data;
        try
        {
            var data = new ArenaStatsData();
            ArenaStatsLedger.SelectMode(data, true);
            ArenaStatsLedger.StartRun(data, playedRun, 0, 0, true);
            for (var i = 1; i <= 5; i++) ArenaStatsLedger.BeginGame(data, playedRun + ":state-fixture:" + i);
            Check(data.AutomaticGamesInRun == 5, "Retained budget fixture uses the production counted-game ledger");
            ArenaStatsStore.Data = data;
            var runtime = new ArenaBoxBot.ArenaController();
            runtime.Activation(true, false);
            runtime.Activation(true, true);
            var raw = runtime.Budget("", "NO_RUN", active: false, rewards: true, empty: true);
            runtime.Limit(raw, 5);
            Check(raw == ArenaRunBudgetDecision.WaitForIdentity && !runtime.Stopped,
                "Retained legacy reward bit reproduces the completed-run budget wait");

            var lifecycle = new ArenaLifecyclePolicy();
            lifecycle.Observe("NO_RUN", 0);
            var rewards = ArenaSessionStatePolicy.BlocksEmptySessionFromRewards("NO_RUN", "NO_RUN", true);
            Check(!lifecycle.CanObserveEmptyRun("NO_RUN", true, false, rewards, false, 1), "Budget emptiness requires fresh stable native proof");
            var empty = lifecycle.CanObserveEmptyRun("NO_RUN", true, false, rewards, false, 4);
            Check(empty, "Canonical empty proof reaches the unchanged production budget evaluator");
            var ready = runtime.Budget("", "NO_RUN", active: false, rewards: rewards, empty: empty);
            runtime.Limit(ready, 5);
            Check(ready == ArenaRunBudgetDecision.Ready && runtime.Stopped && data.AutomaticGamesInRun == 5,
                "Reached limit is latched before stable NO_RUN can retire the old counter");
            Check(ArenaStatsLedger.RetireMissingRun(data) && data.AutomaticGamesInRun == 0 && runtime.Stopped,
                "Retirement cannot erase an already latched game limit");

            foreach (var missingProof in new[] { "not-ready", "active", "chest", "mode-mismatch" })
            {
                data = new ArenaStatsData { RunActive = true, RunUnderground = true, RunKey = playedRun, AutomaticGamesInRun = 5 };
                ArenaStatsStore.Data = data;
                runtime = new ArenaBoxBot.ArenaController();
                var proof = new ArenaLifecyclePolicy();
                proof.Observe("NO_RUN", 0);
                var nativeReady = missingProof != "not-ready" && missingProof != "mode-mismatch";
                var active = missingProof == "active";
                var chest = missingProof == "chest";
                proof.CanObserveEmptyRun("NO_RUN", nativeReady, active, rewards, chest, 1);
                var confirmedEmpty = proof.CanObserveEmptyRun("NO_RUN", nativeReady, active, rewards, chest, 20);
                Check(!confirmedEmpty, "Canonical rewards cannot bypass remaining empty proof: " + missingProof);
                var decision = runtime.Budget("", "NO_RUN", ready: nativeReady,
                    underground: missingProof != "mode-mismatch", active: active, rewards: rewards, empty: confirmedEmpty);
                runtime.Limit(decision, 5);
                Check(decision == ArenaRunBudgetDecision.WaitForIdentity && !runtime.Stopped,
                    "Incomplete/mismatched empty observation cannot retire counters or latch a false stop: " + missingProof);
            }
        }
        finally { ArenaStatsStore.Data = savedData; }
    }
}
