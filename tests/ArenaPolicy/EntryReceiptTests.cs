using ArenaBoxBot;
using Newtonsoft.Json.Linq;

internal static class EntryReceiptTests
{
    private const string OldKey = "underground:151:2770235581";
    private const string NewKey = "underground:151:2770485547";
    private const string OtherKey = "underground:151:2770999999";
    private static int assertions;

    internal static void Run()
    {
        LongHeroWaitAndRestart();
        UnknownMarkersAndManualRuns();
        ModeIsolation();
        ReadBackFailures();
        FreshEmptyAcknowledgement();
        BoundedRecoveryWithoutSession();
        IndependentPredecessors();
        BeginResponseWithStaleSessionState();
        ResumeOlderAcceptedHeroDraft();
        Console.WriteLine($"PASS: {assertions} entry receipt assertions: exact identity, long hero waits, mode isolation, durable IO and fresh empty acknowledgement");
    }

    internal static void ConfirmEntry(ArenaStatsData data, string key, long requestAt)
    {
        var nonce = Guid.NewGuid().ToString("N");
        Check(ArenaStatsLedger.PrepareEntry(data, nonce, data.EmptyRunPreviousKey, 8,
            data.RunUnderground ? 2 : 1, requestAt), "Automatic entry requires fresh same-mode empty proof");
        data = RoundTripInPlace(data);
        Check(ArenaStatsLedger.MarkEntrySubmission(data, nonce), "Spending lock is armed before financial event");
        data = RoundTripInPlace(data);
        Check(ArenaStatsLedger.MarkEntryAccepted(data, nonce), "Only own native submission return may accept receipt");
        data = RoundTripInPlace(data);
        Check(ArenaStatsLedger.ObserveEntryResponse(data, data.RunUnderground, key, "DRAFTING", true, false,
            0, 0, 0, 30, requestAt + 100), "Actual native beginning response fixes exact mode/deck within short window");
        Check(ArenaStatsLedger.BindEntryResponse(data, key, "DRAFTING", true, false, 0, 0, 0, 30,
            true, requestAt + 3100), "Stable same native identity binds before hero selection");
    }

    private static ArenaStatsData Original()
    {
        var data = new ArenaStatsData();
        ArenaStatsLedger.SelectMode(data, true);
        data.RunKey = OldKey;
        data.LastCompletedRunKey = OldKey;
        ArenaStatsLedger.ObserveEmptyRun(data, OldKey, 100000);
        return data;
    }

    private static ArenaStatsData Accepted()
    {
        var data = Original();
        Check(ArenaStatsLedger.PrepareEntry(data, Guid.NewGuid().ToString("N"), OldKey, 8, 2, 100100), "Prepare own request");
        Check(ArenaStatsLedger.MarkEntrySubmission(data, data.EntryReceipt.Nonce), "Write-ahead lock");
        Check(ArenaStatsLedger.MarkEntryAccepted(data, data.EntryReceipt.Nonce), "Native submission confirmed");
        return Restart(data);
    }

