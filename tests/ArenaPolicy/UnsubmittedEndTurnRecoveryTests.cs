using ArenaBoxBot;

internal static class UnsubmittedEndTurnRecoveryTests
{
    internal static void Run()
    {
        Check(UnsubmittedEndTurnRecoveryPolicy.CanPoll(Valid()), "fresh unsubmitted end turn can reach native executor");
        foreach (var change in new Action<EndTurnRecoveryEvidence>[]
        {
            e => e.ActiveOwnTurn = false, e => e.ResponseMode = 4,
            e => e.PacketId = -1, e => e.RecommendationPacketId = 21,
            e => e.RecommendationGamePid = 99, e => e.GamePid = 0,
            e => e.CapturedAgeSeconds = 10.01, e => e.CapturedAgeSeconds = -0.01,
            e => e.CapturedAgeSeconds = double.NaN, e => e.CapturedAgeSeconds = double.PositiveInfinity,
            e => e.RequestIdentityKnown = false, e => e.LedgerKnown = false,
            e => e.LedgerEntryPresent = true, e => e.CurrentPacketLedgerEntryPresent = true,
            e => e.ShadowKnown = false, e => e.ShadowCommitActive = true,
            e => e.ChoicePending = true
        })
        {
            var evidence = Valid(); change(evidence);
            Check(!UnsubmittedEndTurnRecoveryPolicy.CanPoll(evidence), "pending or unproven end turn retains acknowledgement gate");
        }
        Check(!UnsubmittedEndTurnRecoveryPolicy.CanPoll(null), "missing facts fail closed");
        Check(UnsubmittedEndTurnRecoveryPolicy.PacketEntryBlocks(22, true, "opt", 22), "new publication key on same pending packet cannot evade acknowledgement");
        Check(!UnsubmittedEndTurnRecoveryPolicy.PacketEntryBlocks(22, true, "opt", 20), "older packet does not block a fresh current packet");
        Check(UnsubmittedEndTurnRecoveryPolicy.PacketEntryBlocks(22, true, "opt", -1), "unknown option packet holds");
        Check(UnsubmittedEndTurnRecoveryPolicy.PacketEntryBlocks(22, false, "opt", 20), "unreadable ledger metadata holds");
        Check(UnsubmittedEndTurnRecoveryPolicy.PacketEntryBlocks(22, true, "", 20), "unknown domain holds");
        Check(!UnsubmittedEndTurnRecoveryPolicy.PacketEntryBlocks(22, true, "choice", 22), "explicit choice domain has a separate ID space; current choice state still independently holds");
        Console.WriteLine("PASS: fresh unsubmitted end-turn polling; pending/current-packet ACK, republished request, shadow, choice, stale PID/packet/time and unknown facts stay held");
    }

    private static EndTurnRecoveryEvidence Valid() => new()
    {
        ActiveOwnTurn = true, ResponseMode = 1, PacketId = 22, RecommendationPacketId = 22,
        GamePid = 3852, RecommendationGamePid = 3852, CapturedAgeSeconds = 1.2,
        RequestIdentityKnown = true, LedgerKnown = true, ShadowKnown = true
    };
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
