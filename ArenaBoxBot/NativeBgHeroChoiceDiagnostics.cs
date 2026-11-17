using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

// Game1511 remained at waitHeroChoice until the server selected a hero.
// Observe the existing readiness result and its individual UI predicates;
// never call the stateful recommendation reader or release the display gate.
internal static class NativeBgHeroChoiceDiagnostics
{
    private static readonly FieldInfo s_waiting = typeof(MulliganManager).GetField(
        "m_waitingForUserInput", BindingFlags.Instance | BindingFlags.NonPublic);
    private static Harmony s_harmony;
    private static float s_nextInstall, s_nextSample, s_nextRepeat;
    private static string s_lastKey;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5f;
        Harmony candidate = null;
        try
        {
            var target = AccessTools.Method(AccessTools.TypeByName("HsMod.Utilities.MulliganInteraction"),
                "IsUiReady", new[] { typeof(GameState), typeof(MulliganManager) });
            if (target == null || target.ReturnType != typeof(bool) || s_waiting == null) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.bg-hero-readiness-observer");
            candidate.Patch(target, postfix: new HarmonyMethod(typeof(NativeBgHeroChoiceDiagnostics), nameof(AfterUiReady)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG hero readiness diagnostics installed (original result observed; no input or gate changes)");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("BG hero readiness diagnostics unavailable: " + ex.GetType().Name);
        }
    }

    private static void AfterUiReady(GameState __0, MulliganManager __1, bool __result)
    {
        try
        {
            var now = Time.realtimeSinceStartup;
            if (now < s_nextSample) return;
            s_nextSample = now + 1f;
            var game = GameMgr.Get();
            var choice = __0?.GetFriendlyEntityChoices();
            if (game == null || !game.IsBattlegrounds() || choice == null ||
                choice.ChoiceType.ToString() != "MULLIGAN") return;

            var managerPresent = __1 != null;
            bool? enabled = managerPresent ? __1.isActiveAndEnabled : (bool?)null;
            bool? active = managerPresent ? __1.IsMulliganActive() : (bool?)null;
            bool? intro = managerPresent ? __1.IsMulliganIntroActive() : (bool?)null;
            var waiting = managerPresent ? s_waiting.GetValue(__1) : null;
            var loading = LoadingScreen.Get();
            bool? transitioning = loading != null ? loading.IsTransitioning() : (bool?)null;
            var chooseOne = __0.GetBooleanGameOption(GameEntityOption.MULLIGAN_IS_CHOOSE_ONE);
            GameObject button = null;
            if (managerPresent)
                button = chooseOne ? (__1.mulliganButtonWidget != null ? __1.mulliganButtonWidget.gameObject : null)
                    : __1.GetMulliganButton();
            bool? buttonActive = button != null ? button.activeInHierarchy : (bool?)null;
            var input = InputManager.Get();
            // The current client's PermitDecisionMakingInput only reads spectator
            // state and a friendly-player tag; its IL is checked by the verifier.
            bool? permitted = input != null ? input.PermitDecisionMakingInput() : (bool?)null;
            var predicates = "manager=" + managerPresent + "; enabled=" + enabled + "; active=" + active +
                "; intro=" + intro + "; waiting=" + waiting + "; loading=" + (loading != null) +
                "; transitioning=" + transitioning + "; chooseOne=" + chooseOne +
                "; button=" + (button != null) + "; buttonActive=" + buttonActive +
                "; input=" + (input != null) + "; permitted=" + permitted;
            var key = game.GetGameHandle() + ":" + choice.ID + ":" + __result + ":" + predicates;
            if (key == s_lastKey && now < s_nextRepeat) return;
            s_lastKey = key;
            s_nextRepeat = now + 5f;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI英雄界面就绪诊断: game=" + game.GetGameHandle() +
                "; turn=" + __0.GetTurn() + "; choice=" + choice.ID + "; entities=" + string.Join(",", choice.Entities) +
                "; nativeUiReady=" + __result + "; " + predicates +
                "; observed original IsUiReady result; no choice submission or readiness override");
        }
        catch { /* Diagnostic failures never alter the native result. */ }
    }
}