    private static void BeginResponseWithStaleSessionState()
    {
        foreach (var underground in new[] { false, true })
        {
            var data = new ArenaStatsData();
            ArenaStatsLedger.SelectMode(data, underground);
            var key = (underground ? "underground:" : "normal:") + "151:9531205242";
            ArenaStatsLedger.ObserveEmptyRun(data, "", 100000);
            Check(ArenaStatsLedger.PrepareEntry(data, Guid.NewGuid().ToString("N"), "", 2, 1, 100100), "Prepare actual new entry");
            Check(ArenaStatsLedger.MarkEntrySubmission(data, data.EntryReceipt.Nonce) &&
                ArenaStatsLedger.MarkEntryAccepted(data, data.EntryReceipt.Nonce), "Capture own native submission");
            var accepted = Restart(data);
            foreach (var manager in new[] { "NO_RUN", "REWARDS", "REDRAFTING", "UNKNOWN" })
                Check(!ArenaStatsLedger.ObserveEntryResponse(Restart(accepted), underground, key,
                    "NO_RUN", true, false, 0, 0, 0, 30, 100374, manager, 3), "Cached session alone cannot capture begin: " + manager);
            Check(!ArenaStatsLedger.ObserveEntryResponse(Restart(accepted), underground, key,
                "NO_RUN", true, false, 0, 0, 0, 30, 100374, "DRAFTING", 0), "Missing hero choices is not a verified native begin");
            Check(!ArenaStatsLedger.ObserveEntryResponse(Restart(accepted), underground, key,
                "NO_RUN", false, false, 0, 0, 0, 30, 100374, "DRAFTING", 3), "Inactive session is never admission");
            Check(!ArenaStatsLedger.ObserveEntryResponse(Restart(accepted), underground, key,
                "NO_RUN", true, true, 0, 0, 0, 30, 100374, "DRAFTING", 3), "Rewards cannot become an admission");
            Check(ArenaStatsLedger.ObserveEntryResponse(data, underground, key,
                "NO_RUN", true, false, 0, 0, 0, 30, 100374, "DRAFTING", 3), "Real Begin with delayed Session.State retains exact new identity");
            data = Restart(data);
            Check(ArenaStatsLedger.EntryDraftState(data.EntryReceipt, key + "x", "NO_RUN", "DRAFTING", true, false) == "NO_RUN",
                "Projection cannot transfer response ownership to a different deck");
            Check(ArenaStatsLedger.EntryDraftState(data.EntryReceipt, key, "NO_RUN", "REWARDS", true, false) == "NO_RUN",
                "Projection requires current manager draft state");
            var state = ArenaStatsLedger.EntryDraftState(data.EntryReceipt, key, "NO_RUN", "DRAFTING", true, false);
            Check(ArenaStatsLedger.BindEntryResponse(data, key, state, true, false, 0, 0, 0, 30, true, 200000),
                "Hero loading past response TTL binds the retained exact response");
            data = Restart(data);
            state = ArenaStatsLedger.EntryDraftState(data.EntryReceipt, key, "NO_RUN", "DRAFTING", true, false);
            Check(state == "DRAFTING" && ArenaStatsLedger.StartRun(data, key, 0, 0,
                ArenaStatsLedger.HasNewRunEvidence(data, key, 201000)) && data.RunCount == 1,
                "Delayed session state no longer loses actual new-run count");
            Check(!ArenaStatsLedger.PrepareEntry(data, Guid.NewGuid().ToString("N"), "", 2, 1, 201001), "Active run prevents another ticket submission");
        }
    }

