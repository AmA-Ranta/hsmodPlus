using ArenaController;

internal static class ArenaRunStopBindingTests
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        for (var cycle = 0; cycle < 3; cycle++)
        {
            foreach (var state in new[] { "NO_RUN", "INVALID", "", "HUB", "RUN_STOP_RECEIPT_PENDING" })
                Check(!ArenaRunStopBindingPolicy.CanPinRun(true, true, false, state),
                    "Stale active ledger cannot pin an absent or unknown server run: " + state);
            foreach (var state in new[] { "DRAFTING", "MIDRUN", "MIDRUN_REDRAFT_PENDING", "REDRAFTING",
                         "EDITING_DECK", "REWARDS", "IN_REWARDS" })
            {
                Check(ArenaRunStopBindingPolicy.CanPinRun(true, true, false, state), "Current run stays bound: " + state);
                Check(!ArenaRunStopBindingPolicy.CanPinRun(false, true, false, state), "Unselected arena cannot bind");
                Check(!ArenaRunStopBindingPolicy.CanPinRun(true, false, false, state), "No tracked run cannot bind");
            }
            Check(ArenaRunStopBindingPolicy.CanPinRun(true, true, true, "INVALID"), "Gameplay preserves current run while draft state is unavailable");
            var calls = new List<string>();
            var refused = false;
            try { ArenaRunStopBindingPolicy.EnableAfterBinding(true,
                () => { calls.Add("publish-failed"); return false; }, () => { calls.Add("ON"); return true; }); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused && calls.SequenceEqual(new[] { "publish-failed" }), "Reconnect must not enable if rebinding fails");
            calls.Clear();
            Check(ArenaRunStopBindingPolicy.EnableAfterBinding(true,
                () => { calls.Add("publish-new-process"); return true; }, () => { calls.Add("ON"); return true; }) &&
                calls.SequenceEqual(new[] { "publish-new-process", "ON" }), "Pending intent is published before recovered script ON");
            calls.Clear();
            Check(ArenaRunStopBindingPolicy.EnableAfterBinding(false,
                () => throw new Exception("Unexpected request"), () => { calls.Add("ON"); return true; }) &&
                calls.SequenceEqual(new[] { "ON" }), "Ordinary startup remains independent of run-stop files");
            Check(!ArenaRunStopBindingPolicy.EnableAfterBinding(true, () => true, () => false), "Unconfirmed ON remains a failure");
        }
        Console.WriteLine($"PASS: Arena run-stop stale-ledger and reconnect binding, {checks} assertions / 3 cycles");
    }
}
