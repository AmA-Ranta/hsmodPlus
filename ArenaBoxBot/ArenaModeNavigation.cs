using System;
using System.Linq;
using Hearthstone.UI;
using Hearthstone.DataModels;
using UnityEngine;

namespace ArenaBoxBot;

internal sealed partial class ArenaController
{
    private bool? _modeNavigationTarget;
    private int _modeNavigationLandingId;
    private bool _modeNavigationConfirmed;
    private int _modeNavigationReloads;
    private float _modeNavigationWaitSince = -1f;
    private float _modeNavigationNextLog;
    private readonly ArenaDraftVisualGate _modeNavigationStable = new();

    private void TrackArenaNavigationTarget(bool target)
    {
        if (_modeNavigationTarget == target) return;
        _modeNavigationTarget = target;
        _modeNavigationLandingId = 0;
        _modeNavigationConfirmed = false;
        _modeNavigationReloads = 0;
        _modeNavigationWaitSince = -1f;
        _modeNavigationStable.Reset();
        _undergroundRequested = false;
    }

    private bool PrepareNativeArenaModeBeforeEntry(DraftManager manager)
    {
        var target = ArenaSettings.IsUnderground;
        TrackArenaNavigationTarget(target);
        if (target && !GameModeUtils.HasUnlockedMode(Assets.Global.UnlockableGameMode.UNDERGROUND_ARENA))
        {
            RequestArenaStop("地下竞技场尚未解锁，未切换或购买门票");
            return false;
        }
        // ArenaLandingPageManager.Awake chooses StartNormal/StartUnderground
        // from this flag. It must be set before constructing the Draft scene.
        if (manager.IsUnderground() != target) manager.SetUndergroundState(target);
        _modeNavigationConfirmed = false;
        _modeNavigationLandingId = 0;
        _modeNavigationWaitSince = Time.unscaledTime;
        _modeNavigationStable.Reset();
        Log("prepared native Arena mode before page creation: target=" + (target ? "underground" : "normal"));
        return true;
    }