    private static void ResumeOlderAcceptedHeroDraft()
    {
        var accepted = Accepted();
        foreach (var slot in new[] { 1, 15, 30 })
        {
            var progressed = Restart(accepted);
            Check(ArenaStatsLedger.AttachPendingExistingDraft(progressed, NewKey, "DRAFTING", "DRAFTING",
                true, false, 0, 0, slot, 30, true), "Old accepted draft remains resumable after manual hero/card selection: " + slot);
            progressed = Restart(progressed);
            Check(progressed.RunCount == 0 && progressed.RunActive && progressed.EntryReceipt.Phase == "attached-uncredited",
                "Resumed progressed draft never invents payment ownership after restart");
        }
        foreach (var slot in new[] { -1, 31 })
            Check(!ArenaStatsLedger.AttachPendingExistingDraft(Restart(accepted), NewKey, "DRAFTING", "DRAFTING",
                true, false, 0, 0, slot, 30, true), "Invalid slot cannot resume old accepted receipt");
        Check(!ArenaStatsLedger.AttachPendingExistingDraft(Restart(accepted), NewKey, "NO_RUN", "DRAFTING",
            true, false, 0, 0, 0, 30, false), "Transient hero snapshot cannot recover old accepted record");
        foreach (var key in new[] { OldKey, "normal:151:77", "" })
            Check(!ArenaStatsLedger.AttachPendingExistingDraft(Restart(accepted), key, "NO_RUN", "DRAFTING",
                true, false, 0, 0, 0, 30, true), "Old/opposite/missing native identity cannot resume: " + key);
        foreach (var state in new[] { "REWARDS", "MIDRUN", "UNKNOWN" })
            Check(!ArenaStatsLedger.AttachPendingExistingDraft(Restart(accepted), NewKey, state, "DRAFTING",
                true, false, 0, 0, 0, 30, true), "Recovery never overrides non-initial session state: " + state);
        Check(!ArenaStatsLedger.AttachPendingExistingDraft(Restart(accepted), NewKey, "NO_RUN", "DRAFTING",
            false, false, 0, 0, 0, 30, true), "No active server run keeps spending lock");
        Check(!ArenaStatsLedger.AttachPendingExistingDraft(Restart(accepted), NewKey, "NO_RUN", "DRAFTING",
            true, true, 0, 0, 0, 30, true), "Pending rewards keep lock");
        Check(ArenaStatsLedger.AttachPendingExistingDraft(accepted, NewKey, "NO_RUN", "DRAFTING",
            true, false, 0, 0, 0, 30, true), "Old accepted receipt can resume stable active hero draft without spending");
        var nonce = accepted.EntryReceipt.Nonce;
        accepted = Restart(accepted);
        Check(accepted.RunActive && accepted.RunKey == NewKey && accepted.RunCount == 0 &&
            accepted.EntryReceipt.Phase == "attached-uncredited" && accepted.EntryReceipt.Nonce == nonce &&
            accepted.EntryReceipt.ResponseRunKey == "" && accepted.GoldSpent == 0,
            "Recovery preserves nonce/history and does not fabricate response, payment or counted run");
        Check(!ArenaStatsLedger.PrepareEntry(accepted, Guid.NewGuid().ToString("N"), OldKey, 8, 2, 9999999),
            "Recovered live draft cannot spend again after restart");
    }

    private static void LongHeroWaitAndRestart()
    {
        var data = Accepted();
        Check(ArenaStatsLedger.ObserveEntryResponse(data, true, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, 100200), "Capture real begin response at slot zero");
        data = Restart(data);
        Check(!ArenaStatsLedger.HasNewRunEvidence(data, NewKey, 100201), "Unstable response is not credited");
        Check(!ArenaStatsLedger.BindEntryResponse(data, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, false, 103200), "Unstable native scene cannot bind");
        Check(!ArenaStatsLedger.BindEntryResponse(data, OtherKey, "DRAFTING", true, false,
            0, 0, 0, 30, true, 103200), "Response cannot bind a different later manual deck");
        // A restart can delay stability itself. The captured response key,
        // rather than a generic time marker, remains the only candidate.
        Check(ArenaStatsLedger.BindEntryResponse(data, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, true, 1960200), "Exact response identity survives 31 minute loading delay");
        data = Restart(data);
        Check(ArenaStatsLedger.HasNewRunEvidence(data, NewKey, 3760200), "Bound ownership survives another long hero wait and restart");
        Check(!ArenaStatsLedger.HasNewRunEvidence(data, OtherKey, 3760200), "Bound proof owns one key only");
        Check(ArenaStatsLedger.StartRun(data, NewKey, 0, 0,
            ArenaStatsLedger.HasNewRunEvidence(data, NewKey, 3760200)), "Real first slot advance may attach bound run");
        Check(data.RunCount == 1 && data.CountedRunKeys.SequenceEqual(new[] { NewKey }) &&
            data.RunOrigin == "observed-new-entry" && data.EntryReceipt.Phase == "credited", "Count once with durable exact identity");
        data = Restart(data);
        Check(!ArenaStatsLedger.StartRun(data, NewKey, 0, 0, true) && data.RunCount == 1, "Same active run cannot count after restart");
        data.RunActive = false;
        data.LastCompletedRunKey = OtherKey;
        Check(ArenaStatsLedger.StartRun(data, NewKey, 2, 1, true) && data.RunCount == 1 &&
            data.CurrentRunWins == 2 && data.TotalWins == 0, "Historical identity remains deduplicated after later completed key replaces last key");
    }

