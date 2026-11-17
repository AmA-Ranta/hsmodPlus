using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

// Native resolution uses HandCards/BattlefieldCards, which can lag behind
// GameState's legal option packet while new cards/minions are materializing.
// Only fill a null native result with one exact current legal-packet entity.
// Mapping, source suppression, choice handling, ACK/ledger and dispatch all
// remain in HsMod. No recommendation or native game state is changed.
internal static class NativeOptionEntityFallback
{
    private static Harmony s_harmony;
    private static float s_nextAttempt;
    private static readonly int s_pid = CurrentPid();
    private static string s_sourceLogKey, s_targetLogKey;
    private static float s_sourceNextLog, s_targetNextLog;

    private static int CurrentPid() { using (var p = Process.GetCurrentProcess()) return p.Id; }

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextAttempt) return;
        s_nextAttempt = Time.unscaledTime + 5f;
        try
        {
            var adapter = Type.GetType("HsMod.BoxAiShadowAdapter, HsMod");
            var rec = adapter?.Assembly.GetType("HsMod.BoxAiRecommendation");
            if (rec == null) return;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var source = adapter.GetMethod("ResolveSource", flags, null, new[] { typeof(GameState), rec }, null);
            var target = adapter.GetMethod("ResolveTarget", flags, null, new[] { typeof(GameState), rec }, null);
            if (source?.ReturnType != typeof(Entity) || target?.ReturnType != typeof(Entity)) return;
            var h = new Harmony("local.hsmod.arenaboxbot.option-entity-fallback");
            h.Patch(source, postfix: new HarmonyMethod(typeof(NativeOptionEntityFallback), nameof(AfterSource)));
            h.Patch(target, postfix: new HarmonyMethod(typeof(NativeOptionEntityFallback), nameof(AfterTarget)));
            s_harmony = h;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BoxAI legal option entity fallback installed (exact current packet/PID/card/position/owner; null native result only)");
        }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI entity fallback deferred: " + ex.GetType().Name); }
    }

    private static object Field(object rec, string name) => rec?.GetType().GetField(name)?.GetValue(rec);
    private static int Number(object rec, string name) => Field(rec, name) is int value ? value : -1;
    private static string Text(object rec, string name) => Field(rec, name) as string;

    private static bool Current(GameState state, object rec, out Network.Options packet)
    {
        packet = state?.GetOptionsPacket();
        var friendly = state?.GetFriendlySidePlayer();
        var opposing = state?.GetOpposingSidePlayer();
        var active = state != null && state.IsGameCreated() && !state.IsGameOverNowOrPending() &&
            !state.WasConcedeRequested() && !state.IsMulliganManagerActive() &&
            !state.IsInChoiceMode() && state.GetFriendlyEntityChoices() == null &&
            (int)state.GetResponseMode() == 1 && SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY &&
            packet?.List != null && friendly != null && opposing != null &&
            friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0;
        var age = Field(rec, "CapturedAt") is DateTime captured
            ? (DateTime.UtcNow - captured.ToUniversalTime()).TotalSeconds : double.NaN;
        return string.Equals(Text(rec, "Action"), "play", StringComparison.OrdinalIgnoreCase) &&
            OptionEntityFallbackPolicy.IsCurrent(s_pid, Number(rec, "GamePid"), packet?.ID ?? -1,
                Number(rec, "OptionId"), age, active);
    }

    private static bool Legal(Network.Options.Option.SubOption option)
        => option?.PlayErrorInfo != null && option.PlayErrorInfo.PlayError == PlayErrors.ErrorType.NONE;

    private static OptionEntityCandidate Snapshot(Entity entity, bool legal)
        => entity == null ? null : new OptionEntityCandidate {
            Id = entity.GetEntityId(), CardId = entity.GetCardId(), Position = entity.GetZonePosition(),
            Controller = entity.GetTag(GAME_TAG.CONTROLLER), Zone = entity.GetZone().ToString(), Legal = legal
        };

    private static int SourceId(GameState state, Network.Options packet, object rec)
    {
        if (Number(rec, "SourceEntityId") > 0 || Number(rec, "SourcePosition") <= 0) return 0;
        var candidates = new List<OptionEntityCandidate>();
        foreach (var option in packet.List)
            if (option != null && option.Type == Network.Options.Option.OptionType.POWER && Legal(option.Main))
                candidates.Add(Snapshot(state.GetEntity(option.Main.ID), true));
        return OptionEntityFallbackPolicy.Resolve(candidates, Text(rec, "SourceCardId"),
            Number(rec, "SourcePosition"), state.GetFriendlyPlayerId(), "HAND");
    }

    private static bool ShouldLog(string key, ref string previous, ref float next)
    {
        var now = Time.unscaledTime;
        if (previous == key && now < next) return false;
        previous = key;
        next = now + 5f;
        return true;
    }

    private static void AfterSource(GameState __0, object __1, ref Entity __result)
    {
        if (__result != null) return;
        try
        {
            if (!Current(__0, __1, out var packet)) return;
            var id = SourceId(__0, packet, __1);
            if (id == 0) return;
            __result = __0.GetEntity(id);
            if (ShouldLog(packet.ID + ":" + Text(__1, "RequestId") + ":" + id, ref s_sourceLogKey, ref s_sourceNextLog))
                ArenaBoxBotPlugin.LogSource?.LogWarning($"BoxAI合法动作包实体兜底: kind=source; optionsId={packet.ID}; requestId={Text(__1, "RequestId")}; entity={id}; cardId={Text(__1, "SourceCardId")}; position={Number(__1, "SourcePosition")}; originalEntityMissing=true; native mapping/ledger/dispatch retained");
        }
        catch { /* Schema or state ambiguity retains the original null. */ }
    }

    private static void AfterTarget(GameState __0, object __1, ref Entity __result)
    {
        if (__result != null) return;
        try
        {
            if (!Current(__0, __1, out var packet) || Number(__1, "TargetEntityId") > 0) return;
            var source = SourceId(__0, packet, __1);
            if (source == 0) return;
            var side = Text(__1, "TargetSide");
            var player = side == "friendly" ? __0.GetFriendlySidePlayer() : side == "enemy" ? __0.GetOpposingSidePlayer() : null;
            if (player == null) return;
            var candidates = new List<OptionEntityCandidate>();
            foreach (var option in packet.List)
            {
                if (option == null || option.Type != Network.Options.Option.OptionType.POWER ||
                    !Legal(option.Main) || option.Main.ID != source || option.Main.Targets == null) continue;
                foreach (var target in option.Main.Targets)
                    if (target?.PlayErrorInfo != null && target.PlayErrorInfo.PlayError == PlayErrors.ErrorType.NONE)
                        candidates.Add(Snapshot(__0.GetEntity(target.ID), true));
            }
            var id = OptionEntityFallbackPolicy.Resolve(candidates, Text(__1, "TargetCardId"),
                Number(__1, "TargetPosition"), player.GetPlayerId(), "PLAY");
            if (id == 0) return;
            __result = __0.GetEntity(id);
            if (ShouldLog(packet.ID + ":" + Text(__1, "RequestId") + ":" + id, ref s_targetLogKey, ref s_targetNextLog))
                ArenaBoxBotPlugin.LogSource?.LogWarning($"BoxAI合法动作包实体兜底: kind=target; optionsId={packet.ID}; requestId={Text(__1, "RequestId")}; entity={id}; cardId={Text(__1, "TargetCardId")}; position={Number(__1, "TargetPosition")}; originalEntityMissing=true; native mapping/ledger/dispatch retained");
        }
        catch { /* Never guess a side, target or option on incomplete data. */ }
    }
}
