using ArenaBoxBot;

static class DraftStabilityTests
{
    private static int _assertions;
    private static readonly float[] Layout = { -4f, 0f, 1f, 1f, 0f, 0f, 4f, 0f, 1f, 1f };

    private static int Main()
    {
        SlowAssetsDoNotBecomeClicks();
        MotionMustActuallyStop();
        FrameFreezeAndMissingObservationsDoNotCount();
        VeryLowFrameRatesCanStillComplete();
        ReplacedAndConsecutiveBucketsRearm();
        RefreshDoesNotStarveSlowLoads();
        RefreshIsBoundedAndSlowsDown();
        ThinkingPaceHasAReadableMinimum();
        ModeSwitchRequiresNativeViewAndSession();
        NavigationFailuresStillCompleteSafeStops();
        DraftEntryUsesNativePageBeforeCachedChoices();
        DraftEntryRetriesAreBoundedAndPageSpecific();
        NativeTicketEntryRequiresAnAuthoritativeEmptyRun();
        Console.WriteLine("PASS: Arena draft stability sequences (" + _assertions + " assertions)");
        return 0;
    }

    private static void SlowAssetsDoNotBecomeClicks()
    {
        var gate = new ArenaDraftVisualGate();
        for (var frame = 0; frame < 120; frame++)
            Require(!gate.Observe("slot1", "actors", Layout, false, frame * .5f, frame),
                "A 60s asset/animation wait must never be treated as ready by elapsed time alone");
        Require(!gate.Observe("slot1", "actors", Layout, true, 60f, 120), "First loaded frame is insufficient");
        Require(!gate.Observe("slot1", "actors", Layout, true, 60.5f, 121), "Two observations are insufficient");
        Require(!gate.Observe("slot1", "actors", Layout, true, 61f, 122), "Three observations are insufficient");
        Require(gate.Observe("slot1", "actors", Layout, true, 61.5f, 123), "Slow assets become usable after stable rendered observations");
        Require(!gate.Observe("slot1", "actors", Layout, false, 62f, 124), "Native transition must immediately close a previously open gate");
    }

    private static void MotionMustActuallyStop()
    {
        var gate = new ArenaDraftVisualGate();
        for (var frame = 0; frame < 50; frame++)
        {
            var moving = (float[])Layout.Clone();
            moving[0] += frame * .002f;
            Require(!gate.Observe("slot1", "actors", moving, true, frame * .25f, frame),
                "Small per-frame drift must not accumulate into an apparently stable animation");
        }
        gate.Reset();
        for (var frame = 0; frame < 3; frame++)
            Require(!gate.Observe("slot1", "actors", Layout, true, frame * .5f, frame), "Layout settling needs multiple frames");
        var rotated = (float[])Layout.Clone();
        rotated[4] += .01f;
        Require(!gate.Observe("slot1", "actors", rotated, true, 1.5f, 3), "Rotation changes restart visual settling");
        Require(!gate.Observe("slot1", "actors", new[] { float.NaN }, true, 2f, 4), "Invalid transforms cannot be clicked");
    }

    private static void FrameFreezeAndMissingObservationsDoNotCount()
    {
        var gate = new ArenaDraftVisualGate();
        for (var sample = 0; sample < 30; sample++)
            Require(!gate.Observe("slot1", "actors", Layout, true, sample * .5f, 12),
                "Repeated observations of one frozen Unity frame must not count as rendering progress");
        gate.Reset();
        for (var frame = 0; frame < 4; frame++) gate.Observe("slot1", "actors", Layout, true, frame * .5f, frame);
        Require(!gate.Observe("slot1", "actors", Layout, true, 30f, 100), "Long unobserved stalls must rearm the gate");
        Require(!gate.Observe("slot1", "actors", Layout, true, 0f, 0), "A scene time/frame reset must rearm the gate");
    }

    private static void ReplacedAndConsecutiveBucketsRearm()
    {
        var gate = new ArenaDraftVisualGate();
        for (var frame = 0; frame < 4; frame++) gate.Observe("slot1:ABC", "actors1", Layout, true, frame * .5f, frame);
        Require(!gate.Observe("slot2:ABC", "actors2", Layout, true, 2f, 4), "Identical card IDs in a new native bucket still need settling");
        Require(!gate.Observe("slot2:ABC", "actors3", Layout, true, 2.5f, 5), "A server refresh replaces actors and restarts settling");
        for (var frame = 6; frame < 9; frame++)
        {
            var ready = gate.Observe("slot2:ABC", "actors3", Layout, true, 2.5f + (frame - 5) * .5f, frame);
            Require(ready == (frame == 8), "A legitimate repeated card bucket can proceed with new stable visuals");
        }
    }