    private static void UnknownMarkersAndManualRuns()
    {
        var data = Original();
        data.EntryRequestedAtUnixMs = 100100;
        Check(!ArenaStatsLedger.HasNewRunEvidence(data, NewKey, 100200), "Legacy empty proof/time marker cannot credit a manual run");
        var accepted = Accepted();
        foreach (var phase in new[] { "prepared", "submission-uncertain", "resolved-empty", "credited" })
        {
            var copy = Restart(accepted);
            copy.EntryReceipt.Phase = phase;
            Check(!ArenaStatsLedger.ObserveEntryResponse(copy, true, NewKey, "DRAFTING", true, false,
                0, 0, 0, 30, 100200), "No own native submission proof: " + phase);
            Check(!ArenaStatsLedger.HasNewRunEvidence(copy, NewKey, 100200), "No credit for uncertain/failed/crash-before-event state: " + phase);
        }
        foreach (var time in new long[] { 100099, 190101, long.MaxValue })
            Check(!ArenaStatsLedger.ObserveEntryResponse(Restart(accepted), true, NewKey, "DRAFTING", true, false,
                0, 0, 0, 30, time), "Late/future-dated request evidence cannot claim a new manual response");
        Check(!ArenaStatsLedger.ObserveEntryResponse(accepted, true, OldKey, "DRAFTING", true, false,
            0, 0, 0, 30, 100200), "Old predecessor is never a new entry");
        Check(!ArenaStatsLedger.ObserveEntryResponse(accepted, true, NewKey, "REDRAFTING", true, false,
            0, 0, 0, 30, 100200), "Redraft callback cannot become a new paid run");
        Check(!ArenaStatsLedger.ObserveEntryResponse(accepted, true, NewKey, "DRAFTING", false, false,
            0, 0, 0, 30, 100200), "Inactive response is not admission");
        Check(!ArenaStatsLedger.ObserveEntryResponse(accepted, true, NewKey, "DRAFTING", true, true,
            0, 0, 0, 30, 100200), "Reward response cannot prove a new entry");
        Check(!ArenaStatsLedger.ObserveEntryResponse(accepted, true, NewKey, "DRAFTING", true, false,
            1, 0, 0, 30, 100200), "Already played deck is not initial native begin response");
        Check(!ArenaStatsLedger.ObserveEntryResponse(accepted, true, NewKey, "DRAFTING", true, false,
            0, 0, 1, 30, 100200), "Beginning hook must capture initial slot, not cached later snapshot");
        var changedPredecessor = Restart(accepted);
        changedPredecessor.RunKey = OtherKey;
        Check(!ArenaStatsLedger.ObserveEntryResponse(changedPredecessor, true, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, 100200), "A changed tracked predecessor invalidates the pending ownership claim");
        Check(!ArenaStatsLedger.PrepareEntry(accepted, Guid.NewGuid().ToString("N"), OldKey, 8, 2, 100300),
            "An unresolved lock prohibits another financial request regardless of marker age");
    }

