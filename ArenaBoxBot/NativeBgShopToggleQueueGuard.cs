using System;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

// Freeze/unfreeze are absolute advice implemented by a native toggle. A shop
// snapshot taken while earlier Power is still applying can lose the native
// pending-toggle identity and submit the same toggle twice (Game802, round1).
internal static class NativeBgShopToggleQueueGuard
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
            var bot = AccessTools.TypeByName("HsMod.BgBoxAiBot");
            var recommendation = AccessTools.TypeByName("HsMod.BoxAiRecommendation");
            if (bot == null || recommendation == null) return;
            var toggle = AccessTools.Method(bot, "ExecuteShopToggle",
                new[] { typeof(GameState), typeof(Network.Options), recommendation, typeof(string) });
            if (toggle == null || toggle.ReturnType != typeof(void)) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.bg-shop-toggle-queue");
            candidate.Patch(toggle,
                prefix: new HarmonyMethod(typeof(NativeBgShopToggleQueueGuard), nameof(BeforeToggle)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG shop-toggle queue guard installed: wait for natural Power drain before reading shop freeze state; original toggle gate and send path retained");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("BG shop-toggle queue guard unavailable: " + ex.GetType().Name);
        }
    }

    private static bool BeforeToggle(GameState __0, object __2, string __3)
    {
        try
        {
            if (__0 == null || __0.GetTurn() < 1 ||
                (!string.Equals(__3, "freeze", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(__3, "unfreeze", StringComparison.OrdinalIgnoreCase))) return true;
            var processor = __0.GetPowerProcessor();
            if (processor == null) return true; // Unknown state keeps native handling.
            var key = GameMgr.Get()?.GetGameHandle() + ":" + __0.GetTurn() + ":" +
                RecommendationMemberAccess.ReadText(__2, "RequestId") + ":" + __3;
            if (!processor.HasTaskLists())
            {
                if (s_waitKey == key)
                    ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI冻结队列已自然完成: key=" + key +
                        "; waitedSeconds=" + (Time.realtimeSinceStartup - s_waitStarted).ToString("0.000") +
                        "; original shop snapshot and toggle validation resume");
                s_waitKey = null;
                return true;
            }
            if (s_waitKey != key)
            {
                s_waitKey = key;
                s_waitStarted = Time.realtimeSinceStartup;
                ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI冻结等待原生队列: key=" + key +
                    "; currentTask=" + processor.GetCurrentTaskList()?.GetId() +
                    "; queued=" + processor.GetPowerQueue()?.Count +
                    "; preserve recommendation and pending toggle; no queue or frozen-state changes");
            }
            return false;
        }
        catch { return true; }
    }
}
