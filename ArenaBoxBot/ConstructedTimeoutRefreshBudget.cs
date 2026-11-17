using System;

namespace ArenaBoxBot;

internal enum ConstructedTimeoutDecision
{
    HoldForTurnState,
    RefreshRecommendation,
    NativeFallback
}

// A native fallback can return after preparing its hover and submit on the
// next tick. Calling it is not proof that the turn ended. A refresh therefore
// belongs to the authoritative game/turn, not to an invocation or packet.
internal sealed class ConstructedTimeoutRefreshBudget
{
    private string _turn;

    internal ConstructedTimeoutDecision Decide(string authoritativeTurn)
    {
        // A stale opponent-turn snapshot is not proof that an own-turn
        // refresh was consumed. In particular, do not turn that uncertainty
        // into permission to submit the native timeout end-turn action.
        if (string.IsNullOrEmpty(authoritativeTurn))
            return ConstructedTimeoutDecision.HoldForTurnState;
        return TryRefresh(authoritativeTurn)
            ? ConstructedTimeoutDecision.RefreshRecommendation
            : ConstructedTimeoutDecision.NativeFallback;
    }

    internal bool TryRefresh(string authoritativeTurn)
    {
        if (string.IsNullOrEmpty(authoritativeTurn)) return false;
        if (string.Equals(_turn, authoritativeTurn, StringComparison.Ordinal)) return false;
        _turn = authoritativeTurn;
        return true;
    }
}
