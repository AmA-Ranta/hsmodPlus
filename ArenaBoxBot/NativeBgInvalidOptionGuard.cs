using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

// The native AllSubs fallback can return an explicitly invalid main option.
// Refuse the captured no-target case, then use the existing file-page refresh
// protocol. No alternate target/action, fake ACK or ledger/queue reset.
internal static class NativeBgInvalidOptionGuard
{
    private static Harmony s_harmony;
    private static float s_nextInstall;
    private static MethodInfo s_refresh;
    private static MethodInfo s_directConfigured;
    private static readonly BgInvalidOptionRefreshBudget s_budget = new BgInvalidOptionRefreshBudget();
    private static string s_lastBlock;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5;
        Harmony candidate = null;
        try
        {
            var bot = AccessTools.TypeByName("HsMod.BgBoxAiBot");
            var adapter = AccessTools.TypeByName("HsMod.BoxAiShadowAdapter");
            var rec = adapter?.Assembly.GetType("HsMod.BoxAiRecommendation");
            var map = AccessTools.Method(bot, "MapBattlegroundsOption", new[] { typeof(GameState), typeof(Network.Options), rec });
            var execute = AccessTools.Method(bot, "TryExecuteOption", new[] { typeof(GameState), typeof(Network.Options) });
            var recover = AccessTools.Method(bot, "TryRecoverFromStall", new[] { typeof(GameState) });
            s_refresh = AccessTools.Method(adapter, "RequestPageStateRefresh", new[] { typeof(GameState), typeof(Network.Options) });
            s_directConfigured = AccessTools.Method(adapter, "DirectConfigured", Type.EmptyTypes);
            if (map?.ReturnType.FullName != "HsMod.BoxAiMappedAction" || execute?.ReturnType != typeof(void) ||
                recover?.ReturnType != typeof(bool) || s_refresh?.ReturnType != typeof(void) ||
                s_directConfigured?.ReturnType != typeof(bool)) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.bg-invalid-option");
            candidate.Patch(map, postfix: new HarmonyMethod(typeof(NativeBgInvalidOptionGuard), nameof(AfterMap)));
            candidate.Patch(execute, prefix: new HarmonyMethod(typeof(NativeBgInvalidOptionGuard), nameof(BeforeExecute)));
            candidate.Patch(recover, prefix: new HarmonyMethod(typeof(NativeBgInvalidOptionGuard), nameof(BeforeRecover)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG invalid-option guard installed: reject targetless REQ_TARGET_TO_PLAY; bounded existing Box page refresh; no combat option execution or synthetic ACK");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("BG invalid-option guard unavailable: " + ex.GetType().Name);
        }
    }

    private static object Field(object value, string name)
        => value?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);

    private static bool? OwnRecruitPhase(GameState state)
    {
        if (state == null) return null;
        if (!state.IsGameCreated() || state.IsGameOverNowOrPending() || state.WasConcedeRequested()) return false;
        var friendly = state.GetFriendlySidePlayer();
        var opposing = state.GetOpposingSidePlayer();
        if (friendly == null || opposing == null) return null;
        return friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0;
    }

    private static bool BeforeExecute(GameState __0)
    {
        try { return OwnRecruitPhase(__0) != false; }
        catch { return true; }
    }

    private static bool BeforeRecover(GameState __0, ref bool __result)
    {
        try
        {
            if (OwnRecruitPhase(__0) != false) return true;
            __result = false;
            return false;
        }
        catch { return true; }
    }

    internal static bool RejectTargetlessMapping(Network.Options packet, object mapped, out int source)
    {
            source = 0;
            if (mapped?.GetType().FullName != "HsMod.BoxAiMappedAction" ||
                !(Field(mapped, "Legal") is bool legal) || !legal || packet?.List == null ||
                !(Field(mapped, "OptionIndex") is int index) || index < 0 || index >= packet.List.Count ||
                !(Field(mapped, "SubOptionIndex") is int sub) || sub != -1 ||
                !(Field(mapped, "TargetEntityId") is int target) || target != 0 ||
                !(Field(mapped, "Position") is int position) || position != 0) return false;
            var option = packet.List[index];
            var main = option?.Main;
            if (option?.Type != Network.Options.Option.OptionType.POWER || main?.PlayErrorInfo == null ||
                main.PlayErrorInfo.PlayError != PlayErrors.ErrorType.REQ_TARGET_TO_PLAY ||
                (main.Targets != null && main.Targets.Count != 0) ||
                (option.Subs != null && option.Subs.Count != 0) ||
                !(Field(mapped, "SourceEntityId") is int mappedSource) || mappedSource != main.ID) return false;
            var legalField = mapped.GetType().GetField("Legal");
            var reasonField = mapped.GetType().GetField("Reason");
            if (legalField?.FieldType != typeof(bool) || reasonField?.FieldType != typeof(string)) return false;
            // Only downgrade the mapping result. The packet and execution
            // ledger remain untouched; original cannotExecute handling runs.
            legalField.SetValue(mapped, false);
            reasonField.SetValue(mapped, "当前选项要求目标但没有合法目标；等待新建议");
            source = mappedSource;
            return true;
    }

    private static void AfterMap(GameState __0, Network.Options __1, object __2, object __result)
    {
        try
        {
            if (!RejectTargetlessMapping(__1, __result, out var source)) return;
            var request = RecommendationMemberAccess.ReadText(__2, "RequestId");
            var server = Network.Get()?.GetLastGameServerJoined();
            var key = BgInvalidOptionRefreshBudget.TurnIdentity(GameMgr.Get()?.GetGameHandle() ?? 0,
                server?.Address, server?.Port ?? 0, server?.ClientHandle ?? 0, __0.GetTurn());
            var blockKey = key + ":" + __1.ID + ":" + request;
            if (s_lastBlock != blockKey)
            {
                s_lastBlock = blockKey;
                ArenaBoxBotPlugin.LogSource?.LogWarning("BgAI拒绝无目标无效选项: requestId=" + request +
                    "; optionsId=" + __1.ID + "; source=" + source + "; error=REQ_TARGET_TO_PLAY; targets=0; no input/ACK/ledger change");
            }
            if (s_directConfigured.Invoke(null, null) is bool direct && !direct &&
                s_budget.ShouldRefresh(key, Time.realtimeSinceStartup, OwnRecruitPhase(__0) == true))
            {
                s_refresh.Invoke(null, new object[] { __0, __1 });
                ArenaBoxBotPlugin.LogSource?.LogWarning("BgAI无效建议页面回读: requestId=" + request +
                    "; optionsId=" + __1.ID + "; requested existing page refresh only; recovery requires a newer executable recommendation");
            }
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("BG invalid-option inspection unavailable: " + ex.GetType().Name);
        }
    }
}