    private static void VeryLowFrameRatesCanStillComplete()
    {
        var gate = new ArenaDraftVisualGate();
        Require(!gate.Observe("slot1", "actors", Layout, true, 0f, 0), "First slow frame is insufficient");
        Require(!gate.Observe("slot1", "actors", Layout, true, 4f, 1), "Two slow frames remain insufficient");
        Require(!gate.Observe("slot1", "actors", Layout, true, 8f, 2), "Three slow frames remain insufficient");
        Require(gate.Observe("slot1", "actors", Layout, true, 12f, 3),
            "An extremely slow machine must be able to finish after four actually rendered stable frames");
    }

    private static void RefreshDoesNotStarveSlowLoads()
    {
        var budget = new ArenaDraftWaitBudget();
        budget.Observe("slot1", "0 actors", 0f);
        for (var seconds = 1; seconds <= 59; seconds++)
            Require(!budget.IsRefreshDue(seconds), "Slow asset loading must not be destroyed by early refreshes");
        budget.Observe("slot1", "1 actor", 59f);
        Require(!budget.IsRefreshDue(100f), "Real actor-loading progress gets another uninterrupted minute");
        budget.Observe("slot1", "3 actors", 110f);
        Require(!budget.IsRefreshDue(169f), "A later actor-loading step renews its grace period");
        Require(budget.IsRefreshDue(170f), "A full minute of no progress permits one native refresh");
    }

    private static void RefreshIsBoundedAndSlowsDown()
    {
        var budget = new ArenaDraftWaitBudget();
        budget.Observe("slot1", "no ACK", 0f);
        Require(budget.IsRefreshDue(60f), "First refresh is due after 60s");
        budget.Refreshed(60f);
        Require(!budget.IsRefreshDue(179f), "Second refresh must back off to 120s");
        Require(budget.IsRefreshDue(180f), "Second refresh becomes due after its backoff");
        budget.Refreshed(180f);
        Require(!budget.IsRefreshDue(359f), "Third refresh must back off to 180s");
        Require(budget.IsRefreshDue(360f), "Third refresh becomes due after its backoff");
        budget.Refreshed(360f);
        Require(!budget.IsRefreshDue(10000f), "Refreshes cannot loop forever on a rejected or stale slot");
        Require(!budget.IsExhausted(419f), "Last refresh gets time to return");
        Require(budget.IsExhausted(420f), "Persistent failure eventually produces a bounded stop");
        budget.Observe("slot2", "next bucket", 421f);
        Require(budget.Refreshes == 0 && !budget.IsExhausted(421f), "An actual new slot starts a fresh recovery budget");
    }

    private static void ThinkingPaceHasAReadableMinimum()
    {
        var random = new Random(123);
        for (var i = 0; i < 100; i++)
        {
            var delay = ArenaDraftPacing.NextDelay(true, random);
            Require(delay >= 2f && delay <= 3f, "Human draft reading delay is 2–3s after layout readiness");
        }
        Require(!ArenaDraftPacing.IsReady(1, 2), "Thinking delay cannot complete early");
        Require(ArenaDraftPacing.IsReady(2, 2), "Thinking delay completes at its deadline");
    }

