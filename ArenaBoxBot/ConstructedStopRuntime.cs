using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using UnityEngine;
using ArenaControl.Contracts;

namespace ArenaBoxBot;

// A stop boundary protocol only. It never selects a mode or enables a script.
internal sealed class ConstructedStopRuntime : MonoBehaviour
{
    private static bool s_installed;
    private static float s_nextInstall, s_nextWarning, s_nextPoll;
    private static string RequestPath => ArenaRuntimePaths.File(ConstructedStopContract.RequestFile);
    private static string ReceiptPath => ArenaRuntimePaths.File(ConstructedStopContract.ReceiptFile);

    private void Update()
    {
        if (!s_installed && Time.unscaledTime >= s_nextInstall)
        {
            s_nextInstall = Time.unscaledTime + 5f;
            try
            {
                var target = AccessTools.Method(typeof(GameMgr), "FindGameInternal");
                if (target == null || target.ReturnType != typeof(void)) throw new MissingMethodException("GameMgr.FindGameInternal");
                new Harmony("local.hsmod.arenaboxbot.constructed-stop").Patch(target,
                    prefix: new HarmonyMethod(typeof(ConstructedStopRuntime), nameof(BeforeFindGame)));
                s_installed = true;
                ArenaBoxBotPlugin.LogSource?.LogInfo("Constructed stop-before-next-match guard installed");
            }
            catch (Exception ex) { Warn("stop guard install deferred: " + ex.GetType().Name); }
        }
        if (Time.unscaledTime >= s_nextPoll)
        {
            s_nextPoll = Time.unscaledTime + 0.25f;
            TryCompleteStop();
        }
    }

    private static bool BeforeFindGame()
    {
        if (NativeBgCompletedGameGuard.BlockNextMatchIfReached())
        {
            TryCompleteStop();
            return false;
        }
        // A valid pending stop blocks the next queue even while the previous
        // page is transitioning. Current-game actions remain enabled until a
        // positively known idle boundary permits releasing the runtime.
        if (!TryReadCurrentRequest(out _)) return true;
        TryCompleteStop();
        return false;
    }

    private static bool TryReadCurrentRequest(out ConstructedStopRequest request, bool requireRunning = true)
    {
        request = null;
        if (!File.Exists(RequestPath) || HsModNativeArena.IsNativeArenaModeSelected() ||
            (requireRunning && !HsModNativeArena.IsRuntimeEnabled())) return false;
        try
        {
            request = JsonUtility.FromJson<ConstructedStopRequest>(File.ReadAllText(RequestPath));
            using (var process = Process.GetCurrentProcess())
                return ConstructedStopContract.Matches(request, process.Id, process.StartTime.ToUniversalTime().Ticks,
                    HsModNativeArena.ReadHsModModeText(), DateTime.UtcNow.Ticks);
        }
        catch (Exception ex) { Warn("stop request unavailable: " + ex.GetType().Name); return false; }
    }

    private static void TryCompleteStop()
    {
        if (!TryReadCurrentRequest(out var request, false)) return;
        try
        {
            var scene = SceneMgr.Get(); var game = GameMgr.Get(); var network = Network.Get();
            if (scene == null || game == null || network == null ||
                !ConstructedStopContract.IsSafeBoundary(scene.GetMode().ToString(), scene.GetNextMode().ToString(),
                    scene.IsTransitioning(), game.IsFindingGame(), network.IsConnectedToGameServer())) return;
            if (!HsModNativeArena.SetRuntimeEnabled(false) || HsModNativeArena.IsRuntimeEnabled())
                throw new InvalidOperationException("Native runtime off unconfirmed");
            request.CompletedAtUtcTicks = DateTime.UtcNow.Ticks;
            var pending = ReceiptPath + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(pending, JsonUtility.ToJson(request));
                if (File.Exists(ReceiptPath)) File.Replace(pending, ReceiptPath, null);
                else File.Move(pending, ReceiptPath);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            File.Delete(RequestPath);
            // The receipt remains until the controller confirms completion.
            ArenaBoxBotPlugin.LogSource?.LogInfo("Constructed controller stop completed before next match: requestId=" +
                request.RequestId + "; mode=" + request.Mode + "; scene=" + scene.GetMode());
        }
        catch (Exception ex) { Warn("stop completion deferred: " + ex.GetType().Name); }
    }

    private static void Warn(string text)
    {
        if (Time.unscaledTime < s_nextWarning) return;
        s_nextWarning = Time.unscaledTime + 15f;
        ArenaBoxBotPlugin.LogSource?.LogWarning("Constructed " + text);
    }
}
