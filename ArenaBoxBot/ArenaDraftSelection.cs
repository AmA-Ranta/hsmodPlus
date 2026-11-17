using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ArenaBoxBot;

internal sealed partial class ArenaController
{
    private readonly ArenaDraftVisualGate _draftVisualGate = new ArenaDraftVisualGate();
    private readonly ArenaDraftWaitBudget _draftWaitBudget = new ArenaDraftWaitBudget();
    private readonly ArenaDraftVisualGate _draftEntryVisualGate = new ArenaDraftVisualGate();
    private readonly ArenaDraftEntryRetry _draftEntryRetry = new ArenaDraftEntryRetry();
    private string _draftEntryConfirmedContext;
    private float _nextDraftEntryDiagnostic;
    private float _nextStaleRewardDiagnostic;
    private string _pendingDraftContext;
    private string _pendingDraftVisualIdentity;
    private bool _draftRefreshAfterSubmission;
    private bool _draftConfirmationActive;
    private int _draftSelectionGeneration;
    private string _lastSubmittedVisualIdentity;
    private string _lastSubmittedContext;
    private static readonly Dictionary<string, FieldInfo> s_draftFields = new Dictionary<string, FieldInfo>();

    private static T DraftField<T>(DraftDisplay display, string name)
    {
        if (!s_draftFields.TryGetValue(name, out var field))
        {
            field = typeof(DraftDisplay).GetField(name, InstancePrivate);
            s_draftFields[name] = field;
        }
        if (field == null) throw new MissingFieldException(typeof(DraftDisplay).FullName, name);
        if (!typeof(T).IsAssignableFrom(field.FieldType))
            throw new InvalidOperationException("DraftDisplay." + name + " expected " + typeof(T).FullName +
                "; native field is " + field.FieldType.FullName);
        return (T)field.GetValue(display);
    }

    private void ResetDraftSelection()
    {
        // Coroutines capture this generation and may never confirm a dialog
        // after F12/off, a mode change, or a page-cycle reset.
        _draftSelectionGeneration++;
        _draftVisualGate.Reset();
        _draftWaitBudget.Reset();
        _draftEntryVisualGate.Reset();
        _draftEntryRetry.Reset();
        _draftEntryConfirmedContext = null;
        _nextDraftEntryDiagnostic = 0f;
        _pendingDraftContext = _pendingDraftVisualIdentity = null;
        _lastSubmittedVisualIdentity = _lastSubmittedContext = null;
        _draftRefreshAfterSubmission = _draftConfirmationActive = false;
    }

    private static string DraftContext(DraftManager manager)
    {
        var redraft = manager.IsRedrafting();
        var deck = redraft ? manager.GetRedraftDeck() : manager.GetDraftDeck(manager.IsUnderground());
        return (manager.IsUnderground() ? "U:" : "N:") + (deck?.ID ?? 0) +
            (redraft ? ":R:" : ":D:") + (redraft ? manager.GetRedraftSlot() : manager.GetSlot()) +
            ":" + manager.GetSlotType();
    }

    private bool IsRedraftSlotReady(DraftManager manager)
    {
        return manager != null && (!manager.IsRedrafting() ||
            ArenaRedraftPolicy.IsSlotReady(manager.GetRedraftSlot(), manager.GetMaxRedraftSlot()));
    }

    private bool EnsureNativeDraftSelectionEntry(DraftManager manager, string serverState)
    {
        if (!ArenaDraftEntryPolicy.IsSelectionState(serverState)) return true;
        var client = manager.GetCurrentClientState().ToString();
        var targetMode = ArenaSettings.IsUnderground;
        var modeMatches = manager.IsUnderground() == targetMode;
        var display = GetDraftDisplay();
        if (display == null || display != DraftDisplay.Get() || !display.isActiveAndEnabled ||
            !display.IsLoadingComplete() || display.IsRewardsFlowPending)
            return HoldNativeDraftEntry("等待原生选牌页面单例及加载完成", serverState, client);
        var context = (targetMode ? "U:" : "N:") + (manager.GetDraftDeck(targetMode)?.ID ?? 0) +
            ":display=" + display.GetInstanceID() + ":" + serverState;
        _draftEntryRetry.Observe(context);
        var landing = ArenaLandingPageManager.Get();
        var landingWidget = landing?.GetWidget();
        var transition = GameUtils.IsAnyTransitionActive();
        var landingReady = landing != null && landing.IsLoadingComplete() && landingWidget != null &&
            landingWidget.IsReady && landingWidget.IsActive && !landingWidget.IsChangingStates && !transition;
        var expectedClient = ArenaDraftEntryPolicy.SelectionClient(targetMode, serverState == "REDRAFTING");
        var shown = client == expectedClient && modeMatches &&
            DraftField<bool>(display, "m_areDraftCardsBeingShown");
        var selectionWidget = DraftField<Hearthstone.UI.Widget>(display, "m_arenaDraftScreenWidget");
        var nativeReady = shown && selectionWidget != null && selectionWidget.IsReady && selectionWidget.IsActive &&
            !selectionWidget.IsChangingStates && !transition && display.DraftAnimationIsComplete() &&
            !DraftField<bool>(display, "m_phaseMessageShowing");
        var pageState = display.m_arenaDraftScreenBroadcaster == null ? "missing" : display.m_arenaDraftScreenBroadcaster.State;
        var pageGeometry = new List<float>();
        if (nativeReady) AppendTransform(pageGeometry, display.transform);
        var stable = _draftEntryVisualGate.Observe(context, client + ":" + pageState, pageGeometry.ToArray(),
            nativeReady, Time.unscaledTime, Time.frameCount);
        var action = ArenaDraftEntryPolicy.Decide(serverState, client, targetMode, modeMatches,
            landingReady, shown, stable);
        if (action == ArenaDraftEntryAction.OpenDraftPage)
        {
            if (!ArenaSettings.AutoDraft.Value) return HoldNativeDraftEntry("自动选牌已关闭，等待手动继续", serverState, client);
            if (_draftEntryRetry.IsExhausted(Time.unscaledTime))
            {
                RequestArenaStop("竞技场原生继续页面未响应，已尝试 3 次；server=" + serverState + ", client=" + client);
                return false;
            }
            if (_draftEntryRetry.CanRequest(Time.unscaledTime))
            {
                // Arm before the event: even a callback that throws after
                // accepting the transition cannot be retried every frame.
                _draftEntryRetry.Requested(Time.unscaledTime);
                _draftEntryVisualGate.Reset();
                _draftVisualGate.Reset();
                var accepted = landingWidget.TriggerEvent("MOVETO_DRAFTING", default);
                Log("Arena native draft continuation requested: event=MOVETO_DRAFTING; accepted=" + accepted +
                    "; mode=" + (targetMode ? "underground" : "normal") + "; server=" + serverState +
                    "; client=" + client + "; context=" + context + "; tickets=" + manager.GetNumTicketsOwned() +
                    "; gold=" + NativeDraftEntryGold() + "; attempt=" + _draftEntryRetry.Attempts);
            }
            return HoldNativeDraftEntry("已请求原生继续，等待进入选牌并完成页面显示", serverState, client);
        }
        if (action == ArenaDraftEntryAction.Ready)
        {
            if (_draftEntryConfirmedContext != context)
            {
                _draftEntryConfirmedContext = context;
                Log("Arena native selection page confirmed: server=" + serverState + "; client=" + client +
                    "; shown=" + shown + "; page=" + pageState + "; stable=true; context=" + context);
            }
            return true;
        }
        return HoldNativeDraftEntry(action == ArenaDraftEntryAction.WaitForNativeRedraft
            ? "等待原生 TriggerRedraft 进入重选页面" : "等待原生选牌客户端、页面显示和动画持续稳定：shown=" + shown +
                ", nativeReady=" + nativeReady + ", transition=" + transition + ", page=" + pageState,
            serverState, client);
    }

