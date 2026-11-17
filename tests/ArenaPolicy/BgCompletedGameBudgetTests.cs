using ArenaBoxBot;

internal static class BgCompletedGameBudgetTests
{
    internal static void Run()
    {
        var budget = new BgCompletedGameBudget();
        Require(!budget.Record("1165", true, false, false), "Live combat rounds must not count as completed games");
        Require(!budget.Record("", true, true, true), "Unknown identity cannot count");
        Require(!budget.Record("tutorial", false, true, true), "Excluded game types cannot consume the quota");
        // Captured R39 failure: terminal LOST + COMPLETE, CSV never updates;
        // the next queue must be blocked without waiting 90s for rating data.
        Require(!budget.Record("1165", true, true, false) && !budget.Reached(1), "A simulated local callback or disconnect is not proof of server completion");
        Require(budget.Record("1165", true, false, true) && budget.Reached(1), "Server COMPLETE plus terminal must stop a one-game task even with no CSV row");
        Require(!budget.Record("1165", true, true, true) && budget.Completed == 1, "Repeated terminal callbacks must not double count");
        Require(!budget.Reached(0) && !budget.Reached(2), "Unlimited and larger budgets must retain their semantics");
        Require(!budget.Record("1173", true, false, true), "Raw server completion alone cannot stand in for the terminal callback");
        Require(budget.Record("1173", true, true, false) && budget.Reached(2), "A second distinct game completes a two-game session in either observation order");
        budget.Reset();
        Require(budget.Completed == 0 && !budget.Reached(1), "Only an explicit native session reset starts a fresh budget");
        Require(!budget.Record("1165", true, true, false), "Server evidence must also reset with the native session");
        Require(budget.Record("1165", true, false, true), "Identity de-duplication must reset with the native session");
        System.Console.WriteLine("PASS: BG terminal plus server COMPLETE budget, missing/delayed CSV, fake callback rejection, deduplication and explicit session reset");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new System.InvalidOperationException(message); }
}
