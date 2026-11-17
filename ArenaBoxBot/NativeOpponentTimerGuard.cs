using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

/// <summary>A previous opponent timeout cannot expire a current own option packet.</summary>
internal static class NativeOpponentTimerGuard
{
    private static Harmony s_harmony;
    private static FieldInfo s_owner;
    private static float s_nextAttempt, s_nextNotice;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextAttempt) return;
        s_nextAttempt = Time.unscaledTime + 5f;
        try
        {
            var adapter = Type.GetType("HsMod.BoxAiShadowAdapter, HsMod");
            var original = adapter?.GetMethod("ReadTurnSecondsRemaining",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(GameState) }, null);
            s_owner = typeof(TurnTimer).GetField("m_currentTimerBelongsToFriendlySidePlayer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (original == null || original.ReturnType != typeof(float) || s_owner?.FieldType != typeof(bool)) return;
            var harmony = new Harmony("local.hsmod.arenaboxbot.opponent-timeout-ownership");
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(NativeOpponentTimerGuard), nameof(AfterRemaining)));
            s_harmony = harmony;
            ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod turn remaining ownership guard installed; preserve current friendly timeout, disregard only expired opponent timer on authoritative own OPTION");
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod turn remaining ownership guard unavailable: " + ex.GetType().Name);
        }
    }

    private static void AfterRemaining(GameState __0, ref float __result)
    {
        if (__result != 0f) return;
        try
        {
            var state = __0;
            var timer = TurnTimer.Get();
            var owner = timer == null ? null : s_owner?.GetValue(timer);
            var friendly = state?.GetFriendlySidePlayer();
            var opposing = state?.GetOpposingSidePlayer();
            var packet = state?.GetOptionsPacket();
            var active = state != null && state.IsGameCreated() && !state.IsGameOverNowOrPending() &&
                !state.WasConcedeRequested() && !state.IsMulliganManagerActive() &&
                (int)state.GetResponseMode() == 1 && packet != null && packet.ID >= 0 &&
                packet.List != null && packet.List.Count > 0 &&
                friendly != null && opposing != null && friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 &&
                opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0 &&
                SceneMgr.Get()?.GetMode().ToString() == "GAMEPLAY";
            var adjusted = OpponentTimerPolicy.Remaining(__result, active, owner is bool,
                owner is bool belongsToFriendly && belongsToFriendly,
                timer != null && timer.HasCountdownTimeout(),
                timer != null && timer.GetCurrentTurnTimerState().ToString() == "TIMEOUT");
            if (!float.IsNaN(adjusted)) return;
            __result = adjusted; // Unknown own remaining, never an invented positive budget.
            if (Time.realtimeSinceStartup >= s_nextNotice)
            {
                s_nextNotice = Time.realtimeSinceStartup + 5f;
                ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI忽略过期对手计时器: ownTurn=" + state.GetTurn() +
                    "; optionsId=" + packet.ID + "; originalRemaining=0; timer=TIMEOUT; timerFriendly=false;" +
                    " authoritativeOwnOption=true; own remaining unknown; native options/ACK and server timeout unchanged");
            }
        }
        catch { /* Unknown ownership retains the original native time restriction. */ }
    }
}
