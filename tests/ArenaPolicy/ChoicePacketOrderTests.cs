using ArenaBoxBot;

internal static class ChoicePacketOrderTests
{
    internal static void Run()
    {
        var order = new ChoicePacketOrder();
        // Captured 07:56 sequence: choice 6 waits behind choice 5; native
        // dequeues 6 for DeferOneFrame, then processes its result too early
        // while no choice map entry exists. Its later registration strands
        // task 177's pre-choice reference until the game-over queue flush.
        order.Receive("captured-game", 2, 6);
        Require(!order.AllowChosen("captured-game", 2, 6, 5, false, out _), "native rejection remains rejection");
        Require(!order.AllowChosen("captured-game", 2, 6, null, true, out var first) && first, "deferred registration blocks the early native-allowed result");
        Require(!order.AllowChosen("captured-game", 2, 6, null, true, out first) && !first, "repeated polling preserves the same queued result without log flooding");
        Require(order.AllowChosen("captured-game", 2, 6, 6, true, out _), "native result proceeds after matching choice registration");
        Require(order.AllowChosen("captured-game", 2, 7, null, true, out _), "unobserved choices preserve native behavior");
        Require(order.AllowChosen("captured-game", 1, 6, null, true, out _), "another player's packet is independent");
        order.Receive("captured-game", 2, 8);
        Require(!order.AllowChosen("captured-game", 2, 8, 8, false, out _), "missing entity/native guard cannot be overridden by registered choice");
        Require(order.AllowChosen("captured-game", 2, 8, 8, true, out _), "later native readiness completes without inventing a selection");
        order.Receive("captured-game", 2, 9);
        Require(!order.AllowChosen("captured-game", 2, 9, null, true, out _), "received result waits while native registration is still expected");
        Require(!order.AllowChosen("captured-game", 2, 9, null, false, out _, nativeDiscardsChoices: true), "native rejection still applies during match teardown");
        Require(order.AllowChosen("captured-game", 2, 9, null, true, out _, nativeDiscardsChoices: true), "native early-concede teardown skips registration, so its result must not be stranded");
        order.Receive("captured-game", 2, 9);
        Require(order.AllowChosen("new-game", 2, 9, null, true, out _), "new game does not inherit pending choice IDs");
        order.Receive(null, 2, 10);
        Require(order.AllowChosen(null, 2, 10, null, true, out _), "unknown game retains native behavior");
        Console.WriteLine("PASS: captured deferred choice/result ordering, native rejection, native teardown, player and game isolation");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new Exception("Choice ordering: " + reason);
    }
}
