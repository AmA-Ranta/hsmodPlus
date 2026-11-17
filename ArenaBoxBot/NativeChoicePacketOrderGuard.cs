using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

internal static class NativeChoicePacketOrderGuard
{
    private static readonly ConditionalWeakTable<GameState, ChoicePacketOrder> s_orders = new ConditionalWeakTable<GameState, ChoicePacketOrder>();
    private static Harmony s_harmony;
    private static MethodInfo s_canProcess;
    private static float s_nextInstall;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5f;
        Harmony candidate = null;
        try
        {
            var incoming = AccessTools.Method(typeof(GameState), "OnEntityChoices", new[] { typeof(Network.EntityChoices) });
            var chosen = AccessTools.Method(typeof(GameState), "OnEntitiesChosen", new[] { typeof(Network.EntitiesChosen) });
            var queued = AccessTools.Method(typeof(GameState), "ProcessAllQueuedChoices", Type.EmptyTypes);
            s_canProcess = AccessTools.Method(typeof(GameState), "CanProcessEntitiesChosen", new[] { typeof(Network.EntitiesChosen) });
            if (incoming == null || chosen == null || queued == null || s_canProcess?.ReturnType != typeof(bool)) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.choice-packet-order");
            candidate.Patch(incoming, prefix: new HarmonyMethod(typeof(NativeChoicePacketOrderGuard), nameof(BeforeChoices)));
            // Replace both calls rather than patch the small private predicate,
            // which can be inlined by Mono. Its original checks still run.
            var replacement = new HarmonyMethod(typeof(NativeChoicePacketOrderGuard), nameof(GuardCalls));
            candidate.Patch(chosen, transpiler: replacement);
            candidate.Patch(queued, transpiler: replacement);
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("Choice packet ordering guard installed: native results wait for their received deferred choice; no synthetic choices or queue clearing");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("Choice packet ordering guard unavailable: " + ex.GetType().Name);
        }
    }

    private static string GameKey() => GameMgr.Get()?.GetGameHandle().ToString();

    private static void BeforeChoices(GameState __instance, Network.EntityChoices __0)
    {
        try
        {
            if (__instance != null && __0 != null)
                s_orders.GetValue(__instance, CreateOrder).Receive(GameKey(), __0.PlayerId, __0.ID);
        }
        catch { /* An unknown packet keeps the original path. */ }
    }

    private static ChoicePacketOrder CreateOrder(GameState state) => new ChoicePacketOrder();

    private static bool CanProcessChosenInOrder(GameState state, Network.EntitiesChosen chosen)
    {
        // This is the original read-only predicate. Do not consume a result,
        // send a choice, clear a choice map or advance a native task here.
        var nativeAllowed = (bool)s_canProcess.Invoke(state, new object[] { chosen });
        if (!nativeAllowed || state == null || chosen == null) return nativeAllowed;
        try
        {
            if (!s_orders.TryGetValue(state, out var order)) return nativeAllowed;
            var current = state.GetEntityChoices(chosen.PlayerId);
            var allowed = order.AllowChosen(GameKey(), chosen.PlayerId, chosen.ID, current?.ID,
                nativeAllowed, out var firstDeferred,
                state.GetPowerProcessor()?.HasEarlyConcedeTaskList() == true);
            if (firstDeferred)
                ArenaBoxBotPlugin.LogSource?.LogWarning("Choice结果顺序保护: game=" + GameKey() +
                    "; player=" + chosen.PlayerId + "; choiceId=" + chosen.ID +
                    "; registeredChoice=" + current?.ID +
                    "; received choice awaits native registration; keep original queued packet, no synthetic ACK");
            return allowed;
        }
        catch { return nativeAllowed; }
    }

    private static IEnumerable<CodeInstruction> GuardCalls(IEnumerable<CodeInstruction> instructions)
    {
        var guard = AccessTools.Method(typeof(NativeChoicePacketOrderGuard), nameof(CanProcessChosenInOrder));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(s_canProcess))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = guard;
                ++replaced;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("Expected exactly one native chosen predicate call; found " + replaced);
    }
}