    private static void ModeIsolation()
    {
        var data = Accepted();
        ArenaStatsLedger.SelectMode(data, false);
        data = Restart(data);
        Check(!ArenaStatsLedger.HasUnresolvedEntry(data), "Other mode does not inherit entry lock");
        Check(ArenaStatsLedger.OtherUnresolvedFinancialEntry(data)?.Nonce == data.UndergroundRun.EntryReceipt.Nonce,
            "Shared ticket balance retains a global hold while the other mode's admission is unbound");
        ArenaStatsLedger.ObserveEmptyRun(data, "", 100200);
        Check(!ArenaStatsLedger.PrepareEntry(data, Guid.NewGuid().ToString("N"), "", 8, 1, 100201),
            "Changing modes cannot submit another financial event while the prior entry remains unresolved");
        Check(!ArenaStatsLedger.ObserveEntryResponse(data, false, "normal:151:77", "DRAFTING", true, false,
            0, 0, 0, 30, 100200), "Normal response cannot own underground request");
        Check(ArenaStatsLedger.ObserveEntryResponse(data, true, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, 100200), "Exact underground response may be captured while normal projection selected");
        Check(data.EntryReceipt.Phase == "" && data.UndergroundRun.EntryReceipt.ResponseRunKey == NewKey,
            "Response changes only its own mode context");
        data = Restart(data);
        ArenaStatsLedger.SelectMode(data, true);
        Check(ArenaStatsLedger.BindEntryResponse(data, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, true, 103200), "Returning to original mode restores exact response");
        data = Restart(data);
        ArenaStatsLedger.SelectMode(data, false);
        Check(!ArenaStatsLedger.HasNewRunEvidence(data, NewKey, 1000000), "Bound opposite-mode identity cannot count under selected normal mode");
        ArenaStatsLedger.SelectMode(data, true);
        Check(ArenaStatsLedger.HasNewRunEvidence(data, NewKey, 1000000), "Bound owner survives repeated mode switches");
    }

    private static void ReadBackFailures()
    {
        var data = Accepted();
        var expected = data.EntryReceipt.Copy();
        var json = ArenaStatsPersistence.Write(data);
        Check(ArenaStatsPersistence.SaveAndConfirmEntryReceipt(() => true, () => json, true, expected, out _),
            "Exact production serializer read-back permits financial progression");
        var read = false;
        Check(!ArenaStatsPersistence.SaveAndConfirmEntryReceipt(() => false, () => { read = true; return json; }, true,
            expected, out _) && !read, "Write failure blocks immediately; old readable payload cannot disguise failure");
        Check(!ArenaStatsPersistence.SaveAndConfirmEntryReceipt(() => true,
            () => throw new IOException("simulated sharing/access failure"), true, expected, out var failure) &&
            failure.Contains("IOException"), "Read failure preserves error and denies financial progression");
        Check(!ArenaStatsPersistence.SaveAndConfirmEntryReceipt(() => true, () => "{", true, expected, out _),
            "Unreadable/partial JSON cannot authorize spending");
        foreach (var field in new[] { "Nonce", "Phase", "PreviousRunKey", "PreviousNativeRunKey", "ResponseRunKey", "BoundRunKey" })
        {
            var broken = JObject.Parse(json);
            broken["UndergroundRun"]!["EntryReceipt"]![field] = "mismatch";
            Check(!ArenaStatsPersistence.SaveAndConfirmEntryReceipt(() => true, () => broken.ToString(), true, expected, out _),
                "Nested mode proof must match: " + field);
        }
        var projectionBroken = JObject.Parse(json);
        projectionBroken["EntryReceipt"]!["Nonce"] = "wrong-projection";
        Check(!ArenaStatsPersistence.SaveAndConfirmEntryReceipt(() => true, () => projectionBroken.ToString(), true, expected, out _),
            "Selected projection cannot contradict nested mode proof");
        Check(!ArenaStatsPersistence.IsEntryReceiptPersisted(Restart(data), false, expected), "Expected receipt mode must match requested mode");
        var legacy = ArenaStatsPersistence.Read("{\"CountedRunKeys\":null,\"EntryReceipt\":null}", out _);
        Check(legacy.CountedRunKeys != null && legacy.EntryReceipt != null &&
            !ArenaStatsLedger.HasNewRunEvidence(legacy, NewKey, 100200), "Legacy/null fields recover safely without minting ownership");
    }

