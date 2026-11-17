using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

internal static class NativeBgPreAdjustmentRecovery
{
    private static Harmony s_harmony;
    private static float s_nextInstall;
    private static string s_waitKey;
    private static float s_waitStarted;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5f;
        Harmony candidate = null;
        try
        {
            var type = AccessTools.TypeByName("HsMod.Utilities.BattlegroundsPreAdjustment");
            var evaluate = AccessTools.Method(type, "Evaluate");
            var bot = AccessTools.TypeByName("HsMod.BgBoxAiBot");
            var ready = AccessTools.Method(bot, "PreAdjustmentReady");
            var readyParameters = ready?.GetParameters();
            var parameters = evaluate?.GetParameters();
            if (parameters == null || parameters.Length != 6 || parameters[0].ParameterType != typeof(string) ||
                parameters[1].ParameterType != typeof(int) || parameters[4].ParameterType != typeof(int) ||
                parameters[5].ParameterType != typeof(DateTime) ||
                evaluate.ReturnType.FullName != "HsMod.Utilities.PreAdjustDecision" ||
                ready?.ReturnType != typeof(bool) || readyParameters == null || readyParameters.Length != 3 ||
                readyParameters[0].ParameterType != typeof(GameState) ||
                readyParameters[2].ParameterType.FullName != "HsMod.BoxAiRecommendation") return;
            candidate = new Harmony("local.hsmod.arenaboxbot.bg-pre-adjust-revalidation");
            candidate.Patch(evaluate, prefix: new HarmonyMethod(typeof(NativeBgPreAdjustmentRecovery), nameof(BeforeEvaluate)));
            candidate.Patch(ready, prefix: new HarmonyMethod(typeof(NativeBgPreAdjustmentRecovery), nameof(BeforePreAdjustment)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG pre-adjust revalidation installed: reopen only a cached unsubmitted identity mismatch after exact native board/attribute catch-up; original validation retained");
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG pre-adjust queue gate installed: defer original binding while native Power task lists remain; original evaluator runs after natural drain; no queue or acknowledgement changes");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("BG pre-adjust revalidation unavailable: " + ex.GetType().Name);
        }
    }

    // Game728: the source's stats caught up before an earlier sell/play had
    // finished updating the rest of the board. Binding at that intermediate
    // snapshot cached a board_changed failure for the remainder of the turn.
    // Wait before binding, rather than accepting or resetting a changed board.
    private static bool BeforePreAdjustment(GameState __0, object __2, ref bool __result)
    {
        try
        {
            var moves = __2?.GetType().GetField("PreAdjustPosition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(__2) as IList;
            var needsBinding = string.Equals(RecommendationMemberAccess.ReadAction(__2), "pre_adjust", StringComparison.OrdinalIgnoreCase) ||
                (moves != null && moves.Count > 0);
            if (!needsBinding || __0 == null) return true;
            var processor = __0.GetPowerProcessor();
            if (processor == null) return true; // Unknown contracts keep original handling.
            var key = GameMgr.Get()?.GetGameHandle() + ":" + __0.GetTurn() + ":" +
                RecommendationMemberAccess.ReadText(__2, "RequestId");
            var pending = processor.HasTaskLists();
            if (!pending)
            {
                if (s_waitKey == key)
                    ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI前置站位队列已自然完成: key=" + key +
                        "; waitedSeconds=" + (Time.realtimeSinceStartup - s_waitStarted).ToString("0.000") +
                        "; original binding and all native validation resume");
                s_waitKey = null;
                return true;
            }
            if (s_waitKey != key)
            {
                s_waitKey = key;
                s_waitStarted = Time.realtimeSinceStartup;
                ArenaBoxBotPlugin.LogSource?.LogWarning("BgAI前置站位等待原生队列: key=" + key +
                    "; currentTask=" + processor.GetCurrentTaskList()?.GetId() +
                    "; queued=" + processor.GetPowerQueue()?.Count +
                    "; defer binding to incomplete local board; no queue or ACK changes");
            }
            __result = false; // Only wait. Never synthesize Ready or submit an action.
            return false;
        }
        catch { return true; }
    }

    private static void BeforeEvaluate(object __instance, string __0, int __1, object __2, object __3)
    {
        if (BgPreAdjustmentRecovery.TryReset(__instance, __0, __1, __2, __3, out var detail))
            ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI前置站位重新核验: " + detail +
                "; cleared only cached move_identity_mismatch; native Evaluate will recheck, no synthetic move/ACK");
    }
}
