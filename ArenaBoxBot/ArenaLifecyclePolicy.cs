using System;

namespace ArenaBoxBot;

/// <summary>Server acknowledgements, rather than local page changes, own run boundaries.</summary>
public sealed class ArenaLifecyclePolicy
{
    private string _state = "";
    private float _stateSince;
    private float _beginRequestedAt = -1f;
    private string _attachmentCandidate = "";
    private float _attachmentSince = -1f;
    private float _serverEmptySince = -1f;
    private float _staleRewardSince = -1f;

    public void Observe(string state, float now)
    {
        if (string.Equals(_state, state, StringComparison.Ordinal)) return;
        _state = state ?? "";
        _stateSince = now;
    }

    public bool IsStable(float now) => now - _stateSince >= 3f;
    public bool CanSelect(string state, bool nativeActive, int maxSlot, float now)
        => nativeActive && maxSlot > 0 && IsStable(now) &&
           (state == "DRAFTING" || state == "REDRAFTING");

    public bool CanConfirmRun(string state, bool nativeActive, int slot, int maxSlot, float now)
        => nativeActive && maxSlot > 0 && IsStable(now) &&
           ((state == "DRAFTING" && slot > 0) || state == "MIDRUN" ||
            state == "MIDRUN_REDRAFT_PENDING" || state == "REDRAFTING" || state == "EDITING_DECK");

    public bool CanCloseRun(string state, bool rewardsSeen, float now)
        => state == "NO_RUN" && rewardsSeen && IsStable(now);

    public bool CanDiscardLegacyPlaceholder(string state, bool nativeActive, string persistedRunKey, float now)
        => state == "NO_RUN" && !nativeActive && string.IsNullOrEmpty(persistedRunKey) && now - _stateSince >= 30f;

    public bool CanRequestBegin(bool trackedRunActive, float now, long requestAtUnixMs = 0, long nowUnixMs = 0)
        => !trackedRunActive && IsStable(now) &&
           (_beginRequestedAt < 0f || now - _beginRequestedAt >= 30f) &&
           (requestAtUnixMs <= 0 || nowUnixMs - requestAtUnixMs >= 30000);

    public void BeginRequested(float now) => _beginRequestedAt = now;
    public void ConfirmedRun() => _beginRequestedAt = -1f;

    public static bool ShouldReturnToHub(string scene, string nextScene, bool stopRequested, float elapsed)
        => !stopRequested && scene == "TOURNAMENT" && nextScene == "INVALID" && elapsed >= 30f;

    public static bool ShouldCloseStaleReward(string state, bool pendingWidget, bool serverChestReady, int choiceCount)
        => (state == "DRAFTING" || state == "REDRAFTING") && pendingWidget && !serverChestReady && choiceCount > 0;

    public static bool ResolveStatsMode(bool selectedUnderground, bool inArenaGame, bool actualUnderground)
        => inArenaGame ? actualUnderground : selectedUnderground;

    public bool CanObserveEmptyRun(string state, bool serverSessionReady, bool serverActive,
        bool serverInRewards, bool rewardChestReady, float now)
    {
        if (_state != "NO_RUN" || state != "NO_RUN" || !serverSessionReady || serverActive || serverInRewards || rewardChestReady)
        {
            _serverEmptySince = -1f;
            return false;
        }
        if (_serverEmptySince < 0f) _serverEmptySince = now;
        return IsStable(now) && now - _serverEmptySince >= 3f;
    }

    public bool CanRetireMissingRun(string state, bool serverSessionReady, bool serverActive,
        bool serverInRewards, bool rewardChestReady, float now)
        => CanObserveEmptyRun(state, serverSessionReady, serverActive, serverInRewards, rewardChestReady, now) &&
           now - _serverEmptySince >= 10f;

    public void ResetNativeEvidence()
    {
        _attachmentCandidate = "";
        _attachmentSince = -1f;
        _serverEmptySince = -1f;
        _staleRewardSince = -1f;
    }