    private static void FreshEmptyAcknowledgement()
    {
        var data = Accepted();
        data.EntryReceipt.Phase = "submission-uncertain";
        data = Restart(data);
        Check(!ArenaStatsLedger.ResolveEntryAsEmpty(data, true, 9999999), "Cached stable NO_RUN never releases a financial lock by time alone");
        Check(!ArenaStatsLedger.ObserveEntryEmptyAcknowledgement(data, true, "NO_RUN", false, false, 190000),
            "A response before the minimum pending window does not settle late admission");
        Check(!ArenaStatsLedger.ObserveEntryEmptyAcknowledgement(data, false, "NO_RUN", false, false, 200000),
            "Other mode NO_RUN acknowledgement does not release owner lock");
        Check(ArenaStatsLedger.ObserveEntryEmptyAcknowledgement(data, true, "NO_RUN", false, false, 200000),
            "Actual fresh owner-mode response after pending window starts final confirmation");
        data = Restart(data);
        Check(!ArenaStatsLedger.ResolveEntryAsEmpty(data, true, 209999), "Fresh empty response must remain corroborated at least 10 seconds");
        Check(!ArenaStatsLedger.ResolveEntryAsEmpty(data, false, 210000), "Conflicting live session prevents release even with old ACK");
        Check(ArenaStatsLedger.ObserveEntryEmptyAcknowledgement(data, true, "DRAFTING", true, false, 210000) &&
            data.EntryReceipt.EmptyAcknowledgedAtUnixMs == 0, "Later active response invalidates empty acknowledgement");
        Check(!ArenaStatsLedger.ResolveEntryAsEmpty(data, true, 300000), "Invalidated ACK never settles by age alone");
        Check(ArenaStatsLedger.ObserveEntryEmptyAcknowledgement(data, true, "NO_RUN", false, false, 300000), "Fresh empty acknowledgement after contradiction");
        Check(ArenaStatsLedger.ResolveEntryAsEmpty(data, true, 310000) && data.EntryReceipt.Phase == "resolved-empty" &&
            !ArenaStatsLedger.HasUnresolvedEntry(data) && data.RunCount == 0 && data.EntryRequestedAtUnixMs == 0,
            "Only acknowledged stable empty state releases lock without fabricating spend or run credit");
        Check(!ArenaStatsLedger.ResolveEntryAsEmpty(data, true, 400000), "Repeated acknowledgement cannot re-resolve or credit");
    }

    private static void BoundedRecoveryWithoutSession()
    {
        var data = Accepted();
        data.EntryReceipt.Phase = "submission-uncertain";
        var budget = new ArenaEntryRecoveryBudget();
        Check(budget.Observe(data.EntryReceipt, "UNKNOWN", 190000, 89) == ArenaEntryRecoveryDecision.Wait,
            "Missing session does not shorten minimum response window");
        Check(budget.Observe(data.EntryReceipt, "UNKNOWN", 190100, 90) == ArenaEntryRecoveryDecision.RequestFreshSession,
            "Unloaded session still gets one bounded read-only confirmation");
        Check(budget.Observe(data.EntryReceipt, "UNKNOWN", 191100, 91) == ArenaEntryRecoveryDecision.Wait,
            "Financial recovery never repeatedly refreshes or resubmits");
        Check(budget.Observe(data.EntryReceipt, "NO_RUN", 219100, 119) == ArenaEntryRecoveryDecision.Wait,
            "Thirty second confirmation budget has not expired");
        Check(budget.Observe(data.EntryReceipt, "UNKNOWN", 220100, 120) == ArenaEntryRecoveryDecision.Stop,
            "Missing session cannot leave an uncertain financial event waiting forever");
        Check(budget.Observe(data.EntryReceipt, "NO_RUN", 250100, 150) == ArenaEntryRecoveryDecision.Stop,
            "Timed-out recovery remains stopped and never sends another request");
        budget.Reset();
        Check(data.EntryReceipt.Phase == "submission-uncertain" &&
            budget.Observe(data.EntryReceipt, "DRAFTING", 260100, 160) == ArenaEntryRecoveryDecision.RequestFreshSession,
            "Explicit reactivation starts a fresh read-only confirmation attempt, preserving financial lock");
        Check(budget.Observe(data.EntryReceipt, "DRAFTING", 261100, 161) == ArenaEntryRecoveryDecision.Wait &&
            !ArenaStatsLedger.PrepareEntry(data, Guid.NewGuid().ToString("N"), OldKey, 8, 2, 261100),
            "Reactivation grace cannot submit another ticket entry");
        var bound = Accepted();
        Check(ArenaStatsLedger.ObserveEntryResponse(bound, true, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, 100200), "Known response for healthy long hero wait");
        Check(budget.Observe(bound.EntryReceipt, "DRAFTING", 9999999, 300) == ArenaEntryRecoveryDecision.None,
            "Known exact response during hero loading is not an ambiguous financial wait");
        Check(ArenaStatsLedger.BindEntryResponse(bound, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, true, 103200), "Healthy bound receipt");
        Check(budget.Observe(bound.EntryReceipt, "DRAFTING", 9999999, 600) == ArenaEntryRecoveryDecision.None,
            "Bound hero waiting is never stopped by financial response TTL");
    }