    private bool EnsureNativeArenaModePage(DraftManager manager)
    {
        var target = ArenaSettings.IsUnderground;
        TrackArenaNavigationTarget(target);
        if (_modeNavigationWaitSince < 0f) _modeNavigationWaitSince = Time.unscaledTime;
        if (_modeNavigationConfirmed && !_undergroundRequested)
        {
            _modeNavigationConfirmed = false;
            _modeNavigationWaitSince = Time.unscaledTime;
            _modeNavigationStable.Reset();
        }
        try
        {
            var landing = ArenaLandingPageManager.Get();
            var widget = landing?.GetWidget();
            if (landing == null || widget == null || !landing.IsLoadingComplete() || !widget.IsReady)
                return HoldNativeArenaMode("waiting for native landing widgets to finish loading");
            if (_modeNavigationLandingId != landing.GetInstanceID())
            {
                _modeNavigationLandingId = landing.GetInstanceID();
                _modeNavigationConfirmed = false;
                _modeNavigationWaitSince = Time.unscaledTime;
                _modeNavigationStable.Reset();
            }
            var model = PopupField(landing, "m_arenaLandingPageDataModel") as ArenaLandingPageDataModel;
            var expectedSession = PopupField(landing, target ? "m_undergroundSessionDataModel" : "m_normalSessionDataModel");
            var boundSession = widget.GetDataModel<ArenaSessionDataModel>();
            if (model == null || expectedSession == null)
                return HoldNativeArenaMode("native landing mode/session model is unavailable");
            var managerMode = manager.IsUnderground();
            var viewMode = model.inUnderground;
            var clientState = manager.GetCurrentClientState().ToString();
            var clientModeMatches = clientState.StartsWith(target ? "Underground_" : "Normal_", StringComparison.Ordinal);
            var sessionBound = ReferenceEquals(boundSession, expectedSession);
            // Once this exact page has completed the native mode transition,
            // later drafting/reward animations keep their existing gates.
            if (_modeNavigationConfirmed && managerMode == target && viewMode == target && sessionBound && clientModeMatches)
            {
                _modeNavigationWaitSince = -1f;
                return true;
            }
            if (_modeNavigationConfirmed)
            {
                _modeNavigationConfirmed = false;
                _modeNavigationWaitSince = Time.unscaledTime;
                _modeNavigationStable.Reset();
            }
            var onLanding = clientState.EndsWith("_Landing", StringComparison.Ordinal);
            var template = widget as WidgetTemplate ?? (widget as WidgetInstance)?.Widget;
            var visualStates = template == null ? "" : string.Join(",",
                (PopupField(template, "m_visualControllers") as System.Collections.IEnumerable)?.Cast<VisualController>()
                    .Select(controller => controller.State) ?? Enumerable.Empty<string>());
            var changing = widget.IsChangingStates || GameUtils.IsAnyTransitionActive();
            var snapshot = managerMode + ":" + viewMode + ":" + sessionBound + ":" + clientState + ":" + visualStates;
            var stable = _modeNavigationStable.Observe("ArenaMode:" + target, landing.GetInstanceID() + ":" + snapshot,
                new[] { (float)landing.GetInstanceID() }, !changing, Time.unscaledTime, Time.frameCount);
            var action = ArenaModeNavigationPolicy.Decide(target, managerMode, viewMode,
                sessionBound, clientModeMatches, onLanding, stable);
            var detail = "target=" + (target ? "underground" : "normal") + ", manager=" + managerMode +
                ", landing=" + viewMode + ", sessionBound=" + sessionBound + ", client=" + clientState +
                ", changing=" + changing + ", visualStates=" + visualStates;
            if (action == ArenaModeNavigationAction.ToggleNative)
            {
                if (_runStopRequested) return HoldNativeArenaMode("stop requested; waiting at the current native mode page");
                if (target && !GameModeUtils.HasUnlockedMode(Assets.Global.UnlockableGameMode.UNDERGROUND_ARENA))
                {
                    RequestArenaStop("地下竞技场尚未解锁，原生页面无法切换");
                    return false;
                }
                // This is the native toggle button handler: it updates the
                // landing model, broadcasts ARENA_TOGGLE_CLICKED, binds the
                // right session and requests that mode's choices together.
                landing.ToggleUndergroundState();
                _modeNavigationConfirmed = false;
                _modeNavigationStable.Reset();
                ResetDraftSelection();
                _requestedChoices = false;
                Log("requested native Arena landing mode toggle; " + detail);
                return false;
            }
            if (action == ArenaModeNavigationAction.RecreatePage)
                return RecreateNativeArenaModePage(landing, detail);
            if (action == ArenaModeNavigationAction.Ready)
            {
                _modeNavigationConfirmed = true;
                _undergroundRequested = true;
                _modeNavigationWaitSince = -1f;
                Log("native Arena page mode confirmed after stable view binding; " + detail);
                return true;
            }
            return HoldNativeArenaMode(detail);
        }
        catch (Exception ex)
        {
            return HoldNativeArenaMode("native mode navigation failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool RecreateNativeArenaModePage(ArenaLandingPageManager landing, string detail)
    {
        if (_runStopRequested) return HoldNativeArenaMode("stop requested; retaining the current native mode page; " + detail);
        if (_modeNavigationReloads >= 1)
        {
            RequestArenaStop("竞技场数据与原生页面模式不一致，重建页面后仍未恢复；请查看模式导航日志");
            return false;
        }
        var display = DraftDisplay.Get();
        if (display != null && display.IsRewardsFlowPending)
            return HoldNativeArenaMode("waiting for native reward flow before rebuilding mismatched mode page; " + detail);
        var owner = display != null ? (object)display : landing;
        var exit = owner.GetType().GetMethod("ExitDraftScene", InstancePrivate);
        if (exit == null) return HoldNativeArenaMode("native ExitDraftScene is unavailable; " + detail);
        _modeNavigationReloads++;
        _modeNavigationConfirmed = false;
        _modeNavigationStable.Reset();
        _requestedDraft = false;
        _undergroundRequested = false;
        _requestedChoices = false;
        ResetDraftSelection();
        Log("rebuilding mismatched Arena mode page once through native ExitDraftScene; " + detail);
        exit.Invoke(owner, null);
        return false;
    }

    private bool TryCompleteNavigationStop(DraftManager manager)
    {
        if (!_runStopRequested || manager == null) return false;
        try
        {
            var scene = SceneMgr.Get();
            var game = GameMgr.Get();
            var network = Network.Get();
            if (scene == null || game == null || network == null || manager.CurrentSeason == null) return false;
            var currentMode = manager.IsUnderground();
            var currentState = manager.GetArenaState(currentMode).ToString();
            var selectedState = manager.GetArenaState(ArenaSettings.IsUnderground).ToString();
            var rewards = currentState == "REWARDS" || currentState == "IN_REWARDS" ||
                selectedState == "REWARDS" || selectedState == "IN_REWARDS" || manager.IsRewardChestReady() ||
                DraftDisplay.Get()?.IsRewardsFlowPending == true;
            if (!ArenaModeNavigationPolicy.CanCompleteStop(true, scene.GetMode().ToString(), scene.GetNextMode().ToString(),
                game.IsFindingGame(), network.IsConnectedToGameServer(), rewards,
                ArenaStopPolicy.IsSafeBoundary(currentState, manager.HasCurrentlyActiveRun(currentMode)))) return false;

            // A navigation failure returns before Update's ordinary stop
            // boundary. Consume the already requested stop here only after
            // independently proving that no game or reward is being left.
            CancelPopupOperations();
            ResetDraftSelection();
            if (_arenaAutomationEnabled) SetArenaAutomation(false);
            if (!HsModNativeArena.SetRuntimeEnabled(false) || HsModNativeArena.IsRuntimeEnabled())
            {
                if (Time.unscaledTime >= _modeNavigationNextLog)
                {
                    _modeNavigationNextLog = Time.unscaledTime + 10f;
                    Log("Arena navigation stop could not release the native runtime switch; stop remains pending");
                }
                return false;
            }
            s_arenaOverlayPhase = s_arenaOverlayStopReason = _runStopReason;
            try { System.IO.File.Delete(EnablePath); } catch { }
            try { System.IO.File.Delete(InternalStopPath); } catch { }
            try { System.IO.File.Delete(ControllerStopAfterGamePath); } catch { }
            Log("Arena navigation stop completed at a verified safe boundary: scene=" + scene.GetMode() +
                ", state=" + currentState + ", reason=" + _runStopReason);
            PublishRuntimeState(HsModNativeArena.IsNativeArenaModeSelected(), false, scene.GetMode().ToString(), false, "STOPPED", manager);
            return true;
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= _modeNavigationNextLog)
            {
                _modeNavigationNextLog = Time.unscaledTime + 10f;
                Log("Arena navigation stop is waiting for a verified boundary: " + ex.GetType().Name + ": " + ex.Message);
            }
            return false;
        }
    }

    private bool HoldNativeArenaMode(string reason)
    {
        s_arenaOverlayPhase = "等待竞技场页面与所选模式一致";
        if (Time.unscaledTime >= _modeNavigationNextLog)
        {
            _modeNavigationNextLog = Time.unscaledTime + 10f;
            Log("Arena mode navigation waiting: " + reason);
        }
        if (_modeNavigationWaitSince >= 0f && Time.unscaledTime - _modeNavigationWaitSince >= 120f)
            RequestArenaStop("竞技场模式页面超过 120 秒未就绪：" + reason);
        return false;
    }
}
