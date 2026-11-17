using System;

namespace ArenaControl.Contracts;

[Serializable]
public sealed class ArenaRunStopRequest
{
    public int SchemaVersion = 1;
    public string RequestId = "";
    public int GameProcessId;
    public long GameStartedAtUtcTicks;
    public long RequestedAtUtcTicks;
    public bool Underground;
    public string RunKey = "";
    public long CompletedAtUtcTicks;
}

public static class ArenaRunStopContract
{
    public const string RequestFile = "controller-arena-stop-after-run.json";
    public const string ReceiptFile = "controller-arena-stop-after-run-completed.json";

    // A run that reaches the rewards page with no active session cannot always
    // produce a matching completed-run key: the native ledger can drop that key
    // (three losses end the run on the rewards screen).  Waiting on the key
    // alone hung a stop-after-run request forever (observed 2026-11-09 19:18:
    // managerState=NO_RUN, session=REWARDS, inactive, still waiting 10 min
    // later).  Positive evidence that the requested run was active and is now
    // finished on the rewards page completes the request instead.
    public const double RewardsPageDwellSeconds = 60;

    public static bool CanCompleteOnRewardsPage(string managerState, bool nativeReady, string nativeSession,
        bool nativeActive, bool transitioning, bool displayLoaded, bool rewardPending, bool purchasePending,
        bool trackedRunActive, bool requestedRunObservedActive, double rewardsPageDwellSeconds)
        => managerState == "NO_RUN" && nativeReady && nativeSession == "REWARDS" && !nativeActive &&
           !transitioning && displayLoaded && !rewardPending && !purchasePending && !trackedRunActive &&
           requestedRunObservedActive && rewardsPageDwellSeconds >= RewardsPageDwellSeconds;
    public static bool Matches(ArenaRunStopRequest request, int pid, long started, bool underground, long now)
        => request != null && request.SchemaVersion == 1 &&
           Guid.TryParse(request.RequestId, out var id) && id != Guid.Empty &&
           pid > 0 && request.GameProcessId == pid && started > 0 && request.GameStartedAtUtcTicks == started &&
           request.RequestedAtUtcTicks >= started && request.RequestedAtUtcTicks <= now &&
           request.Underground == underground;

    public static bool CanComplete(string scene, string nextScene, bool transitioning, bool nativeEmptyConfirmed,
        bool trackedRunActive, bool rewardPending, bool purchasePending, string expectedRun, string completedRun)
        => scene == "DRAFT" && nextScene == "INVALID" && !transitioning && nativeEmptyConfirmed &&
           !trackedRunActive && !rewardPending && !purchasePending &&
           (string.IsNullOrEmpty(expectedRun) || expectedRun == completedRun);

    // DRAFTING/MIDRUN/redraft and gameplay remain playable. Only the empty-run
    // entry path is held, so a loading placeholder cannot buy the next ticket.
    public static bool HoldEntry(string state, bool nativeRunActive)
        => state == "NO_RUN" || state == "INVALID" || (state == "EDITING_DECK" && !nativeRunActive);

    public static bool Confirms(ArenaRunStopRequest expected, ArenaRunStopRequest completed,
        int pid, long started, bool underground, long now)
        => Matches(expected, pid, started, underground, now) && Matches(completed, pid, started, underground, now) &&
           completed.RequestId == expected.RequestId && completed.RequestedAtUtcTicks == expected.RequestedAtUtcTicks &&
           completed.RunKey == expected.RunKey && completed.CompletedAtUtcTicks > expected.RequestedAtUtcTicks &&
           completed.CompletedAtUtcTicks <= now;
}
