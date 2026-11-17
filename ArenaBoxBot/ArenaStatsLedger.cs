using System;
using System.Collections.Generic;

namespace ArenaBoxBot;

[Serializable]
internal sealed class ArenaStatsLogCursor
{
    public string LogPath = "";
    public long Position = 0;
}

internal enum ArenaEntryRecoveryDecision { None, Wait, RequestFreshSession, Stop }

// Recovery only confirms a financial result; it never retries spending.
internal sealed class ArenaEntryRecoveryBudget
{
    private string _nonce;
    private float _confirmationStartedAt = -1f;

    // Reset the observation attempt, never the persisted spending receipt.
    internal void Reset() { _nonce = null; _confirmationStartedAt = -1f; }

    internal ArenaEntryRecoveryDecision Observe(ArenaEntryReceipt receipt, string arenaState, long nowUnixMs, float now)
    {
        var unresolved = receipt != null && (receipt.Phase == "prepared" || receipt.Phase == "submission-uncertain" ||
            receipt.Phase == "accepted" || receipt.Phase == "response-seen" || receipt.Phase == "bound");
        var needsConfirmation = unresolved && (arenaState == "NO_RUN" ||
            (string.IsNullOrEmpty(receipt.ResponseRunKey) && (receipt.Phase == "prepared" ||
             receipt.Phase == "submission-uncertain" || receipt.Phase == "accepted")));
        if (!needsConfirmation) { _nonce = null; _confirmationStartedAt = -1f; return ArenaEntryRecoveryDecision.None; }
        if (nowUnixMs < receipt.RequestedAtUnixMs ||
            nowUnixMs - receipt.RequestedAtUnixMs < ArenaStatsLedger.EntryResponseWindowMs)
            return ArenaEntryRecoveryDecision.Wait;
        if (_nonce != receipt.Nonce || _confirmationStartedAt < 0f)
        {
            _nonce = receipt.Nonce;
            _confirmationStartedAt = now;
            return receipt.EmptyAcknowledgedAtUnixMs == 0 ? ArenaEntryRecoveryDecision.RequestFreshSession : ArenaEntryRecoveryDecision.Wait;
        }
        return now - _confirmationStartedAt >= 30f ? ArenaEntryRecoveryDecision.Stop : ArenaEntryRecoveryDecision.Wait;
    }
}

[Serializable]
internal sealed class ArenaEntryReceipt
{
    public string Nonce = "";
    public bool Underground;
    public string PreviousRunKey = "";
    public string PreviousNativeRunKey = "";
    public long RequestedAtUnixMs;
    public int TicketsBefore;
    public int TicketCost;
    // submission-uncertain is a write-ahead spending lock, not evidence
    // that an event ran. accepted means our scoped Network.DraftBegin call
    // returned and its receipt was confirmed on disk; it is not Widget's
    // found-listener return value or proof of server admission.
    public string Phase = "";
    public string ResponseRunKey = "";
    public long ResponseAtUnixMs;
    public string BoundRunKey = "";
    public long BoundAtUnixMs;
    public long EmptyAcknowledgedAtUnixMs;

    internal ArenaEntryReceipt Copy() => (ArenaEntryReceipt)MemberwiseClone();
}

[Serializable]
internal sealed class ArenaRunContext
{
    public int Wins;
    public int Losses;
    public bool Active;
    public bool RewardPending;
    public string RunKey = "";
    public string Origin = "";
    public string PreviousKey = "";
    public long EmptyRunObservedAtUnixMs;
    public string EmptyRunPreviousKey = "";
    public string LastCompletedRunKey = "";
    public string PendingGameKey = "";
    public int PendingGameWinsBefore;
    public int PendingGameLossesBefore;
    public int AutomaticGamesInRun;
    public long EntryRequestedAtUnixMs;
    public ArenaEntryReceipt EntryReceipt = new ArenaEntryReceipt();
}

