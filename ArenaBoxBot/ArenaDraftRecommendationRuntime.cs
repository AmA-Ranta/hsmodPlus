using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ArenaBoxBot;

internal static class ArenaDraftRecommendationRuntime
{
    internal static bool Installed { get; private set; }
    internal static void EnsureInstalled()
    {
        if (Installed) return;
        try
        {
            // All three real native paths (begin, choices/contents, chosen)
            // converge here after setting deck/slot/type and before the UI
            // receives the new bucket. No consuming Network getter is called.
            var method = AccessTools.Method(typeof(DraftManager), "InformDraftDisplayOfChoices",
                new[] { typeof(List<Network.CardChoice>) });
            if (method == null) throw new MissingMethodException("DraftManager choice delivery contract");
            new Harmony("local.hsmod.arenaboxbot.draft-recommendation").Patch(method,
                prefix: new HarmonyMethod(typeof(ArenaDraftRecommendationRuntime), nameof(BeforeChoices)));
            Installed = true;
        }
        catch (Exception ex)
        { ArenaBoxBotPlugin.LogSource?.LogWarning("Arena recommended-choice boundary unavailable: " + ex.GetType().Name); }
    }

    internal static bool CaptureReady(out string detail)
    {
        detail = "原生选牌候选组监听尚未安装";
        return Installed && ArenaDraftRecommendationStream.CaptureReady(out detail);
    }

    private static void BeforeChoices(DraftManager __instance, List<Network.CardChoice> __0)
    {
        try
        {
            var manager = __instance;
            if (manager == null || manager.IsUnderground() != ArenaSettings.IsUnderground)
            { ArenaDraftRecommendationStream.BeginNativeWindow(null, "UNKNOWN"); return; }
            var state = manager.GetArenaState(manager.IsUnderground()).ToString();
            var redraft = manager.IsRedrafting();
            var ids = __0?.Select(card => card?.CardDef?.Name ?? "").ToList();
            var key = ArenaDraftRecommendationStream.Bucket(manager.IsUnderground(),
                manager.GetDraftDeck(manager.IsUnderground())?.ID ?? 0,
                redraft ? manager.GetRedraftDeck()?.ID ?? 0 : 0, redraft,
                redraft ? manager.GetRedraftSlot() : manager.GetSlot(), manager.GetSlotType().ToString(), ids);
            ArenaDraftRecommendationStream.BeginNativeWindow(key, state);
        }
        catch (Exception ex)
        {
            ArenaDraftRecommendationStream.BeginNativeWindow(null, "UNKNOWN");
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena recommended-choice bucket binding unavailable: " + ex.GetType().Name);
        }
    }
}