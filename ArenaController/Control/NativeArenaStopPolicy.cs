namespace ArenaController;

/// <summary>Honors automatic Arena stops without closing active games, rewards, or transactions.</summary>
public static class NativeArenaStopPolicy
{
    public static bool IsAutomaticCloseCandidate(bool closeAfterStop, string? stopReason)
        => closeAfterStop && !string.IsNullOrWhiteSpace(stopReason);

    public static bool CanFinish(bool boundarySafe, bool transactionFactsKnown,
        bool rewardPending, bool purchasePending, string? entryPhase)
        => boundarySafe && transactionFactsKnown && !rewardPending && !purchasePending &&
           entryPhase is "" or "credited" or "empty";
}
