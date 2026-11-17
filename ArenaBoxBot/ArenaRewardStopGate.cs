namespace ArenaBoxBot;

/// <summary>Keep the existing native receipt observation alive before releasing a completed round.</summary>
internal static class ArenaRewardStopGate
{
    internal const string PendingState = "REWARDS_RECEIPT_PENDING";
    internal static bool MustWaitForReceipt(string nativeState, bool rewardPending)
        => nativeState == "NO_RUN" && rewardPending;
}
