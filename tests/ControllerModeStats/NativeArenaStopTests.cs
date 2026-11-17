using ArenaController;

internal static class NativeArenaStopTests
{
    internal static void Run()
    {
        Check(NativeArenaStopPolicy.IsAutomaticCloseCandidate(true, "门票不足，按设置停止"), "actual automatic shortage stop honors CloseGame option");
        Check(!NativeArenaStopPolicy.IsAutomaticCloseCandidate(false, "门票不足"), "unchecked close option preserves game window");
        Check(!NativeArenaStopPolicy.IsAutomaticCloseCandidate(true, ""), "unexplained F12 stop does not force client exit");
        Check(NativeArenaStopPolicy.CanFinish(true, true, false, false, "credited"), "observed updated Normal full rewards/credited entry allows normal stop-close");
        Check(NativeArenaStopPolicy.CanFinish(true, true, false, false, ""), "known no-entry shortage may close safely");
        Check(NativeArenaStopPolicy.CanFinish(true, true, false, false, "empty"), "confirmed empty-entry receipt may close safely");
        Check(!NativeArenaStopPolicy.CanFinish(false, true, false, false, "credited"), "active game or unknown boundary remains held");
        Check(!NativeArenaStopPolicy.CanFinish(true, false, false, false, "credited"), "missing or corrupt stats cannot authorize close");
        Check(!NativeArenaStopPolicy.CanFinish(true, true, true, false, "credited"), "pending rewards remain intact");
        Check(!NativeArenaStopPolicy.CanFinish(true, true, false, true, "credited"), "purchase awaiting receipt must settle");
        foreach (var phase in new[] { "requested", "accepted", "bound", "unknown" })
            Check(!NativeArenaStopPolicy.CanFinish(true, true, false, false, phase), "unresolved entry is preserved: " + phase);
        Check(!NativeArenaStopPolicy.CanFinish(true, true, false, false, null), "absent receipt phase is unknown");
        Console.WriteLine("PASS: native automatic Arena stop-close, manual F12/unchecked option, pending game/rewards/purchase/entry and unknown receipt safety");
    }
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException(reason);
    }
}