[Serializable]
internal sealed class ArenaStatsData
{
    public int TotalWins;
    public int TotalLosses;
    public int CurrentRunWins;
    public int CurrentRunLosses;
    public int RunCount;
    public int HighestWins;
    public int RewardsClaimed;
    public int TicketsPurchased;
    public int GoldSpent;
    public bool RunActive;
    public bool RewardPending;
    public string RunKey = "";
    public string RunOrigin = "";
    public string RunPreviousKey = "";
    public long EmptyRunObservedAtUnixMs;
    public string EmptyRunPreviousKey = "";
    public bool RunUnderground;
    public bool ModeContextsInitialized;
    public ArenaRunContext NormalRun = new ArenaRunContext();
    public ArenaRunContext UndergroundRun = new ArenaRunContext();
    public int AutomaticGamesInRun;
    public long EntryRequestedAtUnixMs;
    public ArenaEntryReceipt EntryReceipt = new ArenaEntryReceipt();
    // Keep every credited identity. Trimming this list could count an old
    // native deck again after later rewards have replaced LastCompletedRunKey.
    public List<string> CountedRunKeys = new List<string>();
    public string LastCompletedRunKey = "";
    public string PendingGameKey = "";
    public int PendingGameWinsBefore;
    public int PendingGameLossesBefore;
    public List<string> RecordedGameKeys = new List<string>();
    public bool PurchasePending;
    public long PurchaseRequestedAtUnixMs;
    public int PurchaseTicketsBefore;
    public int PurchaseGoldBefore = -1;
    public bool PurchaseCounted;
    public int PurchaseGoldCounted;
    // Retain legacy fields when saving existing statistics, but do not run
    // the obsolete log importer alongside native game result receipts.
    public List<ArenaStatsLogCursor> MatchLogCursors = new List<ArenaStatsLogCursor>();
    public bool MatchLogBackfillCompleted = false;
}

internal static class ArenaStatsLedger
{
    internal static bool SelectMode(ArenaStatsData data, bool underground)
    {
        var initializedNow = false;
        if (!data.ModeContextsInitialized)
        {
            // Earlier files have only the projection. A recorded deck key
            // identifies its owner; pre-key files retain their current-run
            // counters under the first verified native/selected Arena type.
            // Lifetime counters are never changed during migration.
            data.RunUnderground = data.RunKey?.StartsWith("underground:", StringComparison.Ordinal) == true ||
                (data.RunKey?.StartsWith("normal:", StringComparison.Ordinal) != true && underground);
            data.ModeContextsInitialized = true;
            if (string.IsNullOrEmpty(data.RunOrigin)) data.RunOrigin = "legacy-projection";
            CaptureCurrentContext(data);
            initializedNow = true;
        }
        if (data.RunUnderground == underground) return initializedNow;
        CaptureCurrentContext(data);
        var target = underground ? data.UndergroundRun : data.NormalRun;
        target = target ?? new ArenaRunContext();
        data.RunUnderground = underground;
        data.CurrentRunWins = target.Wins;
        data.CurrentRunLosses = target.Losses;
        data.RunActive = target.Active;
        data.RewardPending = target.RewardPending;
        data.RunKey = target.RunKey ?? "";
        data.RunOrigin = target.Origin ?? "";
        data.RunPreviousKey = target.PreviousKey ?? "";
        data.EmptyRunObservedAtUnixMs = target.EmptyRunObservedAtUnixMs;
        data.EmptyRunPreviousKey = target.EmptyRunPreviousKey ?? "";
        data.LastCompletedRunKey = target.LastCompletedRunKey ?? "";
        data.PendingGameKey = target.PendingGameKey ?? "";
        data.PendingGameWinsBefore = target.PendingGameWinsBefore;
        data.PendingGameLossesBefore = target.PendingGameLossesBefore;
        data.AutomaticGamesInRun = target.AutomaticGamesInRun;
        data.EntryRequestedAtUnixMs = target.EntryRequestedAtUnixMs;
        data.EntryReceipt = target.EntryReceipt?.Copy() ?? new ArenaEntryReceipt();
        CaptureCurrentContext(data);
        return true;
    }

