using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

internal static class NativeBgPositionProgressGuard
{
    private sealed class Scope
    {
        internal GameState State;
        internal Network.Options Packet;
        internal string Game, Board;
        internal int Turn;
        internal Dictionary<int, int> Positions;
    }

    private static readonly BgPositionProgress s_progress = new BgPositionProgress();
    private static Harmony s_harmony;
    private static float s_nextInstall;
    [ThreadStatic] private static Scope s_scope;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5f;
        Harmony candidate = null;
        try
        {
            var type = AccessTools.TypeByName("HsMod.BgBoxAiBot");
            var reposition = AccessTools.Method(type, "TryRepositionOnce", new[] { typeof(GameState), typeof(Network.Options) });
            var send = AccessTools.Method(typeof(GameState), "SendOption", Type.EmptyTypes);
            if (reposition == null || reposition.ReturnType != typeof(bool) || send == null) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.bg-position-progress");
            candidate.Patch(reposition,
                prefix: new HarmonyMethod(typeof(NativeBgPositionProgressGuard), nameof(BeforeReposition)),
                postfix: new HarmonyMethod(typeof(NativeBgPositionProgressGuard), nameof(AfterReposition)),
                finalizer: new HarmonyMethod(typeof(NativeBgPositionProgressGuard), nameof(FinishReposition)));
            candidate.Patch(send, prefix: new HarmonyMethod(typeof(NativeBgPositionProgressGuard), nameof(ObserveSend)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG position progress guard installed: wait for actual friendly board progress; publication/options IDs do not acknowledge a move; native send path retained");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("BG position progress guard unavailable: " + ex.GetType().Name);
        }
    }

    private static bool BeforeReposition(GameState __0, Network.Options __1, ref bool __result, out Scope __state)
    {
        __state = null;
        s_scope = null;
        try
        {
            var scope = Snapshot(__0, __1);
            if (scope == null) return true;
            var decision = s_progress.Observe(scope.Game, scope.Turn, scope.Board, Time.realtimeSinceStartup, out var notice);
            if (decision != BgPositionProgress.Decision.Allow)
            {
                if (notice)
                    ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI站位等待确认: game=" + scope.Game +
                        "; turn=" + scope.Turn + "; entity=" + s_progress.Source + "; from=" + s_progress.From +
                        "; to=" + s_progress.To + "; attempts=" + s_progress.Attempts + "; reason=" + decision +
                        "; actual board unchanged; keep advice and other actions, no synthetic ACK");
                // Both native callers use this only to choose a waiting label;
                // neither consumes end_turn advice nor ends the recruit phase.
                __result = false;
                return false;
            }
            s_scope = __state = scope;
        }
        catch { /* Unknown state retains the original path. */ }
        return true;
    }

    private static void ObserveSend(GameState __instance)
    {
        try
        {
            var scope = s_scope;
            if (scope == null || !ReferenceEquals(scope.State, __instance)) return;
            var index = __instance.GetSelectedOption();
            if (index < 0 || index >= scope.Packet.List.Count) return;
            var source = scope.Packet.List[index]?.Main?.ID ?? 0;
            if (!scope.Positions.TryGetValue(source, out var from)) return;
            s_progress.Sent(scope.Game, scope.Turn, scope.Board, source, from,
                __instance.GetSelectedOptionPosition(), Time.realtimeSinceStartup);
        }
        catch { /* This prefix only observes an attempted original send. */ }
    }

    private static void AfterReposition(Scope __state)
    {
        if (ReferenceEquals(s_scope, __state)) s_scope = null;
    }

    private static Exception FinishReposition(Exception __exception, Scope __state)
    {
        AfterReposition(__state);
        return __exception;
    }

    private static Scope Snapshot(GameState state, Network.Options packet)
    {
        if (state == null || packet?.List == null || state.GetTurn() < 1) return null;
        var positions = new Dictionary<int, int>();
        foreach (var pair in state.GetEntityMap())
        {
            var entity = pair.Value;
            // Match the current native TryRepositionOnce filter exactly:
            // minion, PLAY (1), FRIENDLY (1), positive board position.
            if (entity != null && entity.IsMinion() && (int)entity.GetZone() == 1 &&
                (int)entity.GetControllerSide() == 1 && entity.GetZonePosition() >= 1)
                positions[entity.GetEntityId()] = entity.GetZonePosition();
        }
        if (positions.Count == 0 || positions.Count > 7) return null;
        return new Scope
        {
            State = state, Packet = packet, Turn = state.GetTurn(), Game = GameMgr.Get()?.GetGameHandle().ToString(),
            Positions = positions,
            Board = string.Join(",", positions.OrderBy(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key + ":" + p.Value))
        };
    }
}