    private static void IndependentPredecessors()
    {
        var clean = new ArenaStatsData();
        ArenaStatsLedger.SelectMode(clean, true);
        ArenaStatsLedger.ObserveEmptyRun(clean, OldKey, 100000);
        ConfirmEntry(clean, NewKey, 100100);
        Check(clean.EntryReceipt.PreviousRunKey == "" && clean.EntryReceipt.PreviousNativeRunKey == OldKey &&
            ArenaStatsLedger.HasNewRunEvidence(clean, NewKey, 103200),
            "Clean ledger may credit an automatic entry even when native NO_RUN retains an old manual deck");
        clean.RunKey = OtherKey;
        Check(!ArenaStatsLedger.HasNewRunEvidence(clean, NewKey, 103200), "A bound receipt is invalidated by a changed ledger predecessor");

        var differing = Original();
        ArenaStatsLedger.ObserveEmptyRun(differing, OtherKey, 100001);
        Check(ArenaStatsLedger.PrepareEntry(differing, Guid.NewGuid().ToString("N"), OtherKey, 8, 2, 100100),
            "Ledger and native old identities are independent admission evidence");
        Check(differing.EntryReceipt.PreviousRunKey == OldKey && differing.EntryReceipt.PreviousNativeRunKey == OtherKey,
            "Both distinct predecessors survive rather than overwriting either one");
        Check(ArenaStatsLedger.MarkEntrySubmission(differing, differing.EntryReceipt.Nonce) &&
            ArenaStatsLedger.MarkEntryAccepted(differing, differing.EntryReceipt.Nonce), "Own submission after independent predecessor preparation");
        differing = Restart(differing);
        foreach (var old in new[] { OldKey, OtherKey })
            Check(!ArenaStatsLedger.ObserveEntryResponse(differing, true, old, "DRAFTING", true, false,
                0, 0, 0, 30, 100200), "Neither predecessor is a new paid identity: " + old);
        Check(ArenaStatsLedger.ObserveEntryResponse(differing, true, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, 100200), "Real new response differs from both predecessor identities");
        Check(ArenaStatsLedger.BindEntryResponse(differing, NewKey, "DRAFTING", true, false,
            0, 0, 0, 30, true, 103200), "Different ledger/native predecessor pair permits a stable real new identity");
    }

    // Keep callers' object identity to exercise ledger operations around the
    // actual production Write/Read pair without replacing a shared reference.
    private static ArenaStatsData RoundTripInPlace(ArenaStatsData data)
    {
        var restarted = Restart(data);
        data.EntryReceipt = restarted.EntryReceipt;
        data.NormalRun = restarted.NormalRun;
        data.UndergroundRun = restarted.UndergroundRun;
        return data;
    }

    private static ArenaStatsData Restart(ArenaStatsData data) => ArenaStatsPersistence.Read(ArenaStatsPersistence.Write(data), out _);
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
