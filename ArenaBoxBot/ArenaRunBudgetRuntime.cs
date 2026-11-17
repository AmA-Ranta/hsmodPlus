namespace ArenaBoxBot;

internal sealed partial class ArenaController
{
    private bool _lastArenaModeSelected;
    private bool _arenaModeSelectionSeen;
    private bool _lastNativeArenaRunning;

    // This exact production activation path is exercised outside Unity by the
    // budget regressions. The ledger owns the run counter and pending game.
    private void ObserveNativeArenaActivation(bool arenaModeSelected, bool nativeArenaRunning)
    {
        var previousModeSelected = _lastArenaModeSelected;
        var previousNativeRunning = _lastNativeArenaRunning;
        var hadPreviousArenaState = _arenaModeSelectionSeen;
        if (hadPreviousArenaState && arenaModeSelected == previousModeSelected &&
            nativeArenaRunning == previousNativeRunning) return;
        _arenaModeSelectionSeen = true;
        _lastArenaModeSelected = arenaModeSelected;
        _lastNativeArenaRunning = nativeArenaRunning;
        ResetArenaPageTransientState();
        // Re-enable re-evaluates a changed limit or a repaired dependency;
        // it does not grant another budget for the same Arena run.
        if (hadPreviousArenaState && (!arenaModeSelected ||
            (arenaModeSelected && nativeArenaRunning && previousModeSelected && !previousNativeRunning)))
        {
            _runStopRequested = false;
            _runStopReason = "";
            s_arenaOverlayStopReason = "";
        }
        SynchronizeRunBudgetProjection();
        Log("native Arena state changed: mode=" + arenaModeSelected + ", runtime=" + nativeArenaRunning +
            "; reset page-cycle state; retained run=" + ArenaStatsStore.Data.RunKey + "; games=" + _gamesThisRun);
    }

    private void SynchronizeRunBudgetProjection()
    {
        _gamesThisRun = ArenaStatsStore.Data.AutomaticGamesInRun;
        UpdateArenaBudgetOverlay();
    }

    private ArenaRunBudgetDecision EvaluateNativeRunBudget(bool nativeReady, bool nativeUnderground,
        string nativeKey, string nativeState, bool nativeActive, bool nativeInRewards,
        int slot, int maxSlot, bool confirmedEmpty, bool confirmedRewardReceipt)
    {
        var data = ArenaStatsStore.Data;
        return ArenaRunBudgetPolicy.Decide(data.RunActive, data.RewardPending, data.RunKey, data.RunUnderground,
            nativeReady, nativeUnderground, nativeKey, nativeState, nativeActive, nativeInRewards,
            slot, maxSlot, confirmedEmpty, confirmedRewardReceipt);
    }

    private void RequestAlignedGameLimitStop(bool runtimeEnabled, string scene, int limit,
        ArenaRunBudgetDecision decision)
    {
        SynchronizeRunBudgetProjection();
        if (!_runStopRequested && decision == ArenaRunBudgetDecision.Ready &&
            ArenaStopPolicy.ShouldRequestGameLimitStop(runtimeEnabled, scene,
                ArenaStatsStore.Data.AutomaticGamesInRun, limit))
            RequestArenaStop("已达到本轮自动对局上限");
    }

    private bool TryRequestConfirmedRewardBudgetStop(bool receiptConfirmed, bool nativeModeMatches,
        string nativeState, bool nativeActive, bool rewardChestReady, bool runtimeEnabled, string scene, int limit)
    {
        var data = ArenaStatsStore.Data;
        // HasConfirmedPendingRewardReceipt already owns this acknowledgement.
        // GetArenaState/HasCurrentlyActiveRun can confirm NO_RUN after the
        // client cleared its session packet and its Has* fields. Preserve the
        // existing pre-clear stop boundary without weakening ordinary run
        // identity checks. This path can only request a stop.
        if (!receiptConfirmed || !data.RunActive || !data.RewardPending || !nativeModeMatches ||
            nativeState != "NO_RUN" || nativeActive || rewardChestReady) return false;
        RequestAlignedGameLimitStop(runtimeEnabled, scene, limit, ArenaRunBudgetDecision.Ready);
        return true;
    }
}