    internal static void CaptureCurrentContext(ArenaStatsData data)
    {
        if (!data.ModeContextsInitialized) return;
        var snapshot = new ArenaRunContext
        {
            Wins = data.CurrentRunWins, Losses = data.CurrentRunLosses,
            Active = data.RunActive, RewardPending = data.RewardPending,
            RunKey = data.RunKey ?? "", LastCompletedRunKey = data.LastCompletedRunKey ?? "",
            Origin = data.RunOrigin ?? "", EmptyRunObservedAtUnixMs = data.EmptyRunObservedAtUnixMs,
            PreviousKey = data.RunPreviousKey ?? "",
            EmptyRunPreviousKey = data.EmptyRunPreviousKey ?? "",
            PendingGameKey = data.PendingGameKey ?? "", PendingGameWinsBefore = data.PendingGameWinsBefore,
            PendingGameLossesBefore = data.PendingGameLossesBefore,
            AutomaticGamesInRun = data.AutomaticGamesInRun, EntryRequestedAtUnixMs = data.EntryRequestedAtUnixMs,
            EntryReceipt = data.EntryReceipt?.Copy() ?? new ArenaEntryReceipt()
        };
        if (data.RunUnderground) data.UndergroundRun = snapshot;
        else data.NormalRun = snapshot;
    }

    internal static bool ObserveEmptyRun(ArenaStatsData data, string previousKey, long nowUnixMs)
    {
        if (data.RunActive) return false;
        // Persist a bounded proof before admission. Loading an existing deck
        // has no such proof and must not be imported as historical results.
        if (data.EmptyRunPreviousKey == previousKey && data.EmptyRunObservedAtUnixMs > 0 &&
            nowUnixMs - data.EmptyRunObservedAtUnixMs < 30000) return false;
        data.EmptyRunObservedAtUnixMs = nowUnixMs;
        data.EmptyRunPreviousKey = previousKey ?? "";
        return true;
    }

    internal static bool HasNewRunEvidence(ArenaStatsData data, string key, long nowUnixMs)
        => !data.RunActive && !string.IsNullOrEmpty(key) && key != data.LastCompletedRunKey &&
           !data.CountedRunKeys.Contains(key) && HasValidEntryReceipt(data.EntryReceipt, data.RunUnderground) &&
           (data.RunKey ?? "") == data.EntryReceipt.PreviousRunKey &&
           key != data.EntryReceipt.PreviousRunKey && key != data.EntryReceipt.PreviousNativeRunKey &&
           data.EntryReceipt.Phase == "bound" && data.EntryReceipt.BoundRunKey == key &&
           data.EntryReceipt.ResponseRunKey == key && data.EntryReceipt.BoundAtUnixMs >= data.EntryReceipt.ResponseAtUnixMs &&
           data.EntryReceipt.ResponseAtUnixMs >= data.EntryReceipt.RequestedAtUnixMs &&
           data.EntryReceipt.ResponseAtUnixMs - data.EntryReceipt.RequestedAtUnixMs <= EntryResponseWindowMs &&
           nowUnixMs >= data.EntryReceipt.BoundAtUnixMs;

    internal const long EntryResponseWindowMs = 90000;

    private static bool HasValidEntryReceipt(ArenaEntryReceipt receipt, bool underground)
        => receipt != null && Guid.TryParse(receipt.Nonce, out var nonce) && nonce != Guid.Empty && receipt.Underground == underground &&
           receipt.RequestedAtUnixMs > 0 && receipt.TicketCost > 0 && receipt.TicketsBefore >= receipt.TicketCost &&
           (string.IsNullOrEmpty(receipt.PreviousRunKey) || IsModeKey(receipt.PreviousRunKey, underground)) &&
           (string.IsNullOrEmpty(receipt.PreviousNativeRunKey) || IsModeKey(receipt.PreviousNativeRunKey, underground));

    private static bool IsModeKey(string key, bool underground)
        => !string.IsNullOrEmpty(key) && key.StartsWith(underground ? "underground:" : "normal:", StringComparison.Ordinal);

    internal static bool HasUnresolvedEntry(ArenaStatsData data)
        => data.EntryReceipt != null && (data.EntryReceipt.Phase == "prepared" ||
           data.EntryReceipt.Phase == "submission-uncertain" || data.EntryReceipt.Phase == "accepted" ||
           data.EntryReceipt.Phase == "response-seen" || data.EntryReceipt.Phase == "bound");

    internal static ArenaEntryReceipt OtherUnresolvedFinancialEntry(ArenaStatsData data)
    {
        var other = data.RunUnderground ? data.NormalRun?.EntryReceipt : data.UndergroundRun?.EntryReceipt;
        return IsUnboundFinancialEntry(other) ? other : null;
    }

