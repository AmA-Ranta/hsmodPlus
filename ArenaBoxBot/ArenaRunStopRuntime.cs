using System;
using System.IO;
using System.Diagnostics;
using ArenaControl.Contracts;
using Newtonsoft.Json;
using UnityEngine;

namespace ArenaBoxBot;

internal sealed partial class ArenaController
{
    private ArenaRunStopRequest _controllerRunStop;
    private float _nextRunStopDiagnostic;
    private float _rewardsPageSince = -1f;
    private string _runStopObservedActiveKey = "";
    private static readonly Tuple<int, long> s_runStopGameIdentity = ReadRunStopGameIdentity();
    private static Tuple<int, long> ReadRunStopGameIdentity()
    {
        using (var game = Process.GetCurrentProcess())
            return Tuple.Create(game.Id, game.StartTime.ToUniversalTime().Ticks);
    }
    private static string ControllerRunStopPath => ArenaRuntimePaths.File(ArenaRunStopContract.RequestFile);
    private static string ControllerRunStopReceiptPath => ArenaRuntimePaths.File(ArenaRunStopContract.ReceiptFile);

    private bool HasControllerRunStop()
    {
        try
        {
            if (!File.Exists(ControllerRunStopPath)) { _controllerRunStop = null; return false; }
            var request = JsonConvert.DeserializeObject<ArenaRunStopRequest>(File.ReadAllText(ControllerRunStopPath));
                if (!ArenaRunStopContract.Matches(request, s_runStopGameIdentity.Item1, s_runStopGameIdentity.Item2,
                    ArenaSettings.IsUnderground, DateTime.UtcNow.Ticks)) { _controllerRunStop = null; return false; }
            _controllerRunStop = request;
            return true;
        }
        catch { _controllerRunStop = null; return false; }
    }

