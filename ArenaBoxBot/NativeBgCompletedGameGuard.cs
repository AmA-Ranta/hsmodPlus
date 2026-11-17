using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using ArenaControl.Contracts;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

internal static class NativeBgCompletedGameGuard
{
    private static readonly BgCompletedGameBudget s_budget = new BgCompletedGameBudget();
    private static Harmony s_harmony;
    private static FieldInfo s_limitField;
    private static float s_nextInstall, s_nextWarning;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5f;
        Harmony candidate = null;
        try
        {
            var result = AccessTools.Method(typeof(GameState), "OnGameOver", new[] { typeof(TAG_PLAYSTATE) });
            var history = AccessTools.Method(typeof(GameState), "OnPowerHistory", new[] { typeof(List<Network.PowerHistory>) });
            var reset = AccessTools.Method(AccessTools.TypeByName("HsMod.BgSessionStats"), "ResetSessionCount", Type.EmptyTypes);
            s_limitField = AccessTools.Field(AccessTools.TypeByName("HsMod.PluginConfig"), "wildLadderStopAfterGames");
            if (result == null || history == null || reset == null || s_limitField == null) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.bg-completed-game-budget");
            candidate.Patch(result, prefix: new HarmonyMethod(typeof(NativeBgCompletedGameGuard), nameof(ObserveGameOver)));
            candidate.Patch(history, prefix: new HarmonyMethod(typeof(NativeBgCompletedGameGuard), nameof(ObserveServerComplete)));
            candidate.Patch(reset, postfix: new HarmonyMethod(typeof(NativeBgCompletedGameGuard), nameof(ResetSession)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG completed-game budget guard installed: terminal callback plus server COMPLETE count independently of rating/CSV; existing postgame stop protocol retained");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            Warn("install unavailable: " + ex.GetType().Name);
        }
    }

    private static void ResetSession() => s_budget.Reset();

    private static void ObserveGameOver(GameState __instance, TAG_PLAYSTATE __0)
    {
        try
        {
            if (__instance == null) return;
            var terminal = __0 == TAG_PLAYSTATE.WON || __0 == TAG_PLAYSTATE.LOST || __0 == TAG_PLAYSTATE.TIED;
            RecordEvidence(terminal, false);
        }
        catch (Exception ex) { Warn("terminal observation unavailable: " + ex.GetType().Name); }
    }

    private static void ObserveServerComplete(GameState __instance, List<Network.PowerHistory> __0)
    {
        try
        {
            var entity = __instance?.GetGameEntity();
            if (entity == null || __0 == null) return;
            foreach (var power in __0)
                if (power is Network.HistTagChange tag && tag.Entity == entity.GetEntityId() &&
                    tag.Tag == (int)GAME_TAG.STATE && tag.Value == (int)TAG_STATE.COMPLETE)
                {
                    RecordEvidence(false, true);
                    break;
                }
        }
        catch (Exception ex) { Warn("server completion observation unavailable: " + ex.GetType().Name); }
    }

    private static void RecordEvidence(bool terminal, bool serverComplete)
    {
            var game = GameMgr.Get();
            if (!Eligible(game)) return;
            var server = Network.Get()?.GetLastGameServerJoined();
            var handle = game.GetGameHandle();
            if (handle == 0) return;
            var identity = handle + ":" + server?.Address + ":" + server?.Port + ":" + server?.ClientHandle;
            if (s_budget.Record(identity, true, terminal, serverComplete))
                ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI真实终局计数: game=" + handle + "; terminalAndServerComplete=True" +
                    "; completed=" + s_budget.Completed + "; limit=" + ReadLimit() + "; " + NativeStatsDiagnostic() +
                    "; rating/CSV not required; no synthetic placement or rating");
            BlockNextMatchIfReached();
    }

    private static bool Eligible(GameMgr game)
        => game != null && HsModNativeArena.IsRuntimeEnabled() &&
           HsModNativeArena.ReadHsModModeText() == "酒馆战棋" && game.IsBattlegrounds() &&
           !game.IsBattlegroundsTutorial() && !game.IsBattlegroundVsAIGame() && !game.IsFriendlyBattlegrounds();

    private static int ReadLimit()
    {
        var entry = s_limitField?.GetValue(null) as ConfigEntryBase;
        return entry?.BoxedValue is int value ? value : 0;
    }

    private static string NativeStatsDiagnostic()
    {
        try
        {
            var type = AccessTools.TypeByName("HsMod.BgSessionStats");
            var instance = AccessTools.Field(type, "Instance")?.GetValue(null);
            if (instance == null) return "nativeStats=unavailable";
            return "nativeStats.inMatch=" + AccessTools.Field(type, "inMatch")?.GetValue(instance) +
                "; nativeStats.ratingBefore=" + AccessTools.Field(type, "ratingBefore")?.GetValue(instance) +
                "; nativeStats.place=" + AccessTools.Field(type, "place")?.GetValue(instance) +
                "; nativeStats.pending=" + (AccessTools.Field(type, "pending")?.GetValue(instance) != null);
        }
        catch { return "nativeStats=unreadable"; }
    }

    internal static bool BlockNextMatchIfReached()
    {
        // Once the terminal callback was seen, lobby GameMgr may no longer
        // report a battleground. Current mode/runtime and the session count
        // retain the stop intent through that transition.
        if (s_harmony == null || !HsModNativeArena.IsRuntimeEnabled() ||
            HsModNativeArena.ReadHsModModeText() != "酒馆战棋" || !s_budget.Reached(ReadLimit())) return false;
        try
        {
            var requestPath = ArenaRuntimePaths.File(ConstructedStopContract.RequestFile);
            if (File.Exists(requestPath)) return true; // Never replace another stop request.
            using (var process = Process.GetCurrentProcess())
            {
                var request = new ConstructedStopRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"), GameProcessId = process.Id,
                    GameStartedAtUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    RequestedAtUtcTicks = DateTime.UtcNow.Ticks, Mode = "酒馆战棋"
                };
                var pending = requestPath + ".pending-" + request.RequestId;
                try
                {
                    File.WriteAllText(pending, JsonUtility.ToJson(request));
                    File.Move(pending, requestPath);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
                ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI场数局后停止请求: completed=" + s_budget.Completed +
                    "; limit=" + ReadLimit() + "; requestId=" + request.RequestId + "; original terminal/idle boundary required");
            }
        }
        catch (Exception ex) { Warn("stop request deferred; next queue remains blocked: " + ex.GetType().Name); }
        return true;
    }

    private static void Warn(string message)
    {
        if (Time.unscaledTime < s_nextWarning) return;
        s_nextWarning = Time.unscaledTime + 15f;
        ArenaBoxBotPlugin.LogSource?.LogWarning("BG completed-game budget " + message);
    }
}