    private static bool IsUnboundFinancialEntry(ArenaEntryReceipt receipt)
        => receipt != null && (receipt.Phase == "prepared" || receipt.Phase == "submission-uncertain" ||
            receipt.Phase == "accepted" || receipt.Phase == "response-seen");

    internal static bool PrepareEntry(ArenaStatsData data, string nonce, string previousKey,
        int ticketsBefore, int ticketCost, long nowUnixMs)
    {
        if (!data.ModeContextsInitialized || data.RunActive || data.RewardPending || HasUnresolvedEntry(data) ||
            OtherUnresolvedFinancialEntry(data) != null ||
            !Guid.TryParse(nonce, out var parsedNonce) || parsedNonce == Guid.Empty ||
            nowUnixMs <= 0 || ticketCost <= 0 || ticketsBefore < ticketCost ||
            data.EmptyRunPreviousKey != (previousKey ?? "") || data.EmptyRunObservedAtUnixMs <= 0 ||
            nowUnixMs < data.EmptyRunObservedAtUnixMs || nowUnixMs - data.EmptyRunObservedAtUnixMs > 30000 ||
            (!string.IsNullOrEmpty(previousKey) && !IsModeKey(previousKey, data.RunUnderground)) ||
            (!string.IsNullOrEmpty(data.RunKey) && !IsModeKey(data.RunKey, data.RunUnderground))) return false;
        data.EntryReceipt = new ArenaEntryReceipt { Nonce = nonce, Underground = data.RunUnderground,
            PreviousRunKey = data.RunKey ?? "", PreviousNativeRunKey = previousKey ?? "",
            RequestedAtUnixMs = nowUnixMs, TicketsBefore = ticketsBefore,
            TicketCost = ticketCost, Phase = "prepared" };
        data.EntryRequestedAtUnixMs = nowUnixMs;
        return true;
    }

    internal static bool MarkEntrySubmission(ArenaStatsData data, string nonce)
    {
        if (!HasValidEntryReceipt(data.EntryReceipt, data.RunUnderground) || data.EntryReceipt.Nonce != nonce ||
            data.EntryReceipt.Phase != "prepared") return false;
        data.EntryReceipt.Phase = "submission-uncertain";
        return true;
    }

    internal static bool MarkEntryAccepted(ArenaStatsData data, string nonce)
    {
        if (!HasValidEntryReceipt(data.EntryReceipt, data.RunUnderground) || data.EntryReceipt.Nonce != nonce ||
            data.EntryReceipt.Phase != "submission-uncertain") return false;
        data.EntryReceipt.Phase = "accepted";
        return true;
    }

    internal static void RestoreEntryReceipt(ArenaStatsData data, bool underground, ArenaEntryReceipt receipt)
    {
        if (data.RunUnderground == underground) data.EntryReceipt = receipt;
        else if (underground) data.UndergroundRun.EntryReceipt = receipt;
        else data.NormalRun.EntryReceipt = receipt;
    }

    internal static bool ObserveEntryEmptyAcknowledgement(ArenaStatsData data, bool underground,
        string state, bool serverActive, bool serverInRewards, long nowUnixMs)
    {
        if (!data.ModeContextsInitialized) return false;
        var receipt = data.RunUnderground == underground ? data.EntryReceipt :
            (underground ? data.UndergroundRun : data.NormalRun)?.EntryReceipt;
        if (!HasValidEntryReceipt(receipt, underground) ||
            (receipt.Phase != "prepared" && receipt.Phase != "submission-uncertain" && receipt.Phase != "accepted" &&
             receipt.Phase != "response-seen" && receipt.Phase != "bound")) return false;
        if (state != "NO_RUN" || serverActive || serverInRewards)
        {
            if (receipt.EmptyAcknowledgedAtUnixMs == 0) return false;
            receipt.EmptyAcknowledgedAtUnixMs = 0;
            return true;
        }
        // Only a real response received after the uncertain response window
        // can release spending. A cached pre-entry NO_RUN never qualifies.
        if (nowUnixMs <= receipt.RequestedAtUnixMs ||
            nowUnixMs - receipt.RequestedAtUnixMs < EntryResponseWindowMs ||
            receipt.EmptyAcknowledgedAtUnixMs > 0) return false;
        receipt.EmptyAcknowledgedAtUnixMs = nowUnixMs;
        return true;
    }