    // Invoked after the existing reward receipt/statistics observer and before
    // any new run admission. A mid-run loss or redraft never enters this gate.
    private bool TryFinishControllerRunStop(SceneMgr scene, DraftManager manager, string state)
    {
        if (!HasControllerRunStop()) return false;
        if (_runStopRequested) return false; // Existing stop gate owns teardown.
        if (string.IsNullOrEmpty(_controllerRunStop.RunKey))
        {
            // Armed for the next run: the controller pins a run identity only
            // once a run is actually active.  Treating the current empty lobby
            // as a finished run would complete the request before one match.
            if (Time.unscaledTime >= _nextRunStopDiagnostic)
            {
                _nextRunStopDiagnostic = Time.unscaledTime + 5f;
                Log("controller stop-after-run armed; waiting for the next run identity");
            }
            return false;
        }
        s_arenaOverlayStopReason = "控制端请求：本轮结束并完成领奖后停止";
        var data = ArenaStatsStore.Data;
        // Only a run that was actually seen active can be declared finished
        // later; otherwise a stale pinned key could stop before one match.
        if (data.RunActive && data.RunKey == _controllerRunStop.RunKey) _runStopObservedActiveKey = data.RunKey;
        var ready = TryGetVerifiedNativeSession(manager, ArenaSettings.IsUnderground, out var session) &&
                    manager.IsUnderground() == ArenaSettings.IsUnderground;
        var chestReady = manager.IsRewardChestReady();
        var empty = ready && _lifecycle.CanObserveEmptyRun(session.State.ToString(), true, session.IsActive,
            ArenaSessionStatePolicy.BlocksEmptySessionFromRewards(state, session.State.ToString(),
                session.HasIsInRewards && session.IsInRewards), chestReady, Time.unscaledTime);
        var display = GetDraftDisplay();
        var displayLoaded = display != null && display.IsLoadingComplete();
        var rewardUiPending = display == null || display.IsRewardsFlowPending;
        // The entry path already handles an inactive cached REWARDS session
        // after the acknowledged manager has reached NO_RUN. A pending round
        // stop deliberately prevents entering that path, so use the same
        // bounded proof here without admitting a ticket or altering a receipt.
        // A missing display and unconfirmed financial/result state stay closed.
        var completedRunMatches = string.IsNullOrEmpty(_controllerRunStop.RunKey) ||
                                  _controllerRunStop.RunKey == data.LastCompletedRunKey;
        var staleProof = _lifecycle.CanRecoverStaleRewardSession(state,
            session == null ? "UNKNOWN" : session.State.ToString(),
            ready && displayLoaded && !data.RunActive && !data.RewardPending &&
            !data.PurchasePending && completedRunMatches,
            session == null || session.IsActive, rewardUiPending, chestReady, Time.unscaledTime);
        var staleRewardEmpty = !empty && staleProof;
        // The completed-run key can be missing (the third loss ends the run on
        // the rewards screen), which used to leave this gate waiting forever.
        // Positive evidence: the requested run was active, and the rewards page
        // now shows no active run, no pending reward and no pending purchase.
        var onRewardsPage = state == "NO_RUN" && ready && session != null &&
                            session.State.ToString() == "REWARDS" && !session.IsActive &&
                            !scene.IsTransitioning() && displayLoaded &&
                            !data.RewardPending && !data.PurchasePending && !data.RunActive;
        _rewardsPageSince = onRewardsPage ? (_rewardsPageSince < 0 ? Time.unscaledTime : _rewardsPageSince) : -1f;
        var rewardsPageDwell = _rewardsPageSince < 0 ? 0d : Time.unscaledTime - _rewardsPageSince;
        var completedOnRewardsPage = ArenaRunStopContract.CanCompleteOnRewardsPage(state, ready,
            session == null ? "UNKNOWN" : session.State.ToString(), session != null && session.IsActive,
            scene.IsTransitioning(), displayLoaded, data.RewardPending, data.PurchasePending, data.RunActive,
            _runStopObservedActiveKey == _controllerRunStop.RunKey, rewardsPageDwell);
        empty |= staleRewardEmpty;
        if (completedOnRewardsPage || ArenaRunStopContract.CanComplete(scene.GetMode().ToString(), scene.GetNextMode().ToString(),
            scene.IsTransitioning(), empty, data.RunActive, data.RewardPending, data.PurchasePending,
            _controllerRunStop.RunKey, data.LastCompletedRunKey))
        {
            try
            {
                _controllerRunStop.CompletedAtUtcTicks = DateTime.UtcNow.Ticks;
                var pending = ControllerRunStopReceiptPath + ".pending";
                File.WriteAllText(pending, JsonConvert.SerializeObject(_controllerRunStop));
                if (File.Exists(ControllerRunStopReceiptPath)) File.Delete(ControllerRunStopReceiptPath);
                File.Move(pending, ControllerRunStopReceiptPath);
                RequestArenaStop("控制端请求：本轮结束并领奖后停止");
                Log("controller stop-after-run completed: requestId=" + _controllerRunStop.RequestId +
                    "; runKey=" + _controllerRunStop.RunKey + "; completedRunKey=" + data.LastCompletedRunKey +
                    "; nativeSession=" + session.State + "; staleRewardEmpty=" + staleRewardEmpty +
                    "; rewardsPageDwell=" + (int)rewardsPageDwell + "; completedOnRewardsPage=" + completedOnRewardsPage);
                return false;
            }
            catch (Exception ex) { Log("Arena run-stop receipt not persisted; retaining entry hold: " + ex.Message); }
            return true;
        }
        if (!ArenaRunStopContract.HoldEntry(state, manager.HasCurrentlyActiveRun(ArenaSettings.IsUnderground))) return false;
        if (Time.unscaledTime >= _nextRunStopDiagnostic)
        {
            _nextRunStopDiagnostic = Time.unscaledTime + 5f;
            Log("controller stop-after-run waiting: requestId=" + _controllerRunStop.RequestId +
                "; managerState=" + state + "; nativeReady=" + ready +
                "; nativeSession=" + (session == null ? "missing" : session.State.ToString()) +
                "; nativeActive=" + (session != null && session.IsActive) +
                "; displayLoaded=" + displayLoaded + "; rewardUiPending=" + rewardUiPending +
                "; chestReady=" + chestReady + "; trackedActive=" + data.RunActive +
                "; rewardPending=" + data.RewardPending + "; purchasePending=" + data.PurchasePending +
                "; completedRunMatches=" + completedRunMatches + "; empty=" + empty +
                "; scene=" + scene.GetMode() + "; next=" + scene.GetNextMode() +
                "; transitioning=" + scene.IsTransitioning() +
                "; rewardsPageDwell=" + (int)rewardsPageDwell + "; observedActive=" +
                (_runStopObservedActiveKey == _controllerRunStop.RunKey));
        }
        PublishRuntimeState(true, true, "DRAFT", false, "RUN_STOP_RECEIPT_PENDING", manager);
        return true;
    }
}