    private static void ModeSwitchRequiresNativeViewAndSession()
    {
        Require(ArenaModeNavigationPolicy.Decide(false, true, true, false, false, true, true) ==
            ArenaModeNavigationAction.ToggleNative, "A loaded underground landing page uses its native toggle to enter normal mode");
        Require(ArenaModeNavigationPolicy.Decide(true, false, false, false, false, true, true) ==
            ArenaModeNavigationAction.ToggleNative, "The reverse mode switch uses the same native flow");
        Require(ArenaModeNavigationPolicy.Decide(false, false, true, true, true, true, true) ==
            ArenaModeNavigationAction.RecreatePage, "Changing only DraftManager must not make an old underground view acceptable");
        Require(ArenaModeNavigationPolicy.Decide(true, true, false, true, true, true, true) ==
            ArenaModeNavigationAction.RecreatePage, "The opposite stale page also needs native reconstruction");
        Require(ArenaModeNavigationPolicy.Decide(false, true, true, false, false, false, true) ==
            ArenaModeNavigationAction.RecreatePage, "A draft or reward subpage must not receive the landing-only toggle");
        Require(ArenaModeNavigationPolicy.Decide(false, false, false, false, true, true, true) ==
            ArenaModeNavigationAction.Wait, "Correct mode flags do not excuse the wrong bound native session");
        Require(ArenaModeNavigationPolicy.Decide(false, false, false, true, false, true, true) ==
            ArenaModeNavigationAction.Wait, "A stale client mode must settle before drafting or buying is enabled");

        // Cover every boolean combination: animation/loading always wins, and
        // Ready can only be reached when manager, visible model and session agree.
        for (var mask = 0; mask < 128; mask++)
        {
            var target = (mask & 1) != 0;
            var manager = (mask & 2) != 0;
            var view = (mask & 4) != 0;
            var bound = (mask & 8) != 0;
            var client = (mask & 16) != 0;
            var landing = (mask & 32) != 0;
            var stable = (mask & 64) != 0;
            var result = ArenaModeNavigationPolicy.Decide(target, manager, view, bound, client, landing, stable);
            Require(stable || result == ArenaModeNavigationAction.Wait,
                "No toggle, rebuild or ready result is allowed during an unfinished native transition");
            Require((result == ArenaModeNavigationAction.Ready) ==
                (stable && manager == target && view == target && bound && client),
                "Ready is exactly the stable, fully bound target mode");
        }

        var gate = new ArenaDraftVisualGate();
        for (var frame = 0; frame < 4; frame++)
        {
            var stable = gate.Observe("normal", "normal-session:Normal_Landing", Layout, true, frame * .5f, frame);
            Require((ArenaModeNavigationPolicy.Decide(false, false, false, true, true, true, stable) ==
                ArenaModeNavigationAction.Ready) == (frame == 3),
                "The target session must be stable over four rendered observations, not just assigned in one tick");
        }
        var changed = gate.Observe("underground", "underground-session:Underground_Landing", Layout, true, 2f, 4);
        Require(ArenaModeNavigationPolicy.Decide(true, true, true, true, true, true, changed) ==
            ArenaModeNavigationAction.Wait, "Changing the chosen mode rearms visual settling before the next action");
    }

    private static void NavigationFailuresStillCompleteSafeStops()
    {
        Require(ArenaModeNavigationPolicy.CanCompleteStop(true, "DRAFT", "DRAFT", false, false, false, true),
            "A failed navigation gate must still consume its stop request on a proven safe draft page");
        Require(ArenaModeNavigationPolicy.CanCompleteStop(true, "HUB", "HUB", false, false, false, true),
            "A locked mode rejected before entering draft must also release the runtime");
        Require(ArenaModeNavigationPolicy.CanCompleteStop(true, "DRAFT", "INVALID", false, false, false, true),
            "Native INVALID next-scene means no transition is queued, as seen in stable live draft logs");
        Require(!ArenaModeNavigationPolicy.CanCompleteStop(true, "DRAFT", "UNKNOWN", false, false, false, true),
            "An unreadable next-scene is not the native no-transition sentinel");
        Require(!ArenaModeNavigationPolicy.CanCompleteStop(false, "DRAFT", "DRAFT", false, false, false, true),
            "Merely waiting for a page must not stop a running task");
        Require(!ArenaModeNavigationPolicy.CanCompleteStop(true, "GAMEPLAY", "GAMEPLAY", false, false, false, true),
            "A game scene can never be a navigation stop boundary");
        Require(!ArenaModeNavigationPolicy.CanCompleteStop(true, "DRAFT", "GAMEPLAY", false, false, false, true),
            "The scene before entering an already queued game is not safe");
        Require(!ArenaModeNavigationPolicy.CanCompleteStop(true, "DRAFT", "DRAFT", true, false, false, true),
            "Native matchmaking or connection state must finish first");
        Require(!ArenaModeNavigationPolicy.CanCompleteStop(true, "DRAFT", "DRAFT", false, true, false, true),
            "An existing game-server connection must not be mistaken for idle draft");
        Require(!ArenaModeNavigationPolicy.CanCompleteStop(true, "DRAFT", "DRAFT", false, false, true, true),
            "The native reward flow remains enabled despite a navigation failure");
        Require(!ArenaModeNavigationPolicy.CanCompleteStop(true, "DRAFT", "DRAFT", false, false, false, false),
            "An unknown native arena state cannot certify a stop boundary");
    }