    /// <summary>
    /// A client build can retain SessionState=REWARDS after the server has
    /// already acknowledged the chest and moved the manager to NO_RUN. This
    /// recovery is safe only after the reward UI/chest is gone, the native
    /// session is inactive, and the contradiction is stable for ten seconds.
    /// It never clears a receipt or claims a reward by itself.
    /// </summary>
    public bool CanRecoverStaleRewardSession(string managerState, string sessionState,
        bool serverSessionReady, bool serverActive, bool rewardUiPending,
        bool rewardChestReady, float now)
    {
        var stale = string.Equals(managerState, "NO_RUN", StringComparison.Ordinal) &&
                    (string.Equals(sessionState, "REWARDS", StringComparison.Ordinal) ||
                     string.Equals(sessionState, "IN_REWARDS", StringComparison.Ordinal)) &&
                    serverSessionReady && !serverActive && !rewardUiPending && !rewardChestReady;
        if (!stale)
        {
            _staleRewardSince = -1f;
            return false;
        }
        if (_staleRewardSince < 0f) _staleRewardSince = now;
        return IsStable(now) && now - _staleRewardSince >= 10f;
    }

    public bool CanAttachNativeRun(string key, string state, int wins, int losses, bool serverActive,
        bool serverInRewards, float now)
    {
        if (string.IsNullOrEmpty(key) || wins < 0 || losses < 0 || !serverActive || serverInRewards ||
            (state != "DRAFTING" && state != "MIDRUN" && state != "MIDRUN_REDRAFT_PENDING" &&
             state != "REDRAFTING" && state != "EDITING_DECK"))
        {
            _attachmentCandidate = "";
            _attachmentSince = -1f;
            return false;
        }
        var candidate = key + ":" + state + ":" + wins + "/" + losses;
        if (_attachmentCandidate != candidate || _attachmentSince < 0f)
        {
            _attachmentCandidate = candidate;
            _attachmentSince = now;
        }
        return now - _attachmentSince >= 3f;
    }
}

/// <summary>A loading-time NO_RUN placeholder is not a reward acknowledgement.</summary>
public sealed class ArenaRewardReceiptPolicy
{
    private string _runKey = "";
    private float _confirmedSince = -1f;

    public void Reset()
    {
        _runKey = "";
        _confirmedSince = -1f;
    }

    public bool Observe(string runKey, bool rewardPending, string scene, string nextScene,
        bool nativeReady, string nativeState, bool nativeRunActive, bool rewardChestReady, float now)
    {
        if (!rewardPending || !nativeReady || (scene != "DRAFT" && scene != "HUB") ||
            nextScene != "INVALID" || nativeState != "NO_RUN" || nativeRunActive || rewardChestReady)
        {
            Reset();
            return false;
        }
        if (_confirmedSince < 0f || !string.Equals(_runKey, runKey, StringComparison.Ordinal))
        {
            _runKey = runKey;
            _confirmedSince = now;
        }
        return now - _confirmedSince >= 3f;
    }
}

/// <summary>Monotonic reconciliation of game callbacks and delayed server totals.</summary>
public static class ArenaResultPolicy
{
    public static int Reconcile(int recorded, int server) => Math.Max(0, Math.Max(recorded, server));
    public static int ResultTarget(int before, bool applies) => Math.Max(0, before) + (applies ? 1 : 0);
    public static bool IsConfirmedPurchase(int ticketsBefore, int ticketsAfter, int goldBefore,
        int goldAfter, bool requested, out int spent)
    {
        spent = goldBefore >= 0 && goldAfter >= 0 ? Math.Max(0, goldBefore - goldAfter) : 0;
        // A transaction is ours only after BuyWithGold was invoked. A gold
        // debit still proves it when the new ticket was already consumed.
        return requested && (ticketsAfter > ticketsBefore || spent > 0);
    }
}
