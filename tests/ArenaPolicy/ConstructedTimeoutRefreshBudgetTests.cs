using ArenaBoxBot;

internal static class ConstructedTimeoutRefreshBudgetTests
{
    internal static void Run()
    {
        var capturedStaleTurns = new ConstructedTimeoutRefreshBudget();
        // Captured packets 19/27: server own turns 7/9, processed local turn
        // 6 and opponent ownership. Neither packet authorizes a native end.
        Require(capturedStaleTurns.Decide(null) == ConstructedTimeoutDecision.HoldForTurnState,
            "Packet19 with stale opponent ownership must hold without native end-turn");
        Require(capturedStaleTurns.Decide(null) == ConstructedTimeoutDecision.HoldForTurnState,
            "Packet27 must not treat another unconfirmed turn as consumed refresh");
        Require(capturedStaleTurns.Decide("captured-game:9") == ConstructedTimeoutDecision.RefreshRecommendation,
            "Authoritative state catching up gets its first refresh");
        Require(capturedStaleTurns.Decide("captured-game:9") == ConstructedTimeoutDecision.NativeFallback,
            "Confirmed repeated own-turn timeout retains native hover-to-submit fallback");
        Require(capturedStaleTurns.Decide(null) == ConstructedTimeoutDecision.HoldForTurnState,
            "Lost ownership proof after hover must preserve pending state without sending");
        Require(capturedStaleTurns.Decide("captured-game:9") == ConstructedTimeoutDecision.NativeFallback,
            "Ownership proof returning must not reopen a consumed refresh budget");
        var budget = new ConstructedTimeoutRefreshBudget();
        Require(!budget.TryRefresh(null), "Unknown game/turn must not clear native fallback");
        Require(budget.TryRefresh("game16872:24"), "First timeout may refresh");
        Require(!budget.TryRefresh("game16872:24"), "Second timeout must reach native hover");
        // Packet194 replay: ContinueEndTurnFallback returns after hover;
        // HandleGame's pending-latch branch must reach submission next tick.
        for (var i = 0; i < 8; i++)
            Require(!budget.TryRefresh("game16872:24"), "Repeated attempts must preserve the native pending latch");
        Require(!budget.TryRefresh(null), "Unknown snapshot cannot reopen a consumed budget");
        Require(!budget.TryRefresh("game16872:24"), "Returning to same turn remains consumed");
        Require(budget.TryRefresh("game16872:26"), "Next own turn gets one refresh");
        Require(budget.TryRefresh("game23028:26"), "New game identity gets its own budget");
        Require(!budget.TryRefresh("game23028:26"), "New game's repeated timeout remains bounded");
        Console.WriteLine("PASS: captured packet194 timeout sequence preserves native hover-to-submit phase; one refresh per game/own turn");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