    private static void DraftEntryUsesNativePageBeforeCachedChoices()
    {
        foreach (var underground in new[] { false, true })
        {
            var prefix = underground ? "Underground_" : "Normal_";
            // Both a newly accepted ticket and restarting on a slot-zero
            // deck expose DRAFTING before the native landing Continue event.
            foreach (var scenario in new[] { "new run", "restart with cached heroes" })
            {
                Require(ArenaDraftEntryPolicy.Decide("DRAFTING", prefix + "Landing", underground,
                    true, true, false, false) == ArenaDraftEntryAction.OpenDraftPage,
                    scenario + ": cached choices do not replace native MOVETO_DRAFTING");
                Require(ArenaDraftEntryPolicy.Decide("DRAFTING", prefix + "Landing", underground,
                    true, false, true, true) == ArenaDraftEntryAction.Wait,
                    scenario + ": an unready landing widget cannot receive the entry event");
                Require(ArenaDraftEntryPolicy.Decide("DRAFTING", prefix + "Draft", underground,
                    true, true, false, true) == ArenaDraftEntryAction.Wait,
                    scenario + ": setting the client enum does not prove the selection screen was shown");
                Require(ArenaDraftEntryPolicy.Decide("DRAFTING", prefix + "Draft", underground,
                    true, true, true, false) == ArenaDraftEntryAction.Wait,
                    scenario + ": entry animations must settle before requesting/using choices");
                Require(ArenaDraftEntryPolicy.Decide("DRAFTING", prefix + "Draft", underground,
                    true, true, true, true) == ArenaDraftEntryAction.Ready,
                    scenario + ": native shown stable Draft page allows choices without repeated entry");
            }
            Require(ArenaDraftEntryPolicy.Decide("REDRAFTING", prefix + "Landing", underground,
                true, true, true, true) == ArenaDraftEntryAction.WaitForNativeRedraft,
                "REDRAFTING must retain TriggerRedraft(false), never ordinary continuation");
            Require(ArenaRedraftPolicy.ShouldOpenPage("REDRAFTING", prefix + "Landing"),
                "Existing native redraft policy still handles a restarted redraft landing page");
            Require(ArenaDraftEntryPolicy.Decide("REDRAFTING", prefix + "Redraft", underground,
                true, true, true, true) == ArenaDraftEntryAction.Ready,
                "Native redraft page can continue after its dedicated entry");
            Require(ArenaDraftEntryPolicy.Decide("REDRAFTING", prefix + "Draft", underground,
                true, true, true, true) == ArenaDraftEntryAction.Wait,
                "Ordinary Draft cannot stand in for the distinct Redraft client page");
            Require(ArenaDraftEntryPolicy.Decide("DRAFTING", prefix + "Drafting", underground,
                true, true, true, true) == ArenaDraftEntryAction.Wait,
                "The exact native enum names must be used, not invented Drafting names");
            Require(ArenaDraftEntryPolicy.Decide("DRAFTING", (underground ? "Normal_" : "Underground_") + "Landing",
                underground, true, true, true, true) == ArenaDraftEntryAction.Wait,
                "A different mode's landing page cannot continue the selected session");
            Require(ArenaDraftEntryPolicy.Decide("DRAFTING", prefix + "Landing", underground,
                false, true, true, true) == ArenaDraftEntryAction.Wait,
                "A manager/session mode mismatch must block native entry");
            var gate = new ArenaDraftVisualGate();
            for (var frame = 0; frame < 4; frame++)
            {
                var stable = gate.Observe("run1", prefix + "Draft", Layout, true, frame * .5f, frame);
                Require((ArenaDraftEntryPolicy.Decide("DRAFTING", prefix + "Draft", underground,
                    true, true, true, stable) == ArenaDraftEntryAction.Ready) == (frame == 3),
                    "Native shown page requires the original distinct-frame visual settling gate");
            }
        }
        foreach (var state in new[] { "NO_RUN", "INVALID", "MIDRUN", "MIDRUN_REDRAFT_PENDING", "REWARDS", "EDITING_DECK" })
            Require(!ArenaDraftEntryPolicy.IsSelectionState(state), "Other phases retain their own native actions");
    }

