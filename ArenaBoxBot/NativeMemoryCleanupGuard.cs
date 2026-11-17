using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

/// <summary>
/// Keep HsMod's original cleanup and cadence, but defer its due resource eviction
/// during gameplay, Arena, loading and queueing. This never stops or restarts a task.
/// </summary>
internal static class NativeMemoryCleanupGuard
{
    private static Harmony s_harmony;
    private static FieldInfo s_nextCleanup;
    private static float s_nextInstall, s_nextDeferredNotice;
    private static readonly MemoryCleanupPolicy s_policy = new MemoryCleanupPolicy();

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.realtimeSinceStartup < s_nextInstall) return;
        s_nextInstall = Time.realtimeSinceStartup + 5f;
        try
        {
            var type = Type.GetType("HsMod.Plugin, HsMod");
            var method = type?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .SingleOrDefault(m => m.Name == "MemoryCleanupTick" && m.ReturnType == typeof(void) && m.GetParameters().Length == 0);
            var next = type?.GetField("s_nextMemoryCleanup", BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null || next == null || next.FieldType != typeof(float)) return;
            s_nextCleanup = next;
            var harmony = new Harmony("local.hsmod.arenaboxbot.idle-resource-cleanup");
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(NativeMemoryCleanupGuard), nameof(BeforeCleanup)),
                postfix: new HarmonyMethod(typeof(NativeMemoryCleanupGuard), nameof(AfterCleanup)));
            s_harmony = harmony;
            ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod resource cleanup guard installed: preserve native cadence; defer during gameplay, Arena, loading or matchmaking");
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod resource cleanup guard install deferred: " + ex.GetType().Name);
        }
    }

    private static bool BeforeCleanup(out long __state)
    {
        __state = 0;
        try
        {
            if (!HsModNativeArena.IsRuntimeEnabled()) return true;
            var now = Time.realtimeSinceStartup;
            // The native method returns immediately until its own deadline.
            // Read that field, never rewrite its timer or force extra cleanup.
            if (now < (float)s_nextCleanup.GetValue(null)) return true;
            var scene = SceneMgr.Get();
            var game = GameMgr.Get();
            var mode = scene?.GetMode().ToString() ?? "UNKNOWN";
            var safe = s_policy.Observe(scene != null && game != null ? mode : "UNKNOWN",
                scene == null || scene.IsTransitioning(), game == null || game.IsFindingGame(), now);
            if (!safe)
            {
                if (now >= s_nextDeferredNotice)
                {
                    s_nextDeferredNotice = now + 60f;
                    ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod resource cleanup deferred: scene=" + mode + "; native deadline retained; waiting for a quiet menu");
                }
                return false;
            }
            __state = Stopwatch.GetTimestamp();
            return true;
        }
        catch
        {
            // An unreadable boundary cannot authorize an expensive resource eviction.
            s_policy.Observe("UNKNOWN", true, true, Time.realtimeSinceStartup);
            return false;
        }
    }

    private static void AfterCleanup(long __state)
    {
        if (__state == 0) return;
        try
        {
            var ms = (Stopwatch.GetTimestamp() - __state) * 1000d / Stopwatch.Frequency;
            ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod native resource cleanup invoked in quiet menu: callMilliseconds=" + ms.ToString("0.0") + "; asynchronous resource completion unverified");
        }
        catch { }
    }
}
