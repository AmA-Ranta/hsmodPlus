using System;

namespace ArenaBoxBot;

internal sealed class EndTurnRecoveryEvidence
{
    internal bool ActiveOwnTurn;
    internal int ResponseMode = -1;
    internal int PacketId = -1;
    internal int RecommendationPacketId = -1;
    internal int GamePid;
    internal int RecommendationGamePid;
    internal double CapturedAgeSeconds = double.NaN;
    internal bool RequestIdentityKnown;
    internal bool LedgerKnown;
    internal bool LedgerEntryPresent;
    internal bool CurrentPacketLedgerEntryPresent;
    internal bool ShadowKnown;
    internal bool ShadowCommitActive;
    internal bool ChoicePending;
}

/// <summary>Only an unsubmitted current request may leave the response wait.</summary>
internal static class UnsubmittedEndTurnRecoveryPolicy
{
    internal static bool CanPoll(EndTurnRecoveryEvidence e)
        => e != null && e.ActiveOwnTurn && e.ResponseMode == 1 &&
           e.PacketId >= 0 && e.RecommendationPacketId == e.PacketId &&
           e.GamePid > 0 && e.RecommendationGamePid == e.GamePid &&
           !double.IsNaN(e.CapturedAgeSeconds) && !double.IsInfinity(e.CapturedAgeSeconds) &&
           e.CapturedAgeSeconds >= 0 && e.CapturedAgeSeconds <= 10 &&
           e.RequestIdentityKnown && e.LedgerKnown && !e.LedgerEntryPresent && !e.CurrentPacketLedgerEntryPresent &&
           e.ShadowKnown && !e.ShadowCommitActive && !e.ChoicePending;

    // A re-published request may have a new key while an earlier request on
    // the same packet still awaits acknowledgement. Packet scope must hold
    // it too. Entries in an explicitly different domain use another ID space.
    internal static bool PacketEntryBlocks(int currentPacket, bool known, string domain, int entryPacket)
        => !known || currentPacket < 0 || string.IsNullOrEmpty(domain) ||
           (domain == "opt" && (entryPacket < 0 || entryPacket == currentPacket));
}
