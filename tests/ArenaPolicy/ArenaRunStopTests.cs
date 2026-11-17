using ArenaControl.Contracts;
using ArenaBoxBot;

internal static class ArenaRunStopTests
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string why) { checks++; if (!value) throw new InvalidOperationException(why); }
        foreach (var underground in new[] { false, true })
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var request = new ArenaRunStopRequest { RequestId = Guid.NewGuid().ToString("N"),
                GameProcessId = 123, GameStartedAtUtcTicks = 100, RequestedAtUtcTicks = 200,
                Underground = underground, RunKey = "run-" + cycle };
            Check(ArenaRunStopContract.Matches(request, 123, 100, underground, 300), "Current process/mode request accepted");
            Check(!ArenaRunStopContract.Matches(request, 123, 101, underground, 300), "PID reuse rejected");
            Check(!ArenaRunStopContract.Matches(request, 124, 100, underground, 300), "Old process rejected");
            Check(!ArenaRunStopContract.Matches(request, 123, 100, !underground, 300), "Other Arena type rejected");
            Check(!ArenaRunStopContract.Matches(request, 123, 100, underground, 199), "Future request rejected");
            foreach (var state in new[] { "DRAFTING", "MIDRUN", "MIDRUN_REDRAFT_PENDING", "REDRAFTING", "EDITING_DECK" })
                Check(!ArenaRunStopContract.HoldEntry(state, true), "Continue current paid round: " + state);
            Check(ArenaRunStopContract.HoldEntry("EDITING_DECK", false), "Empty Underground deck page cannot spend for a new round");
            Check(ArenaRunStopContract.HoldEntry("NO_RUN", false), "No next ticket during empty placeholder");
            Check(ArenaRunStopContract.HoldEntry("INVALID", false), "Unknown entry cannot spend");
            Check(!ArenaRunStopContract.CanComplete("GAMEPLAY", "INVALID", false, true, false, false, false, request.RunKey, request.RunKey), "Match completion is not round completion");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, false, false, false, false, request.RunKey, request.RunKey), "Native server proof required");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "HUB", false, true, false, false, false, request.RunKey, request.RunKey), "Transition target blocks completion");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", true, true, false, false, false, request.RunKey, request.RunKey), "Active transition blocks completion");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, true, true, false, false, request.RunKey, request.RunKey), "Tracked active round blocks completion");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, true, false, true, false, request.RunKey, request.RunKey), "Reward receipt blocks completion");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, true, false, false, true, request.RunKey, request.RunKey), "Purchase receipt blocks completion");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, true, false, false, false, request.RunKey, "other"), "Wrong completed round rejected");
            Check(ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, true, false, false, false, request.RunKey, request.RunKey), "Reward acknowledged current round completes");
            Check(ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, true, false, false, false, "", ""), "Already-empty page stops without buying a round");
            var lifecycle = new ArenaLifecyclePolicy();
            lifecycle.Observe("NO_RUN", 0);
            Check(!lifecycle.CanObserveEmptyRun("REWARDS", true, false, true, false, 20), "Cached reward session is not canonical NO_RUN proof");
            Check(!lifecycle.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, false, false, 0), "Cached reward session requires stable observation");
            Check(!lifecycle.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, false, false, 9.9f), "Do not shorten existing ten-second proof");
            var staleEmpty = lifecycle.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, false, false, 10);
            Check(ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, staleEmpty, false, false, false, request.RunKey, request.RunKey), "Acknowledged completed round can stop without entering next paid-run path");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, staleEmpty, false, true, false, request.RunKey, request.RunKey), "Cached empty proof cannot fabricate a reward receipt");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, staleEmpty, false, false, true, request.RunKey, request.RunKey), "Cached empty proof cannot release a pending purchase");
            Check(!ArenaRunStopContract.CanComplete("DRAFT", "INVALID", false, staleEmpty, false, false, false, request.RunKey, "other"), "Cached empty proof still requires the requested completed run");
            Check(!lifecycle.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, true, false, false, 11), "Active native session blocks fallback");
            Check(!lifecycle.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, true, false, 22), "Pending reward UI blocks fallback");
            Check(!lifecycle.CanRecoverStaleRewardSession("NO_RUN", "REWARDS", true, false, false, true, 33), "Pending reward chest blocks fallback");
            Check(!lifecycle.CanRecoverStaleRewardSession("NO_RUN", "INVALID", true, false, false, false, 44), "Unknown session cannot be treated as a stale reward");
            Check(!lifecycle.CanRecoverStaleRewardSession("MIDRUN", "REWARDS", true, false, false, false, 55), "Live manager round cannot be treated as empty");
            var receipt = new ArenaRunStopRequest { RequestId = request.RequestId, GameProcessId = 123,
                GameStartedAtUtcTicks = 100, RequestedAtUtcTicks = 200, Underground = underground,
                RunKey = request.RunKey, CompletedAtUtcTicks = 250 };
            Check(ArenaRunStopContract.Confirms(request, receipt, 123, 100, underground, 300), "Exact receipt pair accepted");
            receipt.RunKey = "other";
            Check(!ArenaRunStopContract.Confirms(request, receipt, 123, 100, underground, 300), "Cross-round receipt rejected");
            receipt.RunKey = request.RunKey; receipt.CompletedAtUtcTicks = 200;
            Check(!ArenaRunStopContract.Confirms(request, receipt, 123, 100, underground, 300), "Completion strictly later than request");
            receipt.CompletedAtUtcTicks = 301;
            Check(!ArenaRunStopContract.Confirms(request, receipt, 123, 100, underground, 300), "Future completion rejected");
        }
        // A run can end on the rewards page without a matching completed-run
        // key (three losses end it there); that used to hang the stop request
        // forever, so a bounded dwell on that page must complete it.
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "REWARDS", false, false, true, false, false, false, true, 59), "Rewards page needs the full dwell");
        Check(ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "REWARDS", false, false, true, false, false, false, true, 60), "Stalled rewards page completes after bounded dwell");
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "REWARDS", true, false, true, false, false, false, true, 600), "Active native session never completes");
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("MIDRUN", true, "REWARDS", false, false, true, false, false, false, true, 600), "Live manager round never completes");
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "REWARDS", false, false, true, false, false, false, false, 600), "Run that was never seen active cannot complete");
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "REWARDS", false, false, true, true, false, false, true, 600), "Pending reward blocks rewards-page completion");
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "REWARDS", false, false, true, false, true, false, true, 600), "Pending purchase blocks rewards-page completion");
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "REWARDS", false, false, true, false, false, true, true, 600), "Tracked active round blocks rewards-page completion");
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "DRAFTING", false, false, true, false, false, false, true, 600), "Drafting session is not a finished run");
        Check(!ArenaRunStopContract.CanCompleteOnRewardsPage("NO_RUN", true, "REWARDS", false, true, true, false, false, false, true, 600), "Active transition blocks rewards-page completion");
        Console.WriteLine($"Arena full-round stop: {checks} assertions across 3 cycles per Arena type passed");
    }
}
