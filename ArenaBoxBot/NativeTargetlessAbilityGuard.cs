using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

// Original packet116 in game23708 explicitly offered BE_013t1 as a legal,
// targetless POWER child of own source61. The Box called it minion_attack;
// native MapOption rejected it before reaching its existing skill mapper.
// Correct only that failed mapping result, without changing the recommendation,
// packet, native submission, acknowledgement, or execution ledger.
internal static class NativeTargetlessAbilityGuard
{
    private static Harmony s_harmony;
    private static MethodInfo s_mapTitan;
    private static float s_nextInstall;
    private static string s_logged;
    private static readonly int s_pid = CurrentPid();
    private static int CurrentPid() { using (var p = Process.GetCurrentProcess()) return p.Id; }
    private static object Field(object value, string name) => value?.GetType().GetField(name)?.GetValue(value);
    private static int Number(object value, string name) => Field(value, name) is int n ? n : -1;
    private static string Text(object value, string name) => Field(value, name) as string;
    private static bool Legal(Network.Options.Option.SubOption part)
        => part?.PlayErrorInfo != null && part.PlayErrorInfo.PlayError == PlayErrors.ErrorType.NONE;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5f;
        Harmony candidate = null;
        try
        {
            var adapter = AccessTools.TypeByName("HsMod.BoxAiShadowAdapter");
            var rec = adapter?.Assembly.GetType("HsMod.BoxAiRecommendation");
            if (rec == null) return;
            var signature = new[] { typeof(GameState), typeof(Network.Options), rec };
            var map = AccessTools.Method(adapter, "MapOption", signature);
            s_mapTitan = AccessTools.Method(adapter, "MapTitanPower", signature);
            if (map?.ReturnType.FullName != "HsMod.BoxAiMappedAction" || s_mapTitan?.ReturnType != map.ReturnType) return;
            foreach (var field in map.ReturnType.GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (field.IsInitOnly || (field.FieldType != typeof(int) && field.FieldType != typeof(bool) && field.FieldType != typeof(string))) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.targetless-ability");
            candidate.Patch(map, postfix: new HarmonyMethod(typeof(NativeTargetlessAbilityGuard), nameof(AfterMap)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BoxAI targetless ability mapping guard installed (unique current legal child; original MapTitanPower and dispatch retained)");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI ability mapping guard unavailable: " + ex.GetType().Name);
        }
    }

    private static void AfterMap(GameState __0, Network.Options __1, object __2, object __result)
    {
        try
        {
            var friendly = __0?.GetFriendlySidePlayer();
            var opposing = __0?.GetOpposingSidePlayer();
            var processor = __0?.GetPowerProcessor();
            var manager = GameMgr.Get();
            var ready = __0 != null && __0.IsGameCreated() && !__0.IsGameOverNowOrPending() && !__0.WasConcedeRequested() &&
                !__0.IsMulliganManagerActive() && !__0.IsInChoiceMode() && __0.GetFriendlyEntityChoices() == null &&
                (int)__0.GetResponseMode() == 1 && SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY &&
                manager != null && !manager.IsBattlegrounds() && friendly != null && opposing != null &&
                friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0 &&
                processor != null && !processor.HasTaskLists() && !__0.IsBusy() &&
                ReferenceEquals(__0.GetOptionsPacket(), __1) && __1?.List != null;
            var ability = Text(__2, "AbilityCardId");
            var age = Field(__2, "CapturedAt") is DateTime captured ? (DateTime.UtcNow - captured.ToUniversalTime()).TotalSeconds : double.NaN;
            var noTarget = Number(__2, "TargetEntityId") == 0 && Number(__2, "TargetPosition") == 0 &&
                string.IsNullOrEmpty(Text(__2, "TargetCardId")) && string.IsNullOrEmpty(Text(__2, "TargetName")) &&
                Number(__2, "BoardPosition") == 0;
            var failed = Field(__result, "Legal") is bool legal && !legal &&
                Text(__result, "Reason") == "攻击建议缺少目标实体";
            if (!TargetlessAbilityPolicy.Current(s_pid, Number(__2, "GamePid"), __1?.ID ?? -1, Number(__2, "OptionId"),
                age, ready, RecommendationMemberAccess.ReadAction(__2), Text(__2, "RawText"), ability, noTarget, failed)) return;
            var sourceId = Number(__result, "SourceEntityId");
            var source = __0.GetEntity(sourceId);
            var owner = __0.GetFriendlyPlayerId();
            if (source == null || !source.IsMinion() || source.GetZone() != TAG_ZONE.PLAY ||
                source.GetTag(GAME_TAG.CONTROLLER) != owner || Number(__2, "SourcePosition") <= 0 ||
                source.GetZonePosition() != Number(__2, "SourcePosition") ||
                !string.Equals(source.GetCardId(), Text(__2, "SourceCardId"), StringComparison.Ordinal) ||
                (Number(__2, "SourceEntityId") > 0 && Number(__2, "SourceEntityId") != sourceId)) return;
            var options = new List<TargetlessAbilityOption>();
            for (var i = 0; i < __1.List.Count; i++)
            {
                var option = __1.List[i];
                if (option?.Type != Network.Options.Option.OptionType.POWER || option.Main?.ID != sourceId || option.Subs == null) continue;
                for (var j = 0; j < option.Subs.Count; j++)
                {
                    var sub = option.Subs[j];
                    var entity = sub == null ? null : __0.GetEntity(sub.ID);
                    options.Add(new TargetlessAbilityOption { OptionIndex = i, SubOptionIndex = j, MainId = sourceId,
                        AbilityId = sub?.ID ?? 0, AbilityCardId = entity?.GetCardId(),
                        AbilityController = entity?.GetTag(GAME_TAG.CONTROLLER) ?? 0,
                        MainLegal = Legal(option.Main), SubLegal = Legal(sub), TargetCount = sub?.Targets?.Count ?? 0 });
                }
            }
            var unique = TargetlessAbilityPolicy.Resolve(options, sourceId, owner, ability);
            if (unique == null) return;
            var mapped = s_mapTitan.Invoke(null, new[] { (object)__0, __1, __2 });
            if (!(Field(mapped, "Legal") is bool accepted) || !accepted || mapped.GetType() != __result.GetType() ||
                Number(mapped, "OptionIndex") != unique.OptionIndex || Number(mapped, "SubOptionIndex") != unique.SubOptionIndex ||
                Number(mapped, "SourceEntityId") != sourceId || Number(mapped, "TargetEntityId") != 0 || Number(mapped, "Position") != 0) return;
            // Legal is copied last: any unexpected reflection error leaves the
            // original mapping rejected, without submitting a partial result.
            var fields = mapped.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach (var field in fields) if (field.Name != "Legal") field.SetValue(__result, field.GetValue(mapped));
            mapped.GetType().GetField("Legal").SetValue(__result, true);
            var key = __1.ID + ":" + Text(__2, "RequestId") + ":" + unique.AbilityId;
            if (s_logged == key) return;
            s_logged = key;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BoxAI技能映射纠正: game=" + manager.GetGameHandle() + "; turn=" + __0.GetTurn() +
                "; optionsId=" + __1.ID + "; requestId=" + Text(__2, "RequestId") + "; source=" + sourceId + "; ability=" + ability +
                "; abilityEntity=" + unique.AbilityId + "; option=" + unique.OptionIndex + "; subOption=" + unique.SubOptionIndex +
                "; target=0; original MapTitanPower used; recommendation/packet/ACK/ledger/dispatch unchanged");
        }
        catch { /* Unknown schema or native mapping retains the original rejection. */ }
    }
}