    private static void DraftEntryRetriesAreBoundedAndPageSpecific()
    {
        var retry = new ArenaDraftEntryRetry();
        retry.Observe("U:deck1:display1:DRAFTING");
        Require(retry.CanRequest(0f), "A new landing page permits its first native continuation");
        retry.Requested(0f);
        Require(!retry.CanRequest(7.9f), "An accepted or throwing transition is not retried every frame");
        Require(retry.CanRequest(8f), "A dropped native page event can be retried after its bounded grace");
        retry.Requested(8f);
        Require(retry.CanRequest(16f), "A second native continuation can recover a dropped event");
        retry.Requested(16f);
        Require(!retry.CanRequest(100f), "Native continuation cannot loop forever");
        Require(!retry.IsExhausted(23.9f) && retry.IsExhausted(24f), "The final event gets its response grace before stopping");
        retry.Observe("U:deck1:display2:DRAFTING");
        Require(retry.Attempts == 0 && retry.CanRequest(25f), "Restarting creates a new actual display, not a permanent once-key lock");
        retry.Requested(25f);
        retry.Observe("U:deck2:display2:DRAFTING");
        Require(retry.Attempts == 0 && retry.CanRequest(26f), "A new server deck has its own entry gate");
        retry.Reset();
        Require(!retry.CanRequest(27f), "Disabled/reset automation cannot keep requesting a captured page");
    }

    private static void NativeTicketEntryRequiresAnAuthoritativeEmptyRun()
    {
        Require(ArenaDraftEntryPolicy.CanUseTicketEntry("NO_RUN", false, false, 6, 2, true, true, true),
            "A verified empty ready underground landing with enough tickets can use native SPEND_TICKET");
        Require(ArenaDraftEntryPolicy.CanUseTicketEntry("NO_RUN", false, false, 1, 1, true, true, true),
            "The normal arena entry uses the same native flow with its actual ticket cost");
        foreach (var state in new[] { "DRAFTING", "REDRAFTING", "INVALID", "EDITING_DECK", "MIDRUN", "REWARDS", "IN_REWARDS" })
            Require(!ArenaDraftEntryPolicy.CanUseTicketEntry(state, false, false, 6, 2, true, true, true),
                "Cached/zero-slot/partial state " + state + " cannot be another paid entry");
        Require(!ArenaDraftEntryPolicy.CanUseTicketEntry("NO_RUN", true, false, 6, 2, true, true, true),
            "Native active session blocks spending despite an empty local ledger");
        Require(!ArenaDraftEntryPolicy.CanUseTicketEntry("NO_RUN", false, true, 6, 2, true, true, true),
            "A tracked unclosed run blocks spending during transient server NO_RUN");
        Require(!ArenaDraftEntryPolicy.CanUseTicketEntry("NO_RUN", false, false, 1, 2, true, true, true),
            "Insufficient tickets must retain the separate validated GOLD purchase flow");
        Require(!ArenaDraftEntryPolicy.CanUseTicketEntry("NO_RUN", false, false, 6, 2, false, true, true),
            "An unready/animating landing widget cannot spend tickets");
        Require(!ArenaDraftEntryPolicy.CanUseTicketEntry("NO_RUN", false, false, 6, 2, true, false, true),
            "A mismatched native mode cannot spend tickets");
        Require(!ArenaDraftEntryPolicy.CanUseTicketEntry("NO_RUN", false, false, 6, 2, true, true, false),
            "A manager flag alone cannot replace a stable verified empty server session");
        Require(ArenaDraftEntryPolicy.IsEntryPersisted(true, 1234, true, 1234, 1234, true),
            "The exact underground entry timestamp must be present in both durable projections before spending");
        Require(ArenaDraftEntryPolicy.IsEntryPersisted(false, 1234, false, 1234, 1234, true),
            "Normal mode verifies its own persisted entry context");
        Require(!ArenaDraftEntryPolicy.IsEntryPersisted(true, 1234, true, 1233, 1233, true),
            "A silently failed save leaving an older file cannot authorize a ticket event");
        Require(!ArenaDraftEntryPolicy.IsEntryPersisted(true, 1234, false, 1234, 1234, true),
            "A saved different-mode entry cannot authorize spending");
        Require(!ArenaDraftEntryPolicy.IsEntryPersisted(true, 1234, true, 1234, 0, true),
            "A missing per-mode entry latch cannot authorize spending");
        Require(!ArenaDraftEntryPolicy.IsEntryPersisted(true, 1234, true, 1234, 1234, false),
            "Uninitialized mode contexts cannot authorize spending");
        Require(!ArenaDraftEntryPolicy.IsEntryPersisted(true, 0, true, 0, 0, true),
            "An empty old statistics file is not a persisted admission request");
    }

    private static void Require(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
