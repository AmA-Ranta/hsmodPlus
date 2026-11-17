using System;

namespace ArenaBoxBot;

internal enum ArenaRunBudgetDecision { Ready, WaitForIdentity, DraftSelectionOnly }

/// <summary>The native Arena identity, rather than an automation toggle, owns its budget.</summary>
internal static class ArenaRunBudgetPolicy
{
    internal static ArenaRunBudgetDecision Decide(bool trackedActive, bool trackedRewards, string trackedKey,
        bool trackedUnderground, bool nativeReady, bool nativeUnderground, string nativeKey, string nativeState,
        bool nativeActive, bool nativeInRewards, int slot, int maxSlot, bool confirmedEmpty,
        bool confirmedRewardReceipt)
    {
        if (!trackedActive) return ArenaRunBudgetDecision.Ready;
        if (!nativeReady || trackedUnderground != nativeUnderground) return ArenaRunBudgetDecision.WaitForIdentity;
        // A confirmed receipt belongs to the retained reward context. The
        // native client may already have removed its finished deck at NO_RUN.
        if (confirmedRewardReceipt && trackedRewards && nativeState == "NO_RUN" &&
            !nativeActive && !nativeInRewards) return ArenaRunBudgetDecision.Ready;
        // Latch a reached budget before stable NO_RUN retires the old ledger.
        if (confirmedEmpty && nativeState == "NO_RUN" && !nativeActive && !nativeInRewards)
            return ArenaRunBudgetDecision.Ready;
        if (string.IsNullOrEmpty(nativeKey)) return ArenaRunBudgetDecision.WaitForIdentity;
        var knownRunState = nativeState == "DRAFTING" || nativeState == "MIDRUN" ||
            nativeState == "MIDRUN_REDRAFT_PENDING" || nativeState == "REDRAFTING" ||
            nativeState == "EDITING_DECK" || nativeState == "REWARDS" || nativeState == "IN_REWARDS";
        if (!knownRunState) return ArenaRunBudgetDecision.WaitForIdentity;
        if (!nativeActive && nativeState != "REWARDS" && nativeState != "IN_REWARDS")
            return ArenaRunBudgetDecision.WaitForIdentity;
        if (!string.IsNullOrEmpty(trackedKey) && string.Equals(trackedKey, nativeKey, StringComparison.Ordinal))
            return ArenaRunBudgetDecision.Ready;
        // The existing lifecycle binds DRAFTING after its first successful
        // choice. Blocking that choice would prevent binding forever. It may
        // use the existing native selection gates, without authorizing queue
        // or applying the previous run's budget to the new native identity.
        if (nativeActive && !nativeInRewards && nativeState == "DRAFTING" && slot == 0 && maxSlot > 0)
            return ArenaRunBudgetDecision.DraftSelectionOnly;
        return ArenaRunBudgetDecision.WaitForIdentity;
    }
}