    // Called only by the native DraftManager.OnBegin response hook. Loading a
    // cached ArenaSession, or merely seeing a new deck, never enters this path.
    internal static bool ObserveEntryResponse(ArenaStatsData data, bool underground, string key,
        string state, bool serverActive, bool serverInRewards, int wins, int losses, int slot, int maxSlot,
        long nowUnixMs, string managerState = "", int heroChoiceCount = 0)
    {
        if (!data.ModeContextsInitialized) return false;
        var context = underground ? data.UndergroundRun : data.NormalRun;
        var selected = data.RunUnderground == underground;
        var receipt = selected ? data.EntryReceipt : context?.EntryReceipt;
        var active = selected ? data.RunActive : context?.Active == true;
        var rewardPending = selected ? data.RewardPending : context?.RewardPending == true;
        var lastCompleted = selected ? data.LastCompletedRunKey : context?.LastCompletedRunKey;
        var trackedPrevious = selected ? data.RunKey : context?.RunKey;
        if (active || rewardPending || !HasValidEntryReceipt(receipt, underground) || receipt.Phase != "accepted" ||
            (trackedPrevious ?? "") != receipt.PreviousRunKey ||
            !IsModeKey(key, underground) || key == receipt.PreviousRunKey || key == receipt.PreviousNativeRunKey || key == lastCompleted ||
            data.CountedRunKeys.Contains(key) ||
            (state != "DRAFTING" && !(state == "NO_RUN" && managerState == "DRAFTING" && heroChoiceCount > 0)) ||
            !serverActive || serverInRewards ||
            wins != 0 || losses != 0 || slot != 0 || maxSlot <= 0 ||
            nowUnixMs < receipt.RequestedAtUnixMs || nowUnixMs - receipt.RequestedAtUnixMs > EntryResponseWindowMs) return false;
        receipt.ResponseRunKey = key;
        receipt.ResponseAtUnixMs = nowUnixMs;
        receipt.Phase = "response-seen";
        return true;
    }

    // OnBegin sets the manager to DRAFTING before assigning its response
    // Session, whose State can still be NO_RUN. Project only the exact
    // captured identity, never generic cached sessions or reward states.
    internal static string EntryDraftState(ArenaEntryReceipt receipt, string key, string state,
        string managerState, bool active, bool rewards)
        => state == "NO_RUN" && managerState == "DRAFTING" && active && !rewards &&
           receipt != null && (receipt.Phase == "response-seen" || receipt.Phase == "bound") &&
           !string.IsNullOrEmpty(key) && receipt.ResponseRunKey == key ? "DRAFTING" : state;

    // An older version may have discarded the actual Begin response. A
    // stable live draft is enough to resume that existing run, but
    // cannot establish ownership of a payment or increment RunCount.
    internal static bool AttachPendingExistingDraft(ArenaStatsData data, string key, string state,
        string managerState, bool active, bool rewards, int wins, int losses,
        int slot, int maxSlot, bool nativeStable)
    {
        var receipt = data.EntryReceipt;
        if (!data.ModeContextsInitialized || data.RunActive || data.RewardPending || !nativeStable ||
            !HasValidEntryReceipt(receipt, data.RunUnderground) || receipt.Phase != "accepted" ||
            !string.IsNullOrEmpty(receipt.ResponseRunKey) || (data.RunKey ?? "") != receipt.PreviousRunKey ||
            !IsModeKey(key, data.RunUnderground) || key == receipt.PreviousRunKey ||
            key == receipt.PreviousNativeRunKey || key == data.LastCompletedRunKey ||
            data.CountedRunKeys.Contains(key) || managerState != "DRAFTING" ||
            (state != "DRAFTING" && state != "NO_RUN") || !active || rewards ||
            wins != 0 || losses != 0 || slot < 0 || slot > maxSlot || maxSlot <= 0) return false;
        return StartRun(data, key, wins, losses);
    }

