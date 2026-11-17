using ArenaBoxBot;

internal static class GameLimitStopTests
{
    internal static void Run()
    {
        // A terminal game reaches its limit before the reward ACK. The ledger
        // legitimately resets its counter at that ACK; the stop intent must
        // already exist and must allow the native reward flow to finish.
        var data = PlayedGame();
        Check(!ArenaStopPolicy.ShouldRequestGameLimitStop(true, "GAMEPLAY", data.AutomaticGamesInRun, 1),
            "Reaching the limit during gameplay must not disable the current game");
        var stopRequested = ArenaStopPolicy.ShouldRequestGameLimitStop(true, "DRAFT", data.AutomaticGamesInRun, 1);
        Check(stopRequested, "A terminal game must latch the reached limit on the rewards page");
        foreach (var state in new[] { "REWARDS", "IN_REWARDS" })
        {
            Check(!ArenaStopPolicy.BlocksFurtherActions(stopRequested, true), state + " must remain actionable");
            Check(!ArenaStopPolicy.IsSafeBoundary(state, true), state + " must not turn off before reward completion");
        }
        data.RewardPending = true;
        Check(ArenaStatsLedger.CompleteRun(data) && data.AutomaticGamesInRun == 0 && data.RewardsClaimed == 1,
            "The confirmed reward resets only the finished run's counter");
        Check(ArenaStopPolicy.BlocksFurtherActions(stopRequested, false) && ArenaStopPolicy.IsSafeBoundary("NO_RUN", false),
            "The retained limit stop must block another purchase or match after the reward ACK");

        // A fast native ACK can be reconciled in HUB/NO_RUN before a DRAFT
        // rewards tick. Check the production pre-receipt rule against the
        // still-intact ledger counter, then simulate that counter being reset.
        foreach (var scene in new[] { "HUB", "DRAFT" })
        {
            data = PlayedGame();
            data.RewardPending = true;
            stopRequested = ArenaStopPolicy.ShouldRequestGameLimitStop(true, scene, data.AutomaticGamesInRun, 1);
            ArenaStatsLedger.CompleteRun(data);
            Check(stopRequested && data.AutomaticGamesInRun == 0 &&
                ArenaStopPolicy.BlocksFurtherActions(stopRequested, false),
                scene + " must preserve the reached limit when the receipt precedes the reward-page tick");
        }

        // A naturally completed run below the per-run limit is allowed to
        // start another run. This setting is not a cross-run task game limit.
        data = PlayedGame();
        stopRequested = ArenaStopPolicy.ShouldRequestGameLimitStop(true, "DRAFT", data.AutomaticGamesInRun, 2);
        data.RewardPending = true;
        ArenaStatsLedger.CompleteRun(data);
        Check(!stopRequested && !ArenaStopPolicy.BlocksFurtherActions(stopRequested, false),
            "A natural early completion must not invent a stop request");
        Check(ArenaStatsLedger.StartRun(data, "normal:1:next", 0, 0, true) && data.AutomaticGamesInRun == 0,
            "The next natural run begins with a fresh per-run budget");
        Check(!ArenaStopPolicy.ShouldRequestGameLimitStop(true, "DRAFT", 5, 0), "Zero means unlimited");
        Check(!ArenaStopPolicy.ShouldRequestGameLimitStop(false, "DRAFT", 1, 1),
            "A late receipt after manual disable must not create an automation stop");
        foreach (var scene in new[] { "GAMEPLAY", "LOGIN", "UNKNOWN", "" })
            Check(!ArenaStopPolicy.ShouldRequestGameLimitStop(true, scene, 1, 1),
                "Only known post-game scenes may latch a limit stop: " + scene);
        Check(!ArenaStopPolicy.ShouldRequestGameLimitStop(true, null!, 1, 1),
            "Unavailable scene information must not stop an active game");
        Console.WriteLine("PASS: per-run limit survives terminal rewards/early ACK without stopping gameplay or natural continuation");
    }

    private static ArenaStatsData PlayedGame()
    {
        var data = new ArenaStatsData();
        ArenaStatsLedger.SelectMode(data, false);
        ArenaStatsLedger.StartRun(data, "normal:1:run", 0, 0, true);
        ArenaStatsLedger.BeginGame(data, "normal:1:run:server:host:3724:7");
        ArenaStatsLedger.FinishGame(data, data.PendingGameKey, false);
        return data;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
