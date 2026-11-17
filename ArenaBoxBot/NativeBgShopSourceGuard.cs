using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

// Game825 round11: ResolveSource chose own HAND entity11303 at slot6,
// although the same card/shop slot had legal purchase target12306.
// Correct only this resolver result using one exact live legal shop target.
// The original mapper, resource checks, ledger and send path remain in charge.
internal static class NativeBgShopSourceGuard
{
    private static Harmony s_harmony;
    private static float s_nextInstall;
    private static string s_logged;
    private static readonly int s_pid = CurrentPid();

    private static int CurrentPid() { using (var p = Process.GetCurrentProcess()) return p.Id; }

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5f;
        Harmony candidate = null;
        try
        {
            var bot = AccessTools.TypeByName("HsMod.BgBoxAiBot");
            var rec = AccessTools.TypeByName("HsMod.BoxAiRecommendation");
            if (bot == null || rec == null) return;
            var source = AccessTools.Method(bot, "ResolveSource", new[] { typeof(GameState), rec });
            if (source == null || source.ReturnType != typeof(Entity)) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.bg-shop-source");
            candidate.Patch(source,
                postfix: new HarmonyMethod(typeof(NativeBgShopSourceGuard), nameof(AfterSource)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG shop source guard installed: exact current legal buy target/card/slot/owner only; native mapping and dispatch retained");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("BG shop source guard unavailable: " + ex.GetType().Name);
        }
    }

    private static object Field(object rec, string name) => rec?.GetType().GetField(name)?.GetValue(rec);
    private static int Number(object rec, string name) => Field(rec, name) is int n ? n : -1;
    private static bool Legal(Network.Options.Option.SubOption part)
        => part?.PlayErrorInfo != null && part.PlayErrorInfo.PlayError == PlayErrors.ErrorType.NONE;

    private static bool Current(GameState state, object rec, string action, out Network.Options packet)
    {
        packet = state?.GetOptionsPacket();
        var friendly = state?.GetFriendlySidePlayer();
        var opposing = state?.GetOpposingSidePlayer();
        var processor = state?.GetPowerProcessor();
        var active = state != null && state.IsGameCreated() && !state.IsGameOverNowOrPending() &&
            !state.WasConcedeRequested() && state.GetTurn() > 0 && !state.IsMulliganManagerActive() &&
            !state.IsInChoiceMode() && state.GetFriendlyEntityChoices() == null &&
            (int)state.GetResponseMode() == 1 && SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY &&
            packet?.List != null && friendly != null && opposing != null &&
            friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0 &&
            processor != null && !processor.HasTaskLists();
        var age = Field(rec, "CapturedAt") is DateTime captured
            ? (DateTime.UtcNow - captured.ToUniversalTime()).TotalSeconds : double.NaN;
        return BgShopSourcePolicy.IsCurrent(s_pid, Number(rec, "GamePid"), packet?.ID ?? -1,
            Number(rec, "OptionId"), age, active, action, Number(rec, "SourceEntityId"),
            RecommendationMemberAccess.ReadText(rec, "SourceZone"));
    }

    private static void AfterSource(GameState __0, object __1, ref Entity __result)
    {
        try
        {
            var action = RecommendationMemberAccess.ReadAction(__1);
            if (!Current(__0, __1, action, out var packet)) return;
            var card = RecommendationMemberAccess.ReadText(__1, "SourceCardId");
            var name = RecommendationMemberAccess.ReadText(__1, "SourceName");
            var position = Number(__1, "SourcePosition");
            if (string.IsNullOrEmpty(card) || position < 1) return;
            var wantSpell = action == "buy_special";
            var candidates = new List<OptionEntityCandidate>();
            foreach (var option in packet.List)
            {
                if (option == null || option.Type != Network.Options.Option.OptionType.POWER ||
                    !Legal(option.Main) || option.Main.Targets == null) continue;
                var button = __0.GetEntity(option.Main.ID)?.GetCardId() ?? "";
                if (button.IndexOf("DragBuy", StringComparison.Ordinal) < 0 ||
                    (button.IndexOf("Spell", StringComparison.Ordinal) >= 0) != wantSpell) continue;
                foreach (var target in option.Main.Targets)
                {
                    if (target?.PlayErrorInfo == null || target.PlayErrorInfo.PlayError != PlayErrors.ErrorType.NONE) continue;
                    var entity = __0.GetEntity(target.ID);
                    if (entity == null || !BgShopSourcePolicy.IsPurchaseCardType(action, (int)entity.GetCardType()) ||
                        (!string.IsNullOrEmpty(name) && !string.Equals(name, entity.GetName(), StringComparison.Ordinal))) continue;
                    candidates.Add(new OptionEntityCandidate {
                        Id = entity.GetEntityId(), CardId = entity.GetCardId(), Position = entity.GetZonePosition(),
                        Controller = entity.GetTag(GAME_TAG.CONTROLLER), Zone = entity.GetZone().ToString(), Legal = true
                    });
                }
            }
            var id = OptionEntityFallbackPolicy.Resolve(candidates, card, position,
                __0.GetOpposingSidePlayer().GetPlayerId(), "PLAY");
            if (id == 0 || (__result != null && __result.GetEntityId() == id)) return;
            var replacement = __0.GetEntity(id);
            if (replacement == null) return;
            var previous = __result?.GetEntityId() ?? 0;
            __result = replacement;
            var key = packet.ID + ":" + RecommendationMemberAccess.ReadText(__1, "RequestId") + ":" + id;
            if (s_logged == key) return;
            s_logged = key;
            ArenaBoxBotPlugin.LogSource?.LogWarning("BgAI购买来源纠正: optionsId=" + packet.ID +
                "; requestId=" + RecommendationMemberAccess.ReadText(__1, "RequestId") +
                "; previousEntity=" + previous + "; legalShopEntity=" + id + "; cardId=" + card +
                "; position=" + position + "; exact unique native buy target; original mapping/ledger/send retained");
        }
        catch { /* Unknown or ambiguous state keeps the original resolver result. */ }
    }
}
