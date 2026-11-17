using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

internal static class NativeOptionExecutionDiagnostics
{
    private static Harmony s_harmony;
    private static Type s_adapter;
    private static MethodInfo s_readRemaining;
    private static object s_observedRecommendation;
    private static string s_observedGame;
    private static int s_observedPacket = -1;
    private static float s_observedAt;
    private static float s_nextAttempt;
    private static float s_nextReadinessSample;
    private static string s_lastReadinessKey;
    private static readonly OptionExecutionWait s_wait = new OptionExecutionWait();
    private static readonly OptionExecutionWait s_responseGateWait = new OptionExecutionWait();

    internal static void ObserveResponseGate(GameState state, object recommendation, bool released, string reason)
    {
        try
        {
            var friendly = state?.GetFriendlySidePlayer();
            var opposing = state?.GetOpposingSidePlayer();
            var packet = state?.GetOptionsPacket();
            var active = state != null && state.IsGameCreated() && !state.IsGameOverNowOrPending() &&
                !state.WasConcedeRequested() && !state.IsMulliganManagerActive() && packet != null &&
                friendly != null && opposing != null && friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 &&
                opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0;
            var key = active ? GameMgr.Get()?.GetGameHandle() + ":" + state.GetTurn() + ":" + packet.ID : "";
            var now = Time.realtimeSinceStartup;
            if (!s_responseGateWait.Observe(key, now, released, active)) return;
            var timer = TurnTimer.Get();
            var remaining = s_readRemaining?.Invoke(null, new object[] { state });
            var captured = Field(recommendation, "CapturedAt");
            ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI响应等待诊断: waitedSeconds=" + s_responseGateWait.Elapsed(now).ToString("0.0") +
                ", turn=" + state.GetTurn() + ", optionsId=" + packet.ID + ", responseMode=" + state.GetResponseMode() +
                ", mulliganActive=" + state.IsMulliganManagerActive() + ", mulliganBusy=" + state.IsMulliganBusy() + ", clientBusy=" + state.IsBusy() +
                ", reason=" + reason + ", action=" + RecommendationMemberAccess.ReadAction(recommendation) +
                ", requestId=" + RecommendationMemberAccess.ReadText(recommendation, "RequestId") +
                ", recGamePid=" + Field(recommendation, "GamePid") + ", publicationSeq=" + Field(recommendation, "PublicationSeq") +
                ", capturedAgeSeconds=" + (captured is DateTime timestamp ? (DateTime.UtcNow - timestamp.ToUniversalTime()).TotalSeconds.ToString("0.0") : "unknown") +
                ", remaining=" + remaining + ", timerState=" + timer?.GetCurrentTurnTimerState() +
                ", timerFriendly=" + Field(timer, "m_currentTimerBelongsToFriendlySidePlayer") +
                ", timerEndTimestamp=" + Field(timer, "m_countdownEndTimestamp") +
                "; existing gate read only; acknowledgement guards unchanged");
        }
        catch { /* Observations never alter the gate result or reader state. */ }
    }

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextAttempt) return;
        s_nextAttempt = Time.unscaledTime + 5f;
        try
        {
            var adapter = Type.GetType("HsMod.BoxAiShadowAdapter, HsMod");
            var target = adapter?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(m => m.Name == "TryExecuteOptionWithinBudget" && m.ReturnType == typeof(bool) &&
                    m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(GameState) &&
                    m.GetParameters()[2].ParameterType == typeof(float));
            if (target == null) return;
            s_adapter = adapter;
            s_readRemaining = adapter.GetMethod("ReadTurnSecondsRemaining", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var reader = adapter.GetMethod("ReadCurrent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (reader == null) return;
            var harmony = new Harmony("local.hsmod.arenaboxbot.option-wait-observer");
            harmony.Patch(reader, postfix: new HarmonyMethod(typeof(NativeOptionExecutionDiagnostics), nameof(AfterNativeRead)));
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(NativeOptionExecutionDiagnostics), nameof(AfterAttempt)));
            s_harmony = harmony;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BoxAI option execution wait diagnostics installed (observational only)");
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI option diagnostics deferred: " + ex.GetType().Name);
        }
    }

    private static object Field(object value, string name)
        => value?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);

    // Observe the native reader's existing result. Calling ReadCurrent again
    // from a diagnostic can initialize/reset native execution tracking and
    // would make an ostensibly passive observer change the state it measures.
    private static void AfterNativeRead(GameState __0, object __result)
    {
        try
        {
            s_observedRecommendation = __result;
            s_observedGame = GameMgr.Get()?.GetGameHandle().ToString();
            s_observedPacket = __0?.GetOptionsPacket()?.ID ?? -1;
            s_observedAt = Time.realtimeSinceStartup;
            ObserveNativeReadReadiness(__0, __result, s_observedAt);
        }
        catch { s_observedRecommendation = null; s_observedGame = null; s_observedPacket = -1; }
    }

    // Capture the reader result before execution/own-turn gates can suppress
    // wait diagnostics. The 03:46 replay had a new option packet while the
    // client's processed turn and source entity still belonged to older tasks.
    // Sample at most twice a second; emit only when recommendation/readiness
    // changes. Never call the stateful reader or change an execution guard.
    private static void ObserveNativeReadReadiness(GameState state, object recommendation, float now)
    {
        try
        {
            if (state == null || now < s_nextReadinessSample) return;
            s_nextReadinessSample = now + 0.5f;
            var turn = state.GetTurn();
            var friendlyTag = state.GetFriendlySidePlayer()?.GetTag(GAME_TAG.CURRENT_PLAYER);
            var opposingTag = state.GetOpposingSidePlayer()?.GetTag(GAME_TAG.CURRENT_PLAYER);
            var mulliganActive = state.IsMulliganManagerActive();
            var mulliganBusy = state.IsMulliganBusy();
            var clientBusy = state.IsBusy();
            var timer = TurnTimer.Get();
            var timerState = timer?.GetCurrentTurnTimerState().ToString();
            var timerOwner = Field(timer, "m_currentTimerBelongsToFriendlySidePlayer");
            var timerHasCountdown = timer != null && timer.HasCountdownTimeout();
            var ropeActive = timer != null && timer.IsRopeActive();
            var key = s_observedGame + ":" + s_observedPacket + ":" + ExecutionKey(recommendation) +
                ":" + turn + ":" + friendlyTag + ":" + opposingTag +
                ":" + mulliganActive + ":" + mulliganBusy + ":" + clientBusy +
                ":" + timerState + ":" + timerOwner + ":" + timerHasCountdown + ":" + ropeActive;
            if (key == s_lastReadinessKey) return;
            s_lastReadinessKey = key;
            // Read the original clock only when emitting an observation. Never
            // use this value to release an execution gate or invent a budget.
            var rawRemaining = timer != null ? timer.ComputeCountdownRemainingSec() : float.NaN;
            var ownTimerKnown = !mulliganActive && friendlyTag == 1 && opposingTag == 0 &&
                timerHasCountdown && timerOwner is bool owner && owner &&
                (timerState == "START" || timerState == "COUNTDOWN" || timerState == "TIMEOUT") &&
                !float.IsNaN(rawRemaining) && !float.IsInfinity(rawRemaining);
            ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI原生读取就绪诊断: game=" + s_observedGame +
                ", turn=" + turn + ", optionsId=" + s_observedPacket +
                ", responseMode=" + state.GetResponseMode() +
                ", friendlyCurrent=" + friendlyTag + ", opposingCurrent=" + opposingTag +
                ", mulliganActive=" + mulliganActive + ", mulliganBusy=" + mulliganBusy + ", clientBusy=" + clientBusy +
                ", requestId=" + RecommendationMemberAccess.ReadText(recommendation, "RequestId") +
                ", recGamePid=" + Field(recommendation, "GamePid") + ", publicationSeq=" + Field(recommendation, "PublicationSeq") +
                ", recommendationPresent=" + (recommendation != null) +
                ", timerState=" + timerState + ", timerFriendly=" + timerOwner +
                ", timerHasCountdown=" + timerHasCountdown + ", ropeActive=" + ropeActive +
                ", rawTimerRemaining=" + rawRemaining.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                ", ownTimerKnown=" + ownTimerKnown +
                ", ownTimerRemaining=" + (ownTimerKnown ? rawRemaining.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "unknown") +
                ", timerEndTimestamp=" + Field(timer, "m_countdownEndTimestamp") +
                "; passive native reader result; no action or guard changes");
        }
        catch { /* Readiness diagnostics must not affect existing observations or native results. */ }
    }

    private static string ExecutionKey(object recommendation)
    {
        if (recommendation == null || !(Field(recommendation, "CapturedAt") is DateTime captured)) return null;
        var request = Field(recommendation, "RequestId") as string;
        var key = string.Format("{0}@{1}", string.IsNullOrEmpty(request) ? "box" : request, captured.Ticks);
        if (Field(recommendation, "SchemaVersion") is int schema && schema == 2 &&
            Field(recommendation, "ProducerInstanceId") is string producer && !string.IsNullOrEmpty(producer) &&
            Field(recommendation, "PublicationSeq") is long sequence && sequence > 0)
            return string.Format("{0}#{1}|{2}", producer, sequence, key);
        return key;
    }

    private static void AfterAttempt(GameState __0, object __1, float __2, bool __result)
    {
        try
        {
            var state = __0;
            var friendly = state?.GetFriendlySidePlayer();
            var opposing = state?.GetOpposingSidePlayer();
            var packet = state?.GetOptionsPacket();
            var active = state != null && state.IsGameCreated() && !state.IsGameOverNowOrPending() &&
                !state.WasConcedeRequested() && friendly != null && opposing != null &&
                friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0 &&
                packet != null && __1 != null;
            var id = __1?.GetType().GetProperty("ID")?.GetValue(__1, null);
            active &= id != null && packet?.ID == Convert.ToInt32(id);
            var key = active ? GameMgr.Get()?.GetGameHandle() + ":" + state.GetTurn() + ":" + id : "";
            var now = Time.realtimeSinceStartup;
            if (!s_wait.Observe(key, now, __result, active)) return;
            var remaining = s_readRemaining?.Invoke(null, new object[] { state });
            var sameRead = s_observedGame == GameMgr.Get()?.GetGameHandle().ToString() && s_observedPacket == Convert.ToInt32(id);
            var rec = sameRead ? s_observedRecommendation : null;
            var timer = TurnTimer.Get();
            var shadow = s_adapter.GetField("s_shadowPolicy", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            var hasCommit = shadow?.GetType().GetProperty("HasActiveCommit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shadow, null);
            var executionKey = ExecutionKey(rec);
            var ledger = s_adapter.GetField("s_actionLedger", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            var entries = Field(ledger, "entries") as IDictionary;
            var entry = executionKey != null && entries != null && entries.Contains(executionKey) ? entries[executionKey] : null;
            var reaction = s_adapter.GetField("s_reactionGate", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            var capturedAt = Field(rec, "CapturedAt");
            ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI执行等待诊断: waitedSeconds=" + s_wait.Elapsed(now).ToString("0.0") +
                ", turn=" + state.GetTurn() + ", optionsId=" + id + ", responseMode=" + state.GetResponseMode() +
                ", mulliganActive=" + state.IsMulliganManagerActive() + ", mulliganBusy=" + state.IsMulliganBusy() + ", clientBusy=" + state.IsBusy() +
                ", executionDeadlineIn=" + (__2 - now).ToString("0.0") + ", nativeReadSamePacket=" + sameRead + ", nativeReadAgeSeconds=" + (sameRead ? (now - s_observedAt).ToString("0.0") : "unknown") +
                ", remaining=" + remaining + ", timerState=" + timer?.GetCurrentTurnTimerState() +
                ", timerFriendly=" + Field(timer, "m_currentTimerBelongsToFriendlySidePlayer") +
                ", timerEndTimestamp=" + Field(timer, "m_countdownEndTimestamp") +
                ", hasShadowCommit=" + hasCommit + ", action=" + RecommendationMemberAccess.ReadAction(rec) +
                ", requestId=" + RecommendationMemberAccess.ReadText(rec, "RequestId") +
                ", publicationSeq=" + Field(rec, "PublicationSeq") + ", recGamePid=" + Field(rec, "GamePid") +
                ", capturedAgeSeconds=" + (capturedAt is DateTime timestamp ? (DateTime.UtcNow - timestamp.ToUniversalTime()).TotalSeconds.ToString("0.0") : "unknown") +
                ", ledgerEntryPresent=" + (executionKey == null ? "unknown" : (entry != null).ToString()) +
                ", ledgerState=" + Field(entry, "State") + ", ledgerPacket=" + Field(entry, "PacketId") + ", ledgerAttempts=" + Field(entry, "Attempts") +
                ", reactionKey=" + Field(reaction, "key") + ", reactionDelay=" + Field(reaction, "delaySeconds") +
                ", shadowPacket=" + Field(shadow, "commitPacketId") + ", shadowKey=" + Field(shadow, "commitKey") +
                ", shadowStage=" + Field(shadow, "stageCursor") + ", shadowStages=" + (Field(shadow, "stages") as ICollection)?.Count +
                ", shadowReadyIn=" + (Field(shadow, "commitReadyAt") is float readyAt ? (readyAt - now).ToString("0.0") : "unknown") +
                "; observed unsuccessful attempt only; guards unchanged");
        }
        catch { /* Diagnostic failures must never change a native execution result. */ }
    }
}