    private bool HoldNativeDraftEntry(string reason, string serverState, string client)
    {
        if (Time.unscaledTime >= _nextDraftEntryDiagnostic)
        {
            _nextDraftEntryDiagnostic = Time.unscaledTime + 10f;
            Log("Arena 原生选牌入页等待：" + reason + "; server=" + serverState + "; client=" + client);
            if (reason == "等待足票且无进行中轮次的原生入场页面")
                LogNativeArenaEntryReadOnlyDiagnostic(serverState, client);
        }
        return false;
    }

    private void LogNativeArenaEntryReadOnlyDiagnostic(string serverState, string heldClient)
    {
        try
        {
            var manager = DraftManager.Get();
            var landing = ArenaLandingPageManager.Get();
            var widget = landing?.GetWidget();
            var display = GetDraftDisplay();
            var singleton = DraftDisplay.Get();
            var mode = ArenaSettings.IsUnderground;
            var now = Time.unscaledTime;
            var managerPresent = manager != null;
            var seasonPresent = managerPresent && manager.CurrentSeason != null;
            var client = managerPresent ? manager.GetCurrentClientState().ToString() : "missing";
            var expectedClient = mode ? "Underground_Landing" : "Normal_Landing";
            var landingPresent = landing != null;
            var landingLoaded = landingPresent && landing.IsLoadingComplete();
            var widgetPresent = widget != null;
            var widgetReady = widgetPresent && widget.IsReady;
            var widgetActive = widgetPresent && widget.IsActive;
            var widgetChanging = widgetPresent && widget.IsChangingStates;
            var displayPresent = display != null;
            var displayIsSingleton = displayPresent && display == singleton;
            var displayLoaded = displayPresent && display.IsLoadingComplete();
            var displayRewardsPending = displayPresent && display.IsRewardsFlowPending;
            var transition = GameUtils.IsAnyTransitionActive();
            var ready = landingPresent && landingLoaded && widgetPresent && widgetReady && widgetActive &&
                !widgetChanging && displayPresent && displayIsSingleton && displayLoaded &&
                !displayRewardsPending && !transition && client == expectedClient;
            var sessionField = mode ? s_undergroundSessionField : s_normalSessionField;
            sessionField ??= typeof(DraftManager).GetField(mode ? "m_currentUndergroundSession" :
                "m_currentNormalSession", InstancePrivate);
            var session = managerPresent ? sessionField?.GetValue(manager) as PegasusUtil.ArenaSession : null;
            var verifiedSession = TryGetVerifiedNativeSession(manager, mode, out _);
            var sessionState = session == null ? "missing" : session.State.ToString();
            var chestReady = managerPresent && manager.IsRewardChestReady();
            // CanObserveEmptyRun writes its evidence clock. Read the existing
            // clock instead so a throttled diagnostic cannot change a gate.
            var lifecycleState = (string)NativeArenaEntryDiagnosticField(_lifecycle, "_state");
            var lifecycleStateSince = (float)NativeArenaEntryDiagnosticField(_lifecycle, "_stateSince");
            var lifecycleEmptySince = (float)NativeArenaEntryDiagnosticField(_lifecycle, "_serverEmptySince");
            var lifecycleStable = now - lifecycleStateSince >= 3f;
            var lifecycleVerifiedEmpty = verifiedSession && lifecycleState == "NO_RUN" &&
                sessionState == "NO_RUN" && !session.IsActive &&
                !ArenaSessionStatePolicy.BlocksEmptySessionFromRewards(serverState, sessionState,
                    session.HasIsInRewards && session.IsInRewards) && !chestReady &&
                lifecycleStable && lifecycleEmptySince >= 0f && now - lifecycleEmptySince >= 3f;
            Log("Arena native entry read-only diagnostic: pid=" + System.Diagnostics.Process.GetCurrentProcess().Id +
                "; manager=" + managerPresent + "; season=" + seasonPresent +
                "; seasonId=" + (managerPresent ? manager.CurrentSeasonId : 0) +
                "; targetMode=" + (mode ? "underground" : "normal") +
                "; nativeMode=" + (managerPresent ? (manager.IsUnderground() ? "underground" : "normal") : "missing") +
                "; server=" + serverState + "; managerState=" + (managerPresent ?
                    NativeArenaEntryDiagnosticField(manager, mode ? "m_undergroundSessionState" : "m_normalSessionState") : "missing") +
                "; client=" + client + "; heldClient=" + heldClient + "; expectedClient=" + expectedClient +
                "; tickets=" + (managerPresent ? manager.GetNumTicketsOwned().ToString() : "missing") +
                "; cost=" + ArenaSettings.TicketCost +
                "; nativeActive=" + (managerPresent ? manager.HasCurrentlyActiveRun(mode).ToString() : "missing") +
                "; trackedActive=" + ArenaStatsStore.Data.RunActive + "; ready=" + ready +
                "; landing=" + landingPresent + "; landingLoaded=" + landingLoaded +
                "; widget=" + widgetPresent + "; widgetReady=" + widgetReady +
                "; widgetActive=" + widgetActive + "; widgetChanging=" + widgetChanging +
                "; display=" + displayPresent + "; displayId=" + (displayPresent ? display.GetInstanceID() : 0) +
                "; singletonId=" + (singleton != null ? singleton.GetInstanceID() : 0) +
                "; displayIsSingleton=" + displayIsSingleton + "; displayLoaded=" + displayLoaded +
                "; displayRewardsPending=" + displayRewardsPending + "; transition=" + transition +
                "; session=" + (session != null) + "; sessionField=" + (sessionField != null) +
                "; sessionHasState=" + (session != null && session.HasState) + "; sessionState=" + sessionState +
                "; sessionHasIsActive=" + (session != null && session.HasIsActive) +
                "; sessionActive=" + (session != null && session.IsActive) +
                "; sessionHasUnderground=" + (session != null && session.HasIsUnderground) +
                "; sessionUnderground=" + (session != null && session.IsUnderground) +
                "; sessionHasRewards=" + (session != null && session.HasIsInRewards) +
                "; sessionRewards=" + (session != null && session.IsInRewards) +
                "; verifiedSession=" + verifiedSession + "; chest=" + chestReady +
                "; lifecycleState=" + lifecycleState + "; lifecycleStable=" + lifecycleStable +
                "; lifecycleEmptySince=" + lifecycleEmptySince +
                "; lifecycleVerifiedEmpty=" + lifecycleVerifiedEmpty);
        }
        catch (Exception ex)
        {
            Log("Arena native entry read-only diagnostic unavailable: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static object NativeArenaEntryDiagnosticField(object instance, string name)
    {
        var field = instance?.GetType().GetField(name, InstancePrivate);
        if (field == null) throw new MissingFieldException(instance?.GetType().FullName ?? "missing", name);
        return field.GetValue(instance);
    }

    private bool TryBeginNativeArenaRun(DraftManager manager, string serverState)
    {
        var landing = ArenaLandingPageManager.Get();
        var widget = landing?.GetWidget();
        var display = GetDraftDisplay();
        var mode = ArenaSettings.IsUnderground;
        var ready = landing != null && landing.IsLoadingComplete() && widget != null && widget.IsReady &&
            widget.IsActive && !widget.IsChangingStates && display != null && display == DraftDisplay.Get() &&
            display.IsLoadingComplete() && !display.IsRewardsFlowPending && !GameUtils.IsAnyTransitionActive() &&
            manager.GetCurrentClientState().ToString() == (mode ? "Underground_Landing" : "Normal_Landing");
        var verifiedEmpty = TryGetVerifiedNativeSession(manager, mode, out var session) &&
            _lifecycle.CanObserveEmptyRun(session.State.ToString(), true, session.IsActive,
                ArenaSessionStatePolicy.BlocksEmptySessionFromRewards(manager.GetArenaState(mode).ToString(),
                    session.State.ToString(), session.HasIsInRewards && session.IsInRewards),
                manager.IsRewardChestReady(), Time.unscaledTime);
        // Some client builds keep the session object in REWARDS after the
        // server has already moved the manager to NO_RUN. Once the reward UI,
        // chest, and persisted reward marker are all clear, release only the
        // empty-session gate. The durable entry receipt remains untouched.
        if (!verifiedEmpty && session != null)
        {
            var managerState = manager.GetArenaState(mode).ToString();
            var rewardUiPending = display != null && display.IsRewardsFlowPending;
            var rewardChestReady = manager.IsRewardChestReady();
            if (_lifecycle.CanRecoverStaleRewardSession(managerState, session.State.ToString(),
                true, session.IsActive, rewardUiPending, rewardChestReady, Time.unscaledTime))
            {
                var rewardWasPending = ArenaStatsStore.Data.RewardPending;
                if (rewardWasPending)
                    CompleteArenaRunStats(true);
                verifiedEmpty = true;
                // The native session can stay in REWARDS after the server has
                // already reached NO_RUN.  Record the same bounded empty-run
                // proof used by the canonical NO_RUN path so PrepareEntry can
                // safely admit the next ticket without clearing the receipt.
                var emptyKey = GetNativeRunKey(manager, mode);
                var emptyAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!ArenaStatsStore.Data.RunActive &&
                    ArenaStatsLedger.ObserveEmptyRun(ArenaStatsStore.Data, emptyKey, emptyAt))
                {
                    ArenaStatsStore.Save();
                    Log("Arena stale reward session recorded safe empty proof: key=" + emptyKey +
                        "; receiptPreserved=true");
                }
                if (Time.unscaledTime >= _nextStaleRewardDiagnostic)
                {
                    _nextStaleRewardDiagnostic = Time.unscaledTime + 10f;
                    Log("Arena stale reward session released at safe empty boundary: managerState=" + managerState +
                    "; sessionState=" + session.State + "; active=" + session.IsActive +
                    "; rewardUiPending=" + rewardUiPending + "; chest=" + rewardChestReady +
                    "; rewardPendingCleared=" + rewardWasPending + "; receiptPreserved=true");
                }
            }
        }
        if (!ArenaDraftEntryPolicy.CanUseTicketEntry(serverState, manager.HasCurrentlyActiveRun(mode),
            ArenaStatsStore.Data.RunActive, manager.GetNumTicketsOwned(), ArenaSettings.TicketCost,
            ready, manager.IsUnderground() == mode, verifiedEmpty))
            return HoldNativeDraftEntry("等待足票且无进行中轮次的原生入场页面", serverState,
                manager.GetCurrentClientState().ToString());
        if (!ArenaLifecycleEvents.EntryReceiptHooksReady)
        {
            RequestArenaStop("原生竞技场入场响应记录不可用，禁止花票；请检查开发者日志");
            return false;
        }
        // Native SpendTicket owns MOVETO_DRAFTING -> DRAFTING -> DraftBegin.
        // RequestDraftBegin alone leaves the visual landing page unchanged.
        // Preserve both lifecycle and durable entry latches before invoking
        // the financial event; continuation retries never reuse SPEND_TICKET.
        var entryAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = Guid.NewGuid().ToString("N");
        var data = ArenaStatsStore.Data;
        var previousKey = GetNativeRunKey(manager, mode);
        if (!ArenaStatsLedger.PrepareEntry(data, nonce, previousKey, manager.GetNumTicketsOwned(),
            ArenaSettings.TicketCost, entryAt))
            return HoldNativeDraftEntry("等待已保存的同模式空轮证明及未决入场释放，禁止重复花票", serverState,
                manager.GetCurrentClientState().ToString());
        if (!ArenaStatsStore.SaveEntryReceipt(mode, data.EntryReceipt.Copy(), out var prepareFailure))
        {
            RequestArenaStop("入场记录保存未确认，禁止花票：" + prepareFailure);
            return false;
        }
        // Write the uncertain lock before executing the event. A crash at
        // any later instruction must not let the next launch spend again.
        if (!ArenaStatsLedger.MarkEntrySubmission(data, nonce) ||
            !ArenaStatsStore.SaveEntryReceipt(mode, data.EntryReceipt.Copy(), out var submitFailure))
        {
            RequestArenaStop("入场防重复记录未确认，禁止花票；path=" + ArenaStatsStore.Path);
            return false;
        }
        _lifecycle.BeginRequested(Time.unscaledTime);
        bool accepted;
        try
        {
            // SpendTicket synchronously calls Network.DraftBegin. Its
            // read-only postfix recognizes only this exact automatic scope;
            // a user's later manual request cannot upgrade our receipt.
            ArenaLifecycleEvents.BeginEntrySubmission(nonce, mode);
            accepted = widget.TriggerEvent("SPEND_TICKET", default);
        }
        catch (Exception ex)
        {
            // The callback may already have submitted before throwing. Keep
            // the durable uncertain lock, and never manufacture a receipt.
            Log("Arena ticket entry event outcome uncertain: nonce=" + nonce + "; " + ex.GetType().Name + ": " + ex.Message);
            RequestArenaStop("原生入场调用异常，结果未确认；保留防重复锁，请核对门票及日志");
            return false;
        }
        finally { ArenaLifecycleEvents.EndEntrySubmission(); }
        if (!string.IsNullOrEmpty(s_entryReceiptWriteFailure))
        {
            RequestArenaStop("原生入场提交记录保存未确认；可能已花票，禁止重复入场：" + s_entryReceiptWriteFailure);
            return false;
        }
        Log("Arena ticket balance ready; requested native SPEND_TICKET entry; accepted=" + accepted +
            "; mode=" + (mode ? "underground" : "normal") + "; server=" + serverState +
            "; client=" + manager.GetCurrentClientState() + "; deckId=" + (manager.GetDraftDeck(mode)?.ID ?? 0) +
            "; displayId=" + display.GetInstanceID() + "; tickets=" + manager.GetNumTicketsOwned() +
            "; gold=" + NativeDraftEntryGold() + "; nonce=" + nonce + "; durablePhase=" + data.EntryReceipt.Phase);
        return accepted;
    }

    private static string NativeDraftEntryGold()
    {
        return TryGetGoldBalance(out var balance) ? balance.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
    }

    private void TryDraftChoice(DraftManager manager)
    {
        if (manager == null || !HsModNativeArena.IsNativeArenaAutomationRunning() || !IsRedraftSlotReady(manager)) return;
        if (!manager.IsRedrafting() && manager.GetMaxSlot() > 0 && manager.GetSlot() > manager.GetMaxSlot()) return;
        var context = DraftContext(manager);
        if (_pendingDraftContext != null && _pendingDraftContext != context)
        {
            Log("Arena choice server context advanced: " + _pendingDraftContext + " -> " + context);
            _pendingDraftContext = _pendingDraftVisualIdentity = null;
            _draftConfirmationActive = _draftRefreshAfterSubmission = false;
            _draftSelectionGeneration++;
            _draftVisualGate.Reset();
            _draftRecommendationPaceKey = "";
        }

        var display = GetDraftDisplay();
        if (display == null) { HoldDraftChoice(context, "display=null", "选牌界面尚未加载"); return; }
        EnsureChoiceReflection(display);
        var choices = s_choicesField?.GetValue(display) as IList;
        if (choices == null || choices.Count == 0)
        {
            HoldDraftChoice(context, "choices=0", "等待服务器选项和卡牌资源");
            return;
        }
        var cards = new List<string>(choices.Count);
        var bindingProgress = new List<string>();
        foreach (var item in choices)
        {
            var actor = s_actorField?.GetValue(item) as Actor;
            var id = s_cardIdField?.GetValue(item) as string ?? "";
            cards.Add(id);
            bindingProgress.Add(id + ":" + (actor == null ? "missing" : actor.GetInstanceID().ToString()));
        }
        var bucket = string.Join(",", cards);
        var nativeReady = TryReadDraftVisuals(display, choices, out var visuals, out var identity, out var geometry, out var reason);
        var progress = string.Join(",", bindingProgress) + ":" + reason;

        if (_pendingDraftContext == context)
        {
            if (_draftConfirmationActive) return;
            // A local chosen latch is not a server acknowledgement. Until a
            // server refresh has actually replaced the visual objects, never
            // click the same slot again, even if a fixed timeout has elapsed.
            if (_draftRefreshAfterSubmission && nativeReady && identity != _pendingDraftVisualIdentity)
            {
                Log("Arena refreshed the unacknowledged choice into new native visuals; revalidating slot=" + context);
                _pendingDraftContext = _pendingDraftVisualIdentity = null;
                _draftRefreshAfterSubmission = false;
                _draftRecommendationPaceKey = "";
                _draftVisualGate.Reset();
            }
            else
            {
                HoldDraftChoice(context, "awaiting-server:" + _pendingDraftVisualIdentity, "等待选牌服务器确认");
                return;
            }
        }

        // Consecutive slots may legitimately contain the same card IDs.
        // Only reused actors prove that this is still the previous bucket.
        if (nativeReady && _lastSubmittedContext != context && identity == _lastSubmittedVisualIdentity)
        {
            HoldDraftChoice(context, progress, "服务器已推进，等待上一组选牌视觉退出");
            return;
        }
        if (cards.Any(string.IsNullOrEmpty))
        {
            HoldDraftChoice(context, progress, "等待全部选项卡牌标识");
            return;
        }
        if (!ArenaSettings.UseBoxScores.Value) return;
        var hero = manager.GetSlotType().ToString() == "DRAFT_SLOT_HERO" ||
                   manager.GetSlotType().ToString() == "DRAFT_SLOT_HERO_POWER";
        if (!hero && cards.All(id => id.StartsWith("HERO_", StringComparison.OrdinalIgnoreCase)))
        {
            HoldDraftChoice(context, progress, "等待旧英雄选项替换为当前卡牌");
            return;
        }

        // Read the bound Box strategy while assets load. A fast callback
        // does not bypass the independent visual readiness gate below.
        var choice = TryGetArenaBoxChoice(cards, manager, hero, out var pending, out var detail);
        var stable = _draftVisualGate.Observe(context + ":" + bucket, identity, geometry,
            nativeReady, Time.unscaledTime, Time.frameCount);
        if (!nativeReady) { HoldDraftChoice(context, progress, reason); return; }
        if (!stable)
        {
            _draftRecommendationPaceKey = "";
            HoldDraftChoice(context, progress, "卡牌已加载，等待布局和动画持续稳定", true, false);
            return;
        }
        if (pending || choice <= 0 || choice > choices.Count)
        {
            HoldDraftChoice(context, progress, "等待盒子推荐打法对当前选项的明确选卡：" + detail, false);
            return;
        }
        // Preserve the recommendation exactly. Picking a different actor
        // merely because it loaded earlier defeats the recommended strategy.
        var selected = visuals[choice - 1];
        if (!display.IsChoiceValid(choice) || selected.IsChosen())
        {
            HoldDraftChoice(context, progress, "推荐卡牌当前不可选，等待原生刷新");
            return;
        }
        if (!WaitForArenaRecommendationPace(context + ":" + identity + ":" + choice, hero ? "选英雄" : "选牌")) return;

        Log("BOX Arena native visual choice: slot=" + context + "; choice=" + choice +
            "; card=" + cards[choice - 1] + "; stableLayout=true; detail=" + detail);
        // Arm the pending state first. If a native callback throws after a
        // network request has been sent, it must not cause an immediate retry.
        _pendingDraftContext = context;
        _pendingDraftVisualIdentity = identity;
        _lastSubmittedContext = context;
        _lastSubmittedVisualIdentity = identity;
        _draftRefreshAfterSubmission = false;
        _draftWaitBudget.Observe(context, "awaiting-server:" + identity, Time.unscaledTime);
        _draftVisualGate.Reset();
        _draftRecommendationPaceKey = "";
        try
        {
            selected.ChooseThisCard();
            if (hero)
            {
                var zoomed = DraftField<DraftCardVisual>(display, "m_zoomedHero");
                if (zoomed == null || zoomed.GetChoiceNum() != choice)
                {
                    Log("Arena hero visual ignored the click; retaining the slot latch until native refresh");
                    return;
                }
                _draftConfirmationActive = true;
                StartCoroutine(ConfirmDraftChoice(display, context, _draftSelectionGeneration, choice));
            }
            else if (!selected.IsChosen())
            {
                Log("Arena visual ignored the click; retaining the slot latch until native refresh; no direct network fallback was sent");
            }
            else if (display.DoesChoiceHavePackageCards(choice - 1))
            {
                _draftConfirmationActive = true;
                StartCoroutine(ConfirmPackageChoice(display, context, _draftSelectionGeneration, choice));
            }
        }
        catch (Exception ex)
        {
            Log("Arena native visual choice failed; awaiting server state before any retry: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool TryReadDraftVisuals(DraftDisplay display, IList choices, out List<DraftCardVisual> visuals,
        out string identity, out float[] geometry, out string reason)
    {
        visuals = null;
        identity = "";
        geometry = null;
        reason = "等待原生选牌界面";
        try
        {
            if (display != DraftDisplay.Get() ||
                DraftField<bool>(display, "m_choicesAreUnderground") != DraftManager.Get().IsUnderground())
            {
                reason = "等待原生选牌单例和竞技场类型与当前选项一致";
                return false;
            }
            var draftManager = DraftManager.Get();
            var expectedClient = ArenaDraftEntryPolicy.SelectionClient(draftManager.IsUnderground(), draftManager.IsRedrafting());
            if (draftManager.GetCurrentClientState().ToString() != expectedClient ||
                !DraftField<bool>(display, "m_areDraftCardsBeingShown"))
            {
                reason = "等待原生选牌页面实际显示：client=" + draftManager.GetCurrentClientState() +
                    ", expected=" + expectedClient;
                return false;
            }
            var loadingComplete = display.IsLoadingComplete();
            if (!display.isActiveAndEnabled || !loadingComplete || display.IsRewardsFlowPending)
            {
                reason = "等待选牌页面加载：active=" + display.isActiveAndEnabled + ", loaded=" +
                    loadingComplete + ", rewards=" + display.IsRewardsFlowPending;
                return false;
            }
            var transition = GameUtils.IsAnyTransitionActive();
            var animationComplete = display.DraftAnimationIsComplete();
            // The native coroutine sets this before waiting for its client
            // page, shown-state event, resources and positioning. It clears it
            // after PositionAndShowChoices; it is not an awaiting-choice flag.
            var building = DraftField<bool>(display, "m_isHandlingChoiceDisplay");
            var phaseMessage = DraftField<bool>(display, "m_phaseMessageShowing");
            if (transition || !animationComplete || building || phaseMessage)
            {
                reason = "等待原生场景/动画：transition=" + transition + ", animationComplete=" + animationComplete +
                    ", buildingChoices=" + building + ", phaseMessage=" + phaseMessage;
                return false;
            }

            visuals = display.GetCardVisuals();
            if (visuals == null || visuals.Count != choices.Count)
            { reason = "等待全部选牌视觉对象绑定：visuals=" + (visuals?.Count.ToString() ?? "null") + ", choices=" + choices.Count; return false; }
            var points = new List<float>();
            var ids = new List<string> { display.GetInstanceID().ToString() };
            var slotType = DraftManager.Get().GetSlotType().ToString();
            // These are public native fields, unlike the loading flags above.
            // Compile against their real types/accessibility: a NonPublic-only
            // reflection lookup falsely reports all three as missing. Native
            // PositionAndShowChoices/SetCardTransform use these same anchors.
            var bones = slotType == "DRAFT_SLOT_HERO" ? display.m_heroBones :
                slotType == "DRAFT_SLOT_HERO_POWER" ? display.m_heroPowerBones : display.m_cardBones;
            if (bones == null || bones.Count < visuals.Count)
            { reason = "等待原生卡牌布局锚点"; return false; }
            var positions = new List<Vector3>();
            for (var i = 0; i < visuals.Count; i++)
            {
                var visual = visuals[i];
                var actor = s_actorField?.GetValue(choices[i]) as Actor;
                if (visual == null || !visual.isActiveAndEnabled || actor == null ||
                    visual.GetActor() != actor || visual.GetChoiceNum() != i + 1 || !actor.HasCardDef)
                { reason = "等待选项 " + (i + 1) + " 的资源及点击对象"; return false; }
                // Hero powers use a subclass portrait collider whose actor
                // differs from the main recommendation card actor.
                var shownActor = visual.GetComponentInParent<Actor>();
                if (shownActor == null || !shownActor.gameObject.activeInHierarchy ||
                    !shownActor.HasCardDef || !shownActor.IsShown() || !shownActor.IsColliderEnabled())
                { reason = "等待选项 " + (i + 1) + " 完成显示"; return false; }
                var anchor = bones[i];
                var transform = shownActor.transform;
                if (anchor == null || Vector3.Distance(transform.position, anchor.position) > 0.025f ||
                    Vector3.Distance(transform.localScale, anchor.localScale) > 0.025f)
                {
                    reason = "等待选项 " + (i + 1) + " 移动到原生布局位置：position=" + FormatDraftVector(transform.position) +
                        ", anchor=" + (anchor == null ? "null" : FormatDraftVector(anchor.position)) +
                        ", scale=" + FormatDraftVector(transform.localScale) +
                        ", anchorScale=" + (anchor == null ? "null" : FormatDraftVector(anchor.localScale));
                    return false;
                }
                var overlapping = positions.FindIndex(p => Vector3.Distance(p, transform.position) < 0.025f);
                if (overlapping >= 0)
                {
                    reason = "卡牌布局重叠，等待原生布局完成：slotType=" + slotType + ", option=" + (i + 1) +
                        ", root=" + FormatDraftVector(transform.position) + ", anchor=" + FormatDraftVector(anchor.position) +
                        ", previousOption=" + (overlapping + 1) + ", previousRoot=" + FormatDraftVector(positions[overlapping]) +
                        ", previousAnchor=" + FormatDraftVector(bones[overlapping].position);
                    return false;
                }
                positions.Add(transform.position);
                AppendTransform(points, transform);
                ids.Add(actor.GetInstanceID() + "/" + visual.GetInstanceID() + "/" + shownActor.GetInstanceID());
            }
            identity = string.Join(",", ids);
            geometry = points.ToArray();
            reason = "native-visuals-ready";
            return true;
        }
        catch (Exception ex)
        {
            reason = "读取选牌视觉状态失败：" + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static void AppendTransform(List<float> values, Transform transform)
    {
        var p = transform.position;
        var s = transform.lossyScale;
        var r = transform.rotation;
        values.AddRange(new[] { p.x, p.y, p.z, s.x, s.y, s.z, r.x, r.y, r.z, r.w });
    }

    private static string FormatDraftVector(Vector3 value)
    {
        return value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void HoldDraftChoice(string context, string progress, string reason, bool allowRefresh = true,
        bool resetVisualGate = true)
    {
        if (resetVisualGate && allowRefresh)
        {
            _draftVisualGate.Reset();
            _draftRecommendationPaceKey = "";
        }
        _draftWaitBudget.Observe(context, progress, Time.unscaledTime);
        if (Time.unscaledTime >= _nextChoiceDiag)
        {
            _nextChoiceDiag = Time.unscaledTime + 10f;
            Log("Arena 选牌等待：" + reason + "; slot=" + context +
                "; nativeRefreshes=" + _draftWaitBudget.Refreshes);
        }
        if (!allowRefresh || _draftConfirmationActive) return;
        if (_draftWaitBudget.IsExhausted(Time.unscaledTime))
        {
            RequestArenaStop("选牌界面持续未就绪，已尝试 3 次原生刷新：" + reason + "；请检查游戏日志和卡牌资源");
            return;
        }
        if (!_draftWaitBudget.IsRefreshDue(Time.unscaledTime)) return;
        var network = Network.Get();
        if (network == null) return;
        network.RequestDraftChoicesAndContents(ArenaSettings.IsUnderground);
        _requestedChoices = true;
        _draftWaitBudget.Refreshed(Time.unscaledTime);
        _draftRefreshAfterSubmission = _pendingDraftContext == context;
        _draftVisualGate.Reset();
        _draftRecommendationPaceKey = "";
        Log("Arena 选牌长期无进展，已请求服务器刷新；未直接提交选择：" + reason +
            "; slot=" + context + "; attempt=" + _draftWaitBudget.Refreshes);
    }

    private bool WaitForArenaRecommendationPace(string key, string phase)
    {
        if (_draftRecommendationPaceKey != key)
        {
            _draftRecommendationPaceKey = key;
            var human = ArenaSettings.UseHumanDraftPace;
            var delay = GetHsModChoiceDelay(human, out var source);
            _draftRecommendationReadyAt = Time.unscaledTime + delay;
            Log("Arena " + phase + "：卡牌布局已稳定；操作节奏=" + (human ? "拟人" : "尽快") +
                "; wait=" + delay.ToString("0.00") + "s; source=" + source);
            return false;
        }
        return ArenaDraftPacing.IsReady(Time.unscaledTime, _draftRecommendationReadyAt);
    }

    private float GetHsModChoiceDelay(bool human, out string source)
    {
        // Arena needs reading time after the actual layout becomes stable;
        // constructed play's short click delay is only a lower bound here.
        var minimum = ArenaDraftPacing.NextDelay(human, s_draftPaceRandom);
        source = "Arena stable-layout pace";
        if (!s_hsModChoiceDelayUnavailable)
        {
            try
            {
                s_hsModUiActionDelayMethod ??= Type.GetType("HsMod.WildLadderBot, HsMod")?.GetMethod(
                    "UiActionDelay", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var parameter = s_hsModUiActionDelayMethod?.GetParameters().FirstOrDefault();
                if (parameter != null)
                {
                    s_hsModChoiceCategory ??= Enum.Parse(parameter.ParameterType, "Choice", true);
                    var value = Convert.ToSingle(s_hsModUiActionDelayMethod.Invoke(null, new[] { s_hsModChoiceCategory }));
                    if (!float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value <= 10f)
                    {
                        source += "+HsMod Choice";
                        return Math.Max(minimum, value);
                    }
                }
            }
            catch (Exception ex)
            {
                s_hsModChoiceDelayUnavailable = true;
                Log("HsMod Choice timing unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        return minimum;
    }

    private bool IsDraftConfirmationCurrent(DraftDisplay display, string context, int generation)
    {
        return generation == _draftSelectionGeneration && HsModNativeArena.IsNativeArenaAutomationRunning() &&
            !_runStopRequested && !File.Exists(ControllerStopAfterGamePath) && ArenaSettings.AutoDraft.Value &&
            SceneMgr.Get() != null && SceneMgr.Get().GetMode() == SceneMgr.Mode.DRAFT &&
            display != null && display == GetDraftDisplay() && display == DraftDisplay.Get() && DraftManager.Get() != null &&
            (!DraftManager.Get().IsRedrafting() || ArenaSettings.AutoRedraft.Value) &&
            context == DraftContext(DraftManager.Get()) && _pendingDraftContext == context;
    }

    private IEnumerator ConfirmDraftChoice(DraftDisplay display, string context, int generation, int choice)
    {
        var gate = new ArenaDraftVisualGate();
        var started = Time.unscaledTime;
        while (Time.unscaledTime - started < 60f && IsDraftConfirmationCurrent(display, context, generation))
        {
            yield return new WaitForSecondsRealtime(0.25f);
            if (!IsDraftConfirmationCurrent(display, context, generation)) break;
            try
            {
                var button = DraftField<NormalButton>(display, "m_confirmButton");
                var zoomed = DraftField<DraftCardVisual>(display, "m_zoomedHero");
                var actor = zoomed == null ? null : zoomed.GetComponentInParent<Actor>();
                var ready = button != null && button.gameObject.activeInHierarchy && button.IsEnabled() &&
                    zoomed != null && zoomed.GetChoiceNum() == choice && actor != null && actor.HasCardDef &&
                    !GameUtils.IsAnyTransitionActive();
                var geometry = new List<float>();
                if (ready) { AppendTransform(geometry, button.transform); AppendTransform(geometry, actor.transform); }
                // ANIMATING_ZOOM can remain set until confirmation on this
                // client. Observe the real hero transform instead of that flag.
                if (!gate.Observe(context, ready ? actor.GetInstanceID().ToString() : "", geometry.ToArray(),
                    ready, Time.unscaledTime, Time.frameCount))
                {
                    LogDraftConfirmationWait("等待英雄确认按钮及缩放动画稳定", context);
                    continue;
                }
                display.ClickConfirmButton();
                _draftConfirmationActive = false;
                Log("Arena hero confirmation clicked after stable zoom; awaiting server slot advance: " + context);
                yield break;
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= _nextChoiceDiag)
                {
                    _nextChoiceDiag = Time.unscaledTime + 10f;
                    Log("Arena hero confirmation waiting: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
        if (generation == _draftSelectionGeneration)
        {
            _draftConfirmationActive = false;
            if (Time.unscaledTime - started >= 60f)
                Log("Arena hero confirmation not ready within 60s; preserving pending choice for server refresh");
        }
    }

    private IEnumerator ConfirmPackageChoice(DraftDisplay display, string context, int generation, int choice)
    {
        var gate = new ArenaDraftVisualGate();
        var started = Time.unscaledTime;
        while (Time.unscaledTime - started < 60f && IsDraftConfirmationCurrent(display, context, generation))
        {
            yield return new WaitForSecondsRealtime(0.25f);
            if (!IsDraftConfirmationCurrent(display, context, generation)) break;
            try
            {
                var popupIndex = DraftField<int>(display, "m_currentlyViewedPackagePopupIndex");
                var popup = DraftField<PackageCardsPopup>(display, "m_packageCardsPopup");
                // WidgetInstance derives from Widget, but this serialized field
                // is public too; verify its contract at compile/release time.
                var widget = display.m_packageCardsPopupWidget;
                var button = popup == null ? null : typeof(PackageCardsPopup).GetField("m_chooseButtonVisualController", InstancePrivate)
                    ?.GetValue(popup) as Hearthstone.UI.VisualController;
                var ready = popupIndex == choice - 1 && popup != null && popup.gameObject.activeInHierarchy &&
                    widget != null && widget.IsReady && widget.IsActive && !widget.IsChangingStates &&
                    button != null && button.State == "Active" && !button.IsChangingStates && !GameUtils.IsAnyTransitionActive();
                var geometry = new List<float>();
                if (ready) AppendTransform(geometry, popup.transform);
                if (!gate.Observe(context, ready ? popup.GetInstanceID().ToString() : "", geometry.ToArray(),
                    ready, Time.unscaledTime, Time.frameCount))
                {
                    LogDraftConfirmationWait("等待传说/组合卡牌弹窗加载和确认按钮可用", context);
                    continue;
                }
                s_confirmPackageChoiceMethod ??= typeof(DraftDisplay).GetMethod("ConfirmPackageChoice", InstancePrivate);
                if (s_confirmPackageChoiceMethod == null) throw new MissingMethodException("DraftDisplay.ConfirmPackageChoice");
                s_confirmPackageChoiceMethod.Invoke(display, null);
                // Direct confirmation advances the server slot, but the native
                // manual-draft path expects its button event to dismiss the UI.
                // The native method has already invalidated the popup index:
                // its callback cannot submit the same (or a new) choice again.
                // Use the native events, never hide arbitrary Unity objects.
                if (DraftField<int>(display, "m_currentlyViewedPackagePopupIndex") == -1 &&
                    widget.IsActive && !widget.IsChangingStates &&
                    generation == _draftSelectionGeneration && HsModNativeArena.IsNativeArenaAutomationRunning())
                {
                    var clicked = widget.TriggerEvent("button_floaty_clicked", default);
                    var dismissed = widget.TriggerEvent("PACKAGE_CARDS_DISMISSED", default);
                    Log("Arena confirmed package UI dismissal events: clicked=" + clicked +
                        "; dismissed=" + dismissed + "; invalidatedPopupIndex=-1; context=" + context);
                }
                _draftConfirmationActive = false;
                Log("Arena package confirmation submitted after native popup became ready; awaiting server slot advance: " + context);
                yield break;
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= _nextChoiceDiag)
                {
                    _nextChoiceDiag = Time.unscaledTime + 10f;
                    Log("Arena package confirmation waiting: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
        if (generation == _draftSelectionGeneration)
        {
            _draftConfirmationActive = false;
            if (Time.unscaledTime - started >= 60f)
                Log("Arena package confirmation not ready within 60s; preserving pending choice for server refresh");
        }
    }

    private void LogDraftConfirmationWait(string reason, string context)
    {
        if (Time.unscaledTime < _nextChoiceDiag) return;
        _nextChoiceDiag = Time.unscaledTime + 10f;
        Log("Arena 选牌确认等待：" + reason + "; slot=" + context);
    }
}