    internal static bool BindEntryResponse(ArenaStatsData data, string key, string state, bool serverActive,
        bool serverInRewards, int wins, int losses, int slot, int maxSlot, bool nativeStable, long nowUnixMs)
    {
        var receipt = data.EntryReceipt;
        if (data.RunActive || data.RewardPending || !HasValidEntryReceipt(receipt, data.RunUnderground) ||
            (data.RunKey ?? "") != receipt.PreviousRunKey ||
            receipt.Phase != "response-seen" || receipt.ResponseRunKey != key || !IsModeKey(key, data.RunUnderground) ||
            key == receipt.PreviousRunKey || key == receipt.PreviousNativeRunKey || key == data.LastCompletedRunKey || data.CountedRunKeys.Contains(key) ||
            state != "DRAFTING" || !serverActive || serverInRewards || wins != 0 || losses != 0 ||
            slot < 0 || slot > maxSlot || maxSlot <= 0 || !nativeStable ||
            receipt.ResponseAtUnixMs < receipt.RequestedAtUnixMs ||
            receipt.ResponseAtUnixMs - receipt.RequestedAtUnixMs > EntryResponseWindowMs ||
            nowUnixMs < receipt.ResponseAtUnixMs) return false;
        // The response itself fixed this exact identity within the short
        // request window. Later stability/hero loading may take any duration.
        receipt.BoundRunKey = key;
        receipt.BoundAtUnixMs = nowUnixMs;
        receipt.Phase = "bound";
        return true;
    }

    internal static bool ResolveEntryAsEmpty(ArenaStatsData data, bool confirmedEmpty, long nowUnixMs)
    {
        if (!HasUnresolvedEntry(data) || data.RunActive || data.RewardPending || !confirmedEmpty ||
            nowUnixMs < data.EntryReceipt.RequestedAtUnixMs ||
            nowUnixMs - data.EntryReceipt.RequestedAtUnixMs < EntryResponseWindowMs ||
            data.EntryReceipt.EmptyAcknowledgedAtUnixMs < data.EntryReceipt.RequestedAtUnixMs + EntryResponseWindowMs ||
            nowUnixMs < data.EntryReceipt.EmptyAcknowledgedAtUnixMs ||
            nowUnixMs - data.EntryReceipt.EmptyAcknowledgedAtUnixMs < 10000) return false;
        data.EntryReceipt.Phase = "resolved-empty";
        data.EntryRequestedAtUnixMs = 0;
        return true;
    }

    internal static bool StartRun(ArenaStatsData data, string key, int wins, int losses,
        bool confirmedNewRun = false, bool replaceActiveRun = false)
    {
        var replacing = data.RunActive && !string.IsNullOrEmpty(data.RunKey) && data.RunKey != key;
        if (replacing)
        {
            if (!replaceActiveRun || string.IsNullOrEmpty(key) || key == data.LastCompletedRunKey) return false;
            // A complete different native identity proves only that the old
            // snapshot is stale. It does not prove when either run was paid
            // for, played, or rewarded, so retain all lifetime history.
            data.RunPreviousKey = data.RunKey;
            data.RunActive = false;
            confirmedNewRun = false;
        }
        if (data.RunActive)
        {
            if (!string.IsNullOrEmpty(data.RunKey) || string.IsNullOrEmpty(key)) return false;
            data.RunKey = key;
            // Binding a pre-key legacy marker is not a new counted run.
            // Use the verified native current score as its baseline without
            // adding or subtracting any lifetime history.
            data.CurrentRunWins = Math.Max(0, wins);
            data.CurrentRunLosses = Math.Max(0, losses);
            data.RunOrigin = "attached-legacy-existing";
            data.EmptyRunObservedAtUnixMs = 0;
            data.EmptyRunPreviousKey = "";
            return true;
        }
        if (string.IsNullOrEmpty(key) || key == data.LastCompletedRunKey) return false;
        confirmedNewRun = confirmedNewRun && !data.CountedRunKeys.Contains(key);
        if (confirmedNewRun) { data.CountedRunKeys.Add(key); data.RunCount++; }
        data.RunKey = key;
        data.RunOrigin = replacing ? "replaced-existing-baseline" :
            (confirmedNewRun ? "observed-new-entry" : "attached-existing-baseline");
        data.CurrentRunWins = confirmedNewRun ? 0 : Math.Max(0, wins);
        data.CurrentRunLosses = confirmedNewRun ? 0 : Math.Max(0, losses);
        data.RunActive = true;
        data.RewardPending = false;
        data.AutomaticGamesInRun = 0;
        data.EntryRequestedAtUnixMs = 0;
        if (HasUnresolvedEntry(data))
            data.EntryReceipt.Phase = confirmedNewRun && data.EntryReceipt.BoundRunKey == key ? "credited" : "attached-uncredited";
        data.EmptyRunObservedAtUnixMs = 0;
        data.EmptyRunPreviousKey = "";
        data.PendingGameKey = "";
        if (confirmedNewRun) Reconcile(data, wins, losses);
        return true;
    }

    internal static bool RetireMissingRun(ArenaStatsData data)
    {
        if (!data.RunActive) return false;
        data.RunActive = false;
        data.RewardPending = false;
        data.RunOrigin = "detached-server-empty";
        data.AutomaticGamesInRun = 0;
        data.EntryRequestedAtUnixMs = 0;
        data.EmptyRunObservedAtUnixMs = 0;
        data.EmptyRunPreviousKey = "";
        data.PendingGameKey = "";
        return true;
    }

    internal static bool Reconcile(ArenaStatsData data, int wins, int losses)
    {
        if (!data.RunActive) return false;
        var targetWins = ArenaResultPolicy.Reconcile(data.CurrentRunWins, wins);
        var targetLosses = ArenaResultPolicy.Reconcile(data.CurrentRunLosses, losses);
        if (targetWins == data.CurrentRunWins && targetLosses == data.CurrentRunLosses) return false;
        data.TotalWins += targetWins - data.CurrentRunWins;
        data.TotalLosses += targetLosses - data.CurrentRunLosses;
        data.CurrentRunWins = targetWins;
        data.CurrentRunLosses = targetLosses;
        data.HighestWins = Math.Max(data.HighestWins, targetWins);
        return true;
    }

    internal static bool BeginGame(ArenaStatsData data, string key)
    {
        if (!data.RunActive || string.IsNullOrEmpty(key) || data.PendingGameKey == key) return false;
        data.PendingGameKey = key;
        data.PendingGameWinsBefore = data.CurrentRunWins;
        data.PendingGameLossesBefore = data.CurrentRunLosses;
        data.AutomaticGamesInRun++;
        return true;
    }

    internal static bool FinishGame(ArenaStatsData data, string key, bool win)
    {
        if (!data.RunActive || string.IsNullOrEmpty(key) || data.PendingGameKey != key ||
            data.RecordedGameKeys.Contains(key)) return false;
        data.RecordedGameKeys.Add(key);
        if (data.RecordedGameKeys.Count > 256) data.RecordedGameKeys.RemoveAt(0);
        Reconcile(data, ArenaResultPolicy.ResultTarget(data.PendingGameWinsBefore, win),
            ArenaResultPolicy.ResultTarget(data.PendingGameLossesBefore, !win));
        return true;
    }

    internal static bool CompleteRun(ArenaStatsData data)
    {
        if (!data.RunActive || !data.RewardPending) return false;
        data.RewardsClaimed++;
        data.LastCompletedRunKey = data.RunKey;
        data.RunActive = false;
        data.RewardPending = false;
        data.AutomaticGamesInRun = 0;
        return true;
    }

    internal static bool DiscardLegacyPlaceholder(ArenaStatsData data)
    {
        if (!data.RunActive || data.RewardPending || !string.IsNullOrEmpty(data.RunKey)) return false;
        data.RunActive = false;
        data.AutomaticGamesInRun = 0;
        return true;
    }

    internal static bool ReconcilePurchase(ArenaStatsData data, int ticketsAfter, int goldAfter, long now)
    {
        if (!data.PurchasePending) return false;
        if (!ArenaResultPolicy.IsConfirmedPurchase(data.PurchaseTicketsBefore, ticketsAfter,
            data.PurchaseGoldBefore, goldAfter, true, out var spent))
        {
            if (now - data.PurchaseRequestedAtUnixMs < 90000) return false;
            data.PurchasePending = false;
            return true;
        }
        var changed = false;
        if (!data.PurchaseCounted)
        { data.PurchaseCounted = true; data.TicketsPurchased++; changed = true; }
        if (spent > data.PurchaseGoldCounted)
        {
            data.GoldSpent += spent - data.PurchaseGoldCounted;
            data.PurchaseGoldCounted = spent;
            changed = true;
        }
        if (spent > 0 || now - data.PurchaseRequestedAtUnixMs >= 30000)
        { data.PurchasePending = false; changed = true; }
        return changed;
    }
}
