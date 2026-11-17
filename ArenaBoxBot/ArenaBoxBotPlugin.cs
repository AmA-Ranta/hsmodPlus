using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Blizzard.T5.Services;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PegasusShared;
using UnityEngine;

namespace ArenaBoxBot;

/// <summary>
/// Resolves controller/box bridge files relative to the game that loaded this
/// plugin.  The old build hard-coded D:\\Hearthstone, which made the manual
/// path fields in the controller ineffective on another drive.
/// </summary>
internal static class ArenaRuntimePaths
{
    internal static string HsModDirectory => System.IO.Path.Combine(Paths.GameRootPath, "BepInEx", "HsMod");
    internal static string ConfigDirectory => System.IO.Path.Combine(Paths.GameRootPath, "BepInEx", "config");
    internal static string File(string name) => System.IO.Path.Combine(HsModDirectory, name);
    internal static string Config(string name) => System.IO.Path.Combine(ConfigDirectory, name);
}

[BepInPlugin("local.hsmod.arenaboxbot", "Arena Box Bot", "0.1.0")]
public sealed class ArenaBoxBotPlugin : BaseUnityPlugin
{
    internal static ManualLogSource LogSource;

    private void Awake()
    {
        LogSource = Logger;
        JournalOpenDiagnostics.EnsureInstalled();
        NativeMemoryCleanupGuard.EnsureInstalled();
        HsModNativeDecks.EnsureInstalled();
        ArenaSettings.Bind(Config);
        ArenaSettingsUi.Attach();
        var go = new GameObject("ArenaBoxBotController");
        DontDestroyOnLoad(go);
        go.AddComponent<ArenaController>();
        go.AddComponent<HsModNativeArenaModeBridge>();
        go.AddComponent<ConstructedStopRuntime>();
        Logger.LogInfo("ArenaBoxBot loaded; Arena can be started from HsMod's native 竞技场 mode or the controller marker");
    }
}

/// <summary>
/// Reads the current HsMod runtime state without taking a compile-time
/// dependency on its private implementation.  The production HsMod build is
/// loaded separately by BepInEx, so this bridge also keeps the Arena plugin
/// usable when HsMod is temporarily absent during a reinstall.
/// </summary>
internal static class HsModNativeArena
{
    private static Type s_configType;
    private static FieldInfo s_modeField;
    private static FieldInfo s_runtimeField;
    private static float s_nextLookup;
    private static bool s_bridgeArenaLatched;
    private static string s_bridgeBaseMode;
    private static string s_virtualMode;
    private static readonly TimeCachedBoolean s_hsModeIntent = new TimeCachedBoolean(ReadHsModeIntent);
    private static readonly TimeCachedBoolean s_pluginModeIntent = new TimeCachedBoolean(ReadPluginModeIntent);
    private static readonly TimeCachedBoolean s_bridgeModeIntent = new TimeCachedBoolean(ReadBridgeModeIntent);
    private static readonly string[] ModeCycle = ArenaModeCatalog.All;

    internal static string ConfigReadDiagnostic => "hs=" + s_hsModeIntent.Diagnostic +
        ",plugin=" + s_pluginModeIntent.Diagnostic + ",bridge=" + s_bridgeModeIntent.Diagnostic;

    private static void InvalidateModeIntent()
    {
        s_hsModeIntent.Invalidate();
        s_pluginModeIntent.Invalidate();
        s_bridgeModeIntent.Invalidate();
    }

    private static bool ReadHsModeIntent() => ConfigRequestsArenaInFile(ArenaRuntimePaths.Config("HsMod.cfg"), "[自动脚本]");
    private static bool ReadPluginModeIntent() => ConfigRequestsArenaInFile(ArenaRuntimePaths.Config("local.hsmod.arenaboxbot.cfg"), "[竞技场脚本]");
    private static bool ReadBridgeModeIntent()
    {
        var path = ArenaRuntimePaths.File("hsmod-arena-mode.json");
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        return text.IndexOf("\"mode\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
            text.IndexOf("竞技场", StringComparison.Ordinal) >= 0;
    }

    internal static bool IsArenaMode()
    {
        try
        {
            EnsureFields();
            if (s_modeField == null) return false;
            var entry = s_modeField.GetValue(null);
            var value = entry?.GetType().GetProperty("Value")?.GetValue(entry, null);
            return string.Equals(DisplayMode(Convert.ToString(value)), "竞技场", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    internal static bool IsRuntimeEnabled()
    {
        try
        {
            EnsureFields();
            var entry = s_runtimeField?.GetValue(null);
            if (entry is bool direct) return direct;
            var value = entry?.GetType().GetProperty("Value")?.GetValue(entry, null);
            return value is bool enabled && enabled;
        }
        catch { return false; }
    }

    internal static bool SetRuntimeEnabled(bool enabled)
    {
        try
        {
            EnsureFields();
            if (s_runtimeField == null || !s_runtimeField.IsStatic) return false;
            var entry = s_runtimeField.GetValue(null);
            if (s_runtimeField.FieldType == typeof(bool))
            {
                s_runtimeField.SetValue(null, enabled);
                return true;
            }
            var property = entry?.GetType().GetProperty("Value");
            if (property == null || !property.CanWrite || property.PropertyType != typeof(bool)) return false;
            property.SetValue(entry, enabled, null);
            return true;
        }
        catch { return false; }
    }

    internal static bool IsNativeArenaRunning() => IsArenaMode() && IsRuntimeEnabled();

    /// <summary>
    /// Arena mode can be supplied by the native settings bridge before the
    /// installed HsMod binary has learned the new enum value.  The bridge
    /// stores the selected value in this small state file and mirrors it to
    /// PluginConfig when the live enum accepts it.  Reading it here keeps the
    /// Arena loop independent from the controller process.
    /// </summary>
    internal static bool IsNativeArenaModeSelected()
    {
        try
        {
            // The live native enum is already authoritative. On older HsMod
            // builds, share bounded configuration observations across the
            // many Update, OnGUI and execution-scope callers instead of
            // reopening and parsing the entire config for every getter.
            if (IsArenaMode()) return true;
            // The sidecar is only authoritative while the persisted HsMod
            // mode still requests Arena.  A controller or the F11 shortcut
            // may have switched back to a constructed mode while the game
            // was closed, leaving the old sidecar behind.
            if (!ConfigRequestsArena()) return false;
            if (s_bridgeModeIntent.Read()) return true;
        }
        catch { return false; }
        return IsArenaMode();
    }

    internal static bool IsNativeArenaAutomationRunning() => IsNativeArenaModeSelected() && IsRuntimeEnabled();

    internal static bool HasNativeArenaEnum()
    {
        try
        {
            EnsureFields();
            var entry = s_modeField?.GetValue(null);
            var valueProperty = entry?.GetType().GetProperty("Value");
            var enumType = valueProperty?.PropertyType;
            return enumType != null && enumType.IsEnum &&
                   Enum.GetNames(enumType).Any(name => string.Equals(DisplayMode(name), "竞技场", StringComparison.Ordinal));
        }
        catch { return false; }
    }

    internal static string SelectedModeText()
    {
        if (IsArenaMode()) return "竞技场";
        try
        {
            if (s_bridgeModeIntent.Read()) return "竞技场";
        }
        catch { }
        return "";
    }

    internal static void SetModeBridge(string mode)
    {
        InvalidateModeIntent();
        try
        {
            if (string.IsNullOrEmpty(mode) || !ModeCycle.Contains(mode, StringComparer.Ordinal))
                mode = "标准";
            // Persist the virtual five-value selector in the plugin-owned
            // config as well as HsMod's original config.  Older HsMod builds
            // cannot serialize the Arena enum; without this mirror a later
            // F11 cycle could appear to leave Arena but the next sync would
            // restore it from the stale Arena sidecar.
            if (ArenaSettings.ScriptMode != null &&
                Enum.TryParse(mode, out ArenaSettings.NativeScriptMode selectedMode))
                ArenaSettings.ScriptMode.Value = selectedMode;
            if (string.Equals(DisplayMode(mode), "竞技场", StringComparison.Ordinal))
            {
                s_bridgeBaseMode = ReadHsModModeText();
                s_bridgeArenaLatched = true;
                s_virtualMode = "竞技场";
                Directory.CreateDirectory(ArenaRuntimePaths.HsModDirectory);
                File.WriteAllText(ArenaRuntimePaths.File("hsmod-arena-mode.json"),
                    "{\"mode\":\"竞技场\",\"baseMode\":\"" + EscapeJson(s_bridgeBaseMode) + "\"}", Encoding.UTF8);
                // The installer patches the HsMod enum in supported builds;
                // use the real ConfigEntry when available. Older binaries
                // reject the value and keep the bridge file as compatibility.
                SetHsModModeText("竞技场");
            }
            else
            {
                s_bridgeArenaLatched = false;
                s_bridgeBaseMode = null;
                s_virtualMode = mode;
                try { File.Delete(ArenaRuntimePaths.File("hsmod-arena-mode.json")); } catch { }
                SetHsModModeText(mode);
                WriteHsModConfigMode(mode);
            }
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod Arena mode bridge update failed: " + ex.Message);
        }
        finally { InvalidateModeIntent(); }
    }

    internal static void SyncModeBridge()
    {
        var mode = ReadHsModModeText();
        if (!s_bridgeArenaLatched && string.IsNullOrEmpty(s_virtualMode)) s_virtualMode = mode;
        var bridgePath = ArenaRuntimePaths.File("hsmod-arena-mode.json");
        // The controller can write the desired mode before Hearthstone starts;
        // an old HsMod enum parser then falls back to 标准. Recover that
        // explicit choice from HsMod.cfg and materialize the bridge state.
        if (!s_bridgeArenaLatched && ConfigRequestsArena())
        {
            SetModeBridge("竞技场");
            return;
        }
        if (!ConfigRequestsArena() && File.Exists(bridgePath) && !IsArenaMode())
        {
            // Do not let a stale virtual-mode marker force Arena after the
            // user selected Standard/Wild/Casual/Battlegrounds in HsMod or
            // the controller.  The marker is recreated whenever Arena is
            // explicitly selected again.
            s_bridgeArenaLatched = false;
            s_bridgeBaseMode = null;
            s_virtualMode = mode;
            try { File.Delete(bridgePath); } catch { }
            return;
        }
        if (!s_bridgeArenaLatched)
        {
            if (!File.Exists(bridgePath)) return;
            s_bridgeArenaLatched = true;
            if (string.IsNullOrEmpty(s_bridgeBaseMode)) s_bridgeBaseMode = mode;
            s_virtualMode = "竞技场";
        }
        if (s_bridgeArenaLatched && !string.Equals(mode, "竞技场", StringComparison.Ordinal) &&
            (HasNativeArenaEnum() || (!string.IsNullOrEmpty(s_bridgeBaseMode) &&
            !string.Equals(mode, s_bridgeBaseMode, StringComparison.Ordinal))))
        {
            s_bridgeArenaLatched = false;
            s_bridgeBaseMode = null;
            s_virtualMode = mode;
            try { File.Delete(ArenaRuntimePaths.File("hsmod-arena-mode.json")); } catch { }
        }
    }

    internal static void HandleModeShortcut()
    {
        var current = string.IsNullOrEmpty(s_virtualMode)
            ? (IsNativeArenaModeSelected() ? "竞技场" : ReadHsModModeText())
            : s_virtualMode;
        var next = ArenaModeCatalog.Next(current);
        SetModeBridge(next);
        ArenaBoxBotPlugin.LogSource?.LogInfo("脚本模式切换: " + current + " -> " + next);
    }

    internal static string ReadHsModModeText()
    {
        try
        {
            EnsureFields();
            var entry = s_modeField?.GetValue(null);
            var value = entry?.GetType().GetProperty("Value")?.GetValue(entry, null);
            return DisplayMode(Convert.ToString(value));
        }
        catch { return "标准"; }
    }

    /// <summary>
    /// HsMod persists English enum identifiers while its UI and the
    /// controller use Chinese display text. Keep the conversion in one place
    /// so config files, the virtual Arena mode and F11 all agree.
    /// </summary>
    internal static string DisplayMode(string mode) => ArenaModeCatalog.ToDisplay(mode);

    internal static string HsModModeValue(string mode) => ArenaModeCatalog.ToNative(mode);

    private static bool ConfigRequestsArena()
    {
        return s_hsModeIntent.Read() || s_pluginModeIntent.Read();
    }

    private static bool ConfigRequestsArenaInFile(string path, string expectedSection)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var section = false;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    section = string.Equals(line, expectedSection, StringComparison.Ordinal);
                    continue;
                }
                if (!section || !line.StartsWith("脚本模式", StringComparison.Ordinal)) continue;
                var equal = line.IndexOf('=');
                if (equal < 0) continue;
                var value = line.Substring(equal + 1).Trim().Trim('"');
                return DisplayMode(value) == "竞技场" || value == "竞技场";
            }
        }
        catch { }
        return false;
    }

    private static void WriteHsModConfigMode(string mode)
    {
        try
        {
            var path = ArenaRuntimePaths.Config("HsMod.cfg");
            if (!File.Exists(path)) return;
            var lines = new List<string>(File.ReadAllLines(path));
            var section = false;
            var replaced = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    section = string.Equals(line, "[自动脚本]", StringComparison.Ordinal);
                    continue;
                }
                if (section && line.StartsWith("脚本模式", StringComparison.Ordinal) && line.IndexOf('=') >= 0)
                {
            lines[i] = "脚本模式 = " + HsModModeValue(mode);
                    replaced = true;
                    break;
                }
            }
            if (!replaced) return;
            File.WriteAllLines(path, lines, Encoding.UTF8);
        }
        catch { }
    }

    private static void SetHsModModeText(string mode)
    {
        try
        {
            EnsureFields();
            var entry = s_modeField?.GetValue(null);
            var property = entry?.GetType().GetProperty("Value");
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum) return;
            var display = DisplayMode(mode);
            var names = Enum.GetNames(property.PropertyType);
            if (display == "竞技场" && !names.Any(name => DisplayMode(name) == "竞技场")) return;
            var native = HsModModeValue(display);
            // Prefer the real Arena enum when this HsMod build has one.  The
            // old four-value build has no Arena member and therefore falls
            // back to Standard while the plugin-owned virtual selector keeps
            // the Arena choice.  Checking the native value first would pick
            // Standard even on a newer build that genuinely supports Arena.
            var candidate = display == "竞技场"
                ? names.FirstOrDefault(name => DisplayMode(name) == display)
                : names.FirstOrDefault(name => name == native || DisplayMode(name) == display);
            if (candidate == null) return;
            property.SetValue(entry, Enum.Parse(property.PropertyType, candidate, false), null);
        }
        catch { }
    }

    private static string EscapeJson(string value)
    {
        return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static void EnsureFields()
    {
        if (s_configType != null && (s_modeField != null || Time.unscaledTime < s_nextLookup)) return;
        s_nextLookup = Time.unscaledTime + 2f;
        s_configType = Type.GetType("HsMod.PluginConfig, HsMod");
        s_modeField = s_configType?.GetField("wildLadderPlayMode", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        s_runtimeField = s_configType?.GetField("scriptRuntimeEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }
}

/// <summary>
/// Supplies the same five-step F11 mode cycle when the installed HsMod build
/// still has the original four-value enum. It only changes the selected mode;
/// the F12 runtime switch remains owned by HsMod.
/// </summary>
internal sealed class HsModNativeArenaModeBridge : MonoBehaviour
{
    private float _nextSync;
    private int _lastShortcutFrame = -1;

    private void Awake()
    {
        HsModNativeArena.SyncModeBridge();
    }

    private void LateUpdate()
    {
        if (Time.unscaledTime >= _nextSync)
        {
            _nextSync = Time.unscaledTime + 0.5f;
            HsModNativeArena.SyncModeBridge();
        }
        // 操作节奏由 HsMod 配置决定（打牌 HumanLike、酒馆 Fastest），插件不覆盖。
        // Patched HsMod owns F11 when its enum contains Arena. The bridge only
        // supplies the old four-value compatibility cycle; handling F11 in
        // both places would advance two modes for one key press.
        if (!HsModNativeArena.HasNativeArenaEnum() && Input.GetKeyDown(KeyCode.F11) && _lastShortcutFrame != Time.frameCount)
        {
            _lastShortcutFrame = Time.frameCount;
            HsModNativeArena.HandleModeShortcut();
        }
    }
}

/// <summary>
/// HsMod still owns the F12/F11 shortcuts and the settings page.  When its
/// mode is Arena, short-circuit only the constructed-play Tick method; this
/// leaves the native mode selector and runtime toggle intact while allowing
/// ArenaController to run the draft/game loop.
/// </summary>
internal static partial class HsModNativeArenaPatch
{
    private static Harmony s_harmony;
    private static float s_nextAttempt;
    private static float s_nextRecommendationWarning;
    private static float s_missingRecommendationBlockedSince;
    private static float s_liveRecommendationBlockedSince;
    private static string s_liveRecommendationRequestId;
    private static int s_liveRecommendationOptionsId = -1;
    private static string s_liveRecommendationReleasedRequestId;
    private static int s_liveRecommendationReleasedOptionsId = -1;
    private static readonly ConstructedTimeoutRefreshBudget s_constructedRefreshBudget = new ConstructedTimeoutRefreshBudget();
    private static float s_nextConstructedTurnStateWarning;
    private static bool s_responseDiagnosticsLogged;
    private static readonly HashSet<string> s_responseDiagnosticReasons = new HashSet<string>(StringComparer.Ordinal);
    private static MethodInfo s_responseStallMethod;
    private static Type s_botType;
    private static MethodInfo s_handleGameMethod;
    private static MethodInfo s_endTurnFallbackMethod;
    private static MethodInfo s_boxStrategyMethod;
    private static readonly MissingRecommendationResponseGate s_missingResponseGate = new MissingRecommendationResponseGate();

    internal static void EnsureInstalled()
    {
        NativeOptionExecutionDiagnostics.EnsureInstalled();
        NativeOpponentTimerGuard.EnsureInstalled();
        NativeOptionEntityFallback.EnsureInstalled();
        NativeTargetlessAbilityGuard.EnsureInstalled();
        if (s_harmony != null || Time.unscaledTime < s_nextAttempt) return;
        s_nextAttempt = Time.unscaledTime + 5f;
        try
        {
            var type = Type.GetType("HsMod.WildLadderBot, HsMod");
            var target = type?.GetMethod("Tick", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var fallback = type?.GetMethod("ContinueEndTurnFallback", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var responseStall = type?.GetMethod("TryRecoverFromResponseStall", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var handleGame = type?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "HandleGame" && m.GetParameters().Length == 1);
            var rankControl = type?.GetMethod("TryConcedeForRankControl", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var lowWinRate = type?.GetMethod("TryConcedeForLowWinRate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var prefix = typeof(HsModNativeArenaPatch).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
            var fallbackPrefix = typeof(HsModNativeArenaPatch).GetMethod(nameof(EndTurnFallbackPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            var handleGameTranspiler = typeof(HsModNativeArenaPatch).GetMethod(nameof(HandleGameTranspiler), BindingFlags.Static | BindingFlags.NonPublic);
            var concedePrefix = typeof(HsModNativeArenaPatch).GetMethod(nameof(ConcedeGuardPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            var responseStallPrefix = typeof(HsModNativeArenaPatch).GetMethod(nameof(ResponseStallRecoveryPrefix), BindingFlags.Static | BindingFlags.NonPublic);
            if (type == null || handleGame == null || prefix == null) return;
            var harmony = new Harmony("local.hsmod.arenaboxbot.native-arena");
            // Publish the executor references before optional compatibility
            // patches.  A transpiler mismatch in a newer HsMod build must not
            // discard the working Arena gameplay bridge or trigger an endless
            // retry loop that leaves s_handleGameMethod null.
            s_botType = type;
            s_handleGameMethod = handleGame;
            s_responseStallMethod = responseStall;
            s_endTurnFallbackMethod = fallback;
            s_boxStrategyMethod = type.GetMethod("IsBoxAiRecommendStrategy", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            s_harmony = harmony;
            if (target != null)
            {
                try { harmony.Patch(target, prefix: new HarmonyMethod(prefix)); }
                catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod Arena Tick patch skipped: " + ex.GetType().Name + ": " + ex.Message); }
            }
            if (fallback != null && fallbackPrefix != null)
            {
                try { harmony.Patch(fallback, prefix: new HarmonyMethod(fallbackPrefix)); }
                catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod Arena timeout fallback patch skipped: " + ex.GetType().Name + ": " + ex.Message); }
            }
            if (responseStall != null && responseStallPrefix != null)
            {
                try
                {
                    harmony.Patch(responseStall, prefix: new HarmonyMethod(responseStallPrefix));
                    ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod response stall recovery patch installed: " + responseStall);
                }
                catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod response stall recovery patch skipped: " + ex.GetType().Name + ": " + ex.Message); }
            }
            if (handleGameTranspiler != null)
            {
                try { harmony.Patch(handleGame, transpiler: new HarmonyMethod(handleGameTranspiler)); }
                catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod Arena action timeout patch skipped: " + ex.GetType().Name + ": " + ex.Message); }
            }
            if (concedePrefix != null)
            {
                if (rankControl != null)
                {
                    try { harmony.Patch(rankControl, prefix: new HarmonyMethod(concedePrefix)); }
                    catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod Arena rank concede guard skipped: " + ex.GetType().Name + ": " + ex.Message); }
                }
                if (lowWinRate != null)
                {
                    try { harmony.Patch(lowWinRate, prefix: new HarmonyMethod(concedePrefix)); }
                    catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod Arena win-rate concede guard skipped: " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod native Arena bridge installed; Arena gameplay executor is available");
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod native Arena bridge deferred: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool Prefix()
    {
        // Keep HsMod's normal executor alive inside an actual Arena game:
        // HandleGame() is the shared Box recommendation/action path used by
        // standard and Wild matches as well.  Outside gameplay, suppress its
        // constructed deck/navigation loop so ArenaController owns draft,
        // rewards, and matchmaking pages.  This was previously an
        // unconditional false return, which silently skipped every Arena
        // card action and caused turns to be passed without a play.
        if (!HsModNativeArena.IsNativeArenaModeSelected()) return true;
        try
        {
            var scene = SceneMgr.Get();
            if (scene == null || scene.IsTransitioning()) return false;
            if (scene.GetMode() != SceneMgr.Mode.GAMEPLAY) return false;
            var game = GameMgr.Get();
            if (game == null || (!game.IsArena() && !game.IsUndergroundArena())) return false;
            // Do not let HsMod's constructed-play Tick run in Arena. It owns
            // ladder stop/rank logic and can also submit a normal deck queue.
            // Re-enter only the shared gameplay executor after the Arena
            // controller has confirmed that automatic play and the Box bridge
            // are both ready.
            if (!ArenaController.IsArenaGameplayExecutorEnabled) return false;
            var state = GameState.Get();
            if (state == null || s_handleGameMethod == null) return false;
            s_handleGameMethod.Invoke(null, new object[] { state });
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// HsMod's normal timeout fallback ends the turn when the Box bridge has
    /// not supplied a recommendation for roughly eight seconds.  That is a
    /// safe fallback for a constructed game, but it makes Arena silently pass
    /// turns when the Box process is attached yet not publishing actions.
    /// Suppress only this fallback, retaining recommendation polling, valid
    /// actions (including a recommended end turn), mulligan and game results.
    /// </summary>
    private static bool IsArenaGameplay()
    {
        if (!HsModNativeArena.IsNativeArenaModeSelected()) return false;
        try
        {
            var scene = SceneMgr.Get();
            var game = GameMgr.Get();
            return scene != null && scene.GetMode() == SceneMgr.Mode.GAMEPLAY && game != null &&
                   (game.IsArena() || game.IsUndergroundArena());
        }
        catch { return false; }
    }

    private static bool EndTurnFallbackPrefix()
    {
        if (!HsModNativeArena.IsNativeArenaModeSelected()) return true;
        if (!IsArenaGameplay()) return true;
        try
        {
            // Release the fallback latch so the next tick polls and maps new
            // Box data instead of immediately re-entering the old fallback.
            s_botType?.GetField("s_pendingEndTurnOptionsId", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, -1);
            foreach (var name in new[] { "s_boxWaitOptionsId", "s_boxPageRefreshRequestedOptionsId", "s_boxThrottleExtendedPacketId", "s_boxLiveRecPacketId" })
                s_botType?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, -1);
            foreach (var name in new[] { "s_boxWaitStartedTime", "s_boxPageRefreshRequestedTime", "s_boxLiveRecFirstSeenTime" })
                s_botType?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, 0f);
            s_botType?.GetField("s_boxTimeoutStreak", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, 0);
            s_botType?.GetMethod("SetStatusText", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { "等待盒子推荐（竞技场超时保护）" });
            s_botType?.GetMethod("Delay", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { 0.25f });
            if (Time.unscaledTime >= s_nextRecommendationWarning)
            {
                s_nextRecommendationWarning = Time.unscaledTime + 5f;
                ArenaBoxBotPlugin.LogSource?.LogWarning("Arena Box recommendation timed out; suppressed fallback end-turn and resumed recommendation polling");
            }
            return false;
        }
        catch
        {
            // The scene/game check above already proved this is an Arena
            // gameplay screen. If a private HsMod field changes, waiting is
            // still safer than allowing the original timeout to end the turn.
            return false;
        }
    }

    /// <summary>
    /// HsMod calls ContinueEndTurnFallback directly from HandleGame after its
    /// eight-second recommendation timeout.  The method-level Harmony prefix
    /// is not reliable on every build because this call can be inlined or
    /// reached through a different generated body.  Replace the call itself:
    /// Arena waits and polls again; constructed/Wild keeps the native fallback.
    /// </summary>
    private static void EndTurnFallbackGuard(object state, object packet)
    {
        if (!IsArenaGameplay())
        {
            // Constructed/Wild occasionally receives a real Box action whose
            // source entity is briefly absent from the current client option
            // list. HsMod's native path ends the turn after eight seconds in
            // that case, even though the Box can publish a valid replacement
            // a moment later. Clear the stale request once and poll again;
            // preserve HsMod's safe end-turn fallback if the same request
            // still fails on the next timeout.
            if (TryRecoverConstructedTimeout(state)) return;
            try { s_endTurnFallbackMethod?.Invoke(null, new[] { state, packet }); }
            catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod native timeout fallback invoke failed: " + ex.GetType().Name + ": " + ex.Message); }
            // Do not reset the budget here: the native void method returns
            // both after hover preparation and after submission. Its pending
            // latch must survive the next HandleGame tick.
            return;
        }

        try
        {
            s_botType?.GetField("s_pendingEndTurnOptionsId", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, -1);
            foreach (var name in new[] { "s_boxWaitOptionsId", "s_boxPageRefreshRequestedOptionsId", "s_boxThrottleExtendedPacketId", "s_boxLiveRecPacketId" })
                s_botType?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, -1);
            foreach (var name in new[] { "s_boxWaitStartedTime", "s_boxPageRefreshRequestedTime", "s_boxLiveRecFirstSeenTime" })
                s_botType?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, 0f);
            s_botType?.GetField("s_boxTimeoutStreak", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, 0);
            s_botType?.GetMethod("SetStatusText", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { "等待盒子推荐（竞技场超时保护）" });
            s_botType?.GetMethod("Delay", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { 0.25f });
            if (Time.unscaledTime >= s_nextRecommendationWarning)
            {
                s_nextRecommendationWarning = Time.unscaledTime + 5f;
                ArenaBoxBotPlugin.LogSource?.LogWarning("Arena Box recommendation timeout fallback suppressed; recommendation polling resumed");
            }
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena timeout guard state reset failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool TryRecoverConstructedTimeout(object state)
    {
        if (!IsConstructedGameplay()) return false;
        try
        {
            var gameState = state as GameState;
            var options = gameState?.GetOptionsPacket();
            var friendly = gameState?.GetFriendlySidePlayer();
            var opposing = gameState?.GetOpposingSidePlayer();
            var gameHandle = GameMgr.Get()?.GetGameHandle().ToString();
            var authoritativeTurn = gameState != null && gameState.IsGameCreated() &&
                !gameState.IsGameOverNowOrPending() && !gameState.WasConcedeRequested() &&
                !gameState.IsMulliganManagerActive() && friendly != null && opposing != null &&
                friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0 &&
                !string.IsNullOrEmpty(gameHandle) ? gameHandle + ":" + gameState.GetTurn() : null;
            // No additional ReadCurrent: it is stateful and unnecessary for
            // deciding whether this own turn already consumed its refresh.
            var decision = s_constructedRefreshBudget.Decide(authoritativeTurn);
            if (decision == ConstructedTimeoutDecision.HoldForTurnState)
            {
                // Captured 07:56/07:57: raw Options advanced through two own
                // turns while the processed client still reported turn 6.
                // Preserve the pending native hover latch and refresh budget;
                // unknown ownership must not authorize an end-turn submission.
                if (Time.unscaledTime >= s_nextConstructedTurnStateWarning)
                {
                    s_nextConstructedTurnStateWarning = Time.unscaledTime + 5f;
                    ArenaBoxBotPlugin.LogSource?.LogWarning($"BoxAI构筑超时等待回合状态：未确认本机己方回合，暂停原生超时结束回合；turn={gameState?.GetTurn()}, optionsId={options?.ID}, friendlyCurrent={friendly?.GetTag(GAME_TAG.CURRENT_PLAYER)}, opposingCurrent={opposing?.GetTag(GAME_TAG.CURRENT_PLAYER)}；待提交状态与刷新预算保留");
                }
                s_botType?.GetMethod("Delay", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { 0.25f });
                return true;
            }
            if (decision == ConstructedTimeoutDecision.NativeFallback) return false;
            var currentOptionsId = options?.ID ?? -1;
            s_botType?.GetField("s_pendingEndTurnOptionsId", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, -1);
            foreach (var name in new[] { "s_boxWaitOptionsId", "s_boxPageRefreshRequestedOptionsId", "s_boxThrottleExtendedPacketId", "s_boxLiveRecPacketId" })
                s_botType?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, -1);
            foreach (var name in new[] { "s_boxWaitStartedTime", "s_boxPageRefreshRequestedTime", "s_boxLiveRecFirstSeenTime" })
                s_botType?.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, 0f);
            s_botType?.GetField("s_boxTimeoutStreak", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, 0);
            s_botType?.GetMethod("SetStatusText", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { "盒子推荐短暂失配，正在刷新" });
            s_botType?.GetMethod("Delay", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, new object[] { 0.25f });
            ArenaBoxBotPlugin.LogSource?.LogWarning($"BoxAI构筑/狂野超时恢复：本回合仅刷新一次（turn={authoritativeTurn}, optionsId={currentOptionsId}；后续保留原生结束回合准备/提交阶段，不再清除其待提交状态）");
            return true;
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= s_nextConstructedTurnStateWarning)
            {
                s_nextConstructedTurnStateWarning = Time.unscaledTime + 5f;
                ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI构筑超时回合状态读取失败，暂停超时结束回合，等待下次状态核验：" + ex.GetType().Name + ": " + ex.Message);
            }
            return true;
        }
    }

    /// <summary>
    /// HsMod refuses to enter its normal Box polling loop while the client
    /// reports a blocked response packet.  When the Box has not published a
    /// recommendation yet, its original recovery method returns false
    /// forever, so the next action can wait until the rope.  After a bounded
    /// two-second observation window, let HandleGame continue; this reaches
    /// the existing recommendation timeout/refresh path and records the
    /// exact missing-recommendation condition instead of hanging in the
    /// response gate. A captured, unexecuted ordinary play/attack recommendation
    /// gets the shorter two-second response budget used by the repaired gate
    /// fast response mode; choice, replace, end_turn, and already-executed
    /// recommendations retain HsMod's original acknowledgement path.
    /// </summary>
    private static bool ResponseStallRecoveryPrefix(ref bool __result, object state)
    {
        try
        {
            if (!s_responseDiagnosticsLogged)
            {
                s_responseDiagnosticsLogged = true;
                ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod response stall prefix entered; constructed gameplay diagnostics enabled");
            }
            if (!IsConstructedGameplay())
            {
                LogResponseDiagnostic("constructed=false");
                s_missingRecommendationBlockedSince = 0f;
                s_liveRecommendationBlockedSince = 0f;
                s_liveRecommendationRequestId = null;
                s_liveRecommendationOptionsId = -1;
                s_liveRecommendationReleasedRequestId = null;
                s_liveRecommendationReleasedOptionsId = -1;
                return true;
            }
            var stateType = state?.GetType();
            var options = stateType?.GetMethod("GetOptionsPacket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(state, null);
            if (options == null)
            {
                LogResponseDiagnostic("options=null state=" + (stateType?.FullName ?? "null"));
                s_missingRecommendationBlockedSince = 0f;
                s_liveRecommendationBlockedSince = 0f;
                s_liveRecommendationRequestId = null;
                s_liveRecommendationOptionsId = -1;
                s_liveRecommendationReleasedRequestId = null;
                s_liveRecommendationReleasedOptionsId = -1;
                return true;
            }
            var adapter = Type.GetType("HsMod.BoxAiShadowAdapter, HsMod");
            var readCurrent = adapter?.GetMethod("ReadCurrent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            LogResponseDiagnostic("reflection adapter=" + (adapter?.FullName ?? "null") + ";readCurrent=" + (readCurrent != null));
            var recommendation = readCurrent?.Invoke(null, new[] { state });
            if (recommendation != null)
            {
                // HsMod uses a response gate for every non-choice action when
                // the client reports a blocked response packet.
                // The Box recommendation is already captured and the action
                // ledger is the duplicate-submission guard; keeping that gate
                // for a live play/attack recommendation burns most of the
                // player's turn and is the source of the observed rope stalls.
                // Keep HsMod's original path for already-submitted actions and
                // for choice/replace/end_turn, whose protocol acknowledgement
                // must still be allowed to settle normally.
                var action = RecommendationMemberAccess.ReadAction(recommendation);
                var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
                var isChoiceAction = ResponseActionPolicy.IsChoiceLike(normalizedAction) || normalizedAction == "end_turn";
            LogActionSeen(normalizedAction, isChoiceAction);
                var alreadyExecutedMethod = adapter?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "AlreadyExecuted" && m.GetParameters().Length == 1);
                var alreadyExecuted = alreadyExecutedMethod != null &&
                    alreadyExecutedMethod.Invoke(null, new[] { recommendation }) is bool executed && executed;
                LogResponseDiagnostic("recommendation action=" + normalizedAction + ";already=" + alreadyExecuted);
                if (alreadyExecuted || isChoiceAction)
                {
                    s_missingRecommendationBlockedSince = 0f;
                    s_liveRecommendationBlockedSince = 0f;
                    s_liveRecommendationRequestId = null;
                    s_liveRecommendationOptionsId = -1;
                    s_liveRecommendationReleasedRequestId = null;
                    s_liveRecommendationReleasedOptionsId = -1;
                    return true;
                }
                if (string.IsNullOrWhiteSpace(normalizedAction))
                {
                    // Empty Action is the transient ordinary recommendation
                    // state handled by the direct gate below. Keep its request
                    // identity intact so a second HandleGame pass does not
                    // restart the bounded recovery budget.
                    return true;
                }
                s_missingRecommendationBlockedSince = 0f;
                // A captured, unexecuted play/attack recommendation is safe to
                // release after the same two-second budget used by the repaired
                // gate. This is deliberately narrower than
                // bypassing the gate entirely and does not touch choice flows.
                var recommendationOptionId = options.GetType().GetProperty("ID")?.GetValue(options, null) ?? -1;
                var requestId = RecommendationMemberAccess.ReadText(recommendation, "RequestId");
                if (!string.Equals(s_liveRecommendationRequestId, requestId, StringComparison.Ordinal) ||
                    s_liveRecommendationOptionsId != Convert.ToInt32(recommendationOptionId))
                {
                    s_liveRecommendationRequestId = requestId;
                    s_liveRecommendationOptionsId = Convert.ToInt32(recommendationOptionId);
                    s_liveRecommendationBlockedSince = Time.realtimeSinceStartup;
                }
                var recommendationElapsed = Time.realtimeSinceStartup - s_liveRecommendationBlockedSince;
                if (recommendationElapsed < 0.2f)
                {
                    __result = false;
                    return false;
                }
                ArenaBoxBotPlugin.LogSource?.LogWarning($"BoxAI响应门控提前放行：已收到未执行的 {normalizedAction} 推荐，等待 {recommendationElapsed:0.0} 秒，optionsId={recommendationOptionId}");
                s_liveRecommendationBlockedSince = 0f;
                s_liveRecommendationRequestId = null;
                s_liveRecommendationOptionsId = -1;
                s_liveRecommendationReleasedRequestId = null;
                s_liveRecommendationReleasedOptionsId = -1;
                __result = true;
                return false;
            }
            LogResponseDiagnostic("recommendation=null");
            var now = Time.realtimeSinceStartup;
            if (s_missingRecommendationBlockedSince <= 0f)
                s_missingRecommendationBlockedSince = now;
            var elapsed = now - s_missingRecommendationBlockedSince;
            if (elapsed < 0.2f)
            {
                __result = false;
                return false;
            }
            var optionId = options.GetType().GetProperty("ID")?.GetValue(options, null) ?? -1;
            ArenaBoxBotPlugin.LogSource?.LogWarning($"BoxAI响应阻塞但无新建议，已等待 {elapsed:0.0} 秒；放行推荐轮询，optionsId={optionId}");
            s_missingRecommendationBlockedSince = 0f;
            __result = true;
            return false;
        }
        catch (Exception ex)
        {
            // Preserve HsMod's original behavior if a private API changes.
            LogResponseDiagnostic("exception=" + ex.GetType().Name + ":" + ex.Message);
            s_missingRecommendationBlockedSince = 0f;
            s_liveRecommendationBlockedSince = 0f;
            s_liveRecommendationRequestId = null;
            s_liveRecommendationOptionsId = -1;
            s_liveRecommendationReleasedRequestId = null;
            s_liveRecommendationReleasedOptionsId = -1;
            return true;
        }
    }

    private static readonly HashSet<string> s_actionsSeen = new(StringComparer.Ordinal);

    /// <summary>Records every recommendation action token once, so arena tests
    /// can verify keywords such as Assemble ("拼装") are classified correctly.</summary>
    private static void LogActionSeen(string action, bool choiceLike)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        if (s_actionsSeen.Add(action))
            ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod response action seen: " + action + ";choiceLike=" + choiceLike);
    }
    private static void LogResponseDiagnostic(string reason)
    {
        if (s_responseDiagnosticReasons.Add(reason))
            ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod response stall diagnostic: " + reason);
    }

    private static bool IsConstructedGameplay()
    {
        try
        {
            var scene = SceneMgr.Get();
            var game = GameMgr.Get();
            return scene != null && scene.GetMode() == SceneMgr.Mode.GAMEPLAY && game != null &&
                   !game.IsArena() && !game.IsUndergroundArena();
        }
        catch { return false; }
    }

    /// <summary>
    /// Rank protection and low-win-rate concede are ladder-only policies.
    /// They are independent HsMod settings and must never concede an Arena
    /// game while the shared gameplay executor is running.
    /// </summary>
    private static bool ConcedeGuardPrefix(ref bool __result)
    {
        if (!HsModNativeArena.IsNativeArenaModeSelected()) return true;
        if (IsArenaGameplay())
        {
            __result = false;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The 1004 HsMod build has a second timeout fallback inside HandleGame:
    /// when the current entity-choice packet has no Box recommendation for
    /// roughly eight seconds it submits the first legal candidate.  That
    /// fallback is useful for constructed play but is unsafe for Arena draft
    /// and discover choices, where a missing recommendation must leave the
    /// current page/action intact.  Keep the original eight-second threshold
    /// outside Arena and make it effectively unbounded while the live game is
    /// an Arena match.  The call is dynamic so switching modes does not alter
    /// HsMod's ordinary behavior.
    /// </summary>
    private static float ChoiceFallbackTimeout()
    {
        return IsArenaGameplay() ? float.PositiveInfinity : 8f;
    }

    private static float MulliganFallbackTimeout()
    {
        // HsMod otherwise keeps all cards after eight seconds. In Arena that
        // silently discards a missing Box mulligan recommendation.
        return IsArenaGameplay() ? float.PositiveInfinity : 8f;
    }

    private static float ChoiceNullFallbackTimeout()
    {
        // A stale/no-package choice must not be promoted to the ordinary
        // options path in Arena, where it can eventually submit an end-turn.
        return IsArenaGameplay() ? float.PositiveInfinity : 8f;
    }

    private static float ActionFallbackTimeout8()
    {
        return IsArenaGameplay() ? float.PositiveInfinity : 8f;
    }

    private static float ActionFallbackTimeout12()
    {
        return IsArenaGameplay() ? float.PositiveInfinity : 12f;
    }

    private static float ActionFallbackTimeout4()
    {
        return IsArenaGameplay() ? float.PositiveInfinity : 4f;
    }

    private static bool IsFloatConstant(CodeInstruction instruction, float value)
    {
        return instruction != null && instruction.opcode == OpCodes.Ldc_R4 &&
               instruction.operand is float actual && Math.Abs(actual - value) < 0.001f;
    }

    private static bool IsLocalStore(CodeInstruction instruction, int expectedIndex, string expectedName)
    {
        if (instruction == null ||
            (instruction.opcode != OpCodes.Stloc && instruction.opcode != OpCodes.Stloc_S &&
             instruction.opcode != OpCodes.Stloc_0 && instruction.opcode != OpCodes.Stloc_1 &&
             instruction.opcode != OpCodes.Stloc_2 && instruction.opcode != OpCodes.Stloc_3)) return false;
        var operand = instruction.operand;
        if (operand == null) return expectedIndex < 0;
        try
        {
            var property = operand.GetType().GetProperty("LocalIndex", BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(operand, null) is int index && index == expectedIndex) return true;
        }
        catch { }
        return operand.ToString()?.IndexOf(expectedName, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static CodeInstruction ReplaceWithCall(CodeInstruction original, MethodInfo method)
    {
        // Harmony uses labels and exception blocks on the original constant
        // instructions. Dropping them while swapping ldc.r4 for a dynamic
        // timeout call produces the opaque "IL Compile Error" seen on the
        // 1004 HsMod build.
        var replacement = new CodeInstruction(OpCodes.Call, method);
        if (original != null)
        {
            replacement.labels.AddRange(original.labels);
            replacement.blocks.AddRange(original.blocks);
        }
        return replacement;
    }

    private static IEnumerable<CodeInstruction> HandleGameTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var responseGate = typeof(HsModNativeArenaPatch).GetMethod(nameof(ResponseStallGate), BindingFlags.Static | BindingFlags.NonPublic);
        var fallbackGuard = typeof(HsModNativeArenaPatch).GetMethod(nameof(EndTurnFallbackGuard), BindingFlags.Static | BindingFlags.NonPublic);
        var mulliganMethod = typeof(HsModNativeArenaPatch).GetMethod(nameof(MulliganFallbackTimeout), BindingFlags.Static | BindingFlags.NonPublic);
        var choiceMethod = typeof(HsModNativeArenaPatch).GetMethod(nameof(ChoiceFallbackTimeout), BindingFlags.Static | BindingFlags.NonPublic);
        var nullChoiceMethod = typeof(HsModNativeArenaPatch).GetMethod(nameof(ChoiceNullFallbackTimeout), BindingFlags.Static | BindingFlags.NonPublic);
        var action8Method = typeof(HsModNativeArenaPatch).GetMethod(nameof(ActionFallbackTimeout8), BindingFlags.Static | BindingFlags.NonPublic);
        var action12Method = typeof(HsModNativeArenaPatch).GetMethod(nameof(ActionFallbackTimeout12), BindingFlags.Static | BindingFlags.NonPublic);
        var action4Method = typeof(HsModNativeArenaPatch).GetMethod(nameof(ActionFallbackTimeout4), BindingFlags.Static | BindingFlags.NonPublic);
        if (mulliganMethod == null || choiceMethod == null || nullChoiceMethod == null ||
            action8Method == null || action12Method == null || action4Method == null ||
            responseGate == null || fallbackGuard == null) return code;

        // HsMod calls its private response recovery helper from inside
        // HandleGame.  On some builds Harmony can install the prefix but the
        // JIT still reaches the original call path. Replace that call directly
        // so the constructed gameplay path uses the same guarded decision on
        // every invocation.
        var responseGatePatched = false;
        foreach (var instruction in code)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo called &&
                called.Name == "TryRecoverFromResponseStall" &&
                called.ReturnType == typeof(bool) &&
                called.GetParameters().Length == 1)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = responseGate;
                responseGatePatched = true;
            }
        }
        ArenaBoxBotPlugin.LogSource?.LogInfo("patched HsMod response stall call: " + responseGatePatched);

        var fallbackGuardPatched = false;
        foreach (var instruction in code)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodInfo called &&
                called.Name == "ContinueEndTurnFallback" &&
                called.ReturnType == typeof(void) &&
                called.GetParameters().Length == 2)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = fallbackGuard;
                fallbackGuardPatched = true;
            }
        }
        ArenaBoxBotPlugin.LogSource?.LogInfo("patched HsMod direct timeout fallback: " + fallbackGuardPatched);

        var mulliganPatched = false;
        var choicePatched = false;
        var nullChoicePatched = false;
        var actionPatched = false;

        // Match the 1004 build's explicit mulligan and first-candidate
        // fallbacks by their diagnostic strings. This avoids changing the
        // unrelated eight-second animation/network waits.
        for (var i = 0; i < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Ldstr || !(code[i].operand is string text)) continue;
            MethodInfo method = null;
            if (text.IndexOf("留牌建议等待超过", StringComparison.Ordinal) >= 0) method = mulliganMethod;
            else if (text.IndexOf("改选第一个当前合法候选", StringComparison.Ordinal) >= 0) method = choiceMethod;
            else if (text.IndexOf("选择残留穿透", StringComparison.Ordinal) >= 0) method = nullChoiceMethod;
            if (method == null) continue;
            for (var j = i - 1; j >= 0 && i - j <= 40; j--)
            {
                if (!IsFloatConstant(code[j], 8f)) continue;
                code[j] = ReplaceWithCall(code[j], method);
                if (ReferenceEquals(method, mulliganMethod)) mulliganPatched = true;
                else if (ReferenceEquals(method, choiceMethod)) choicePatched = true;
                else nullChoicePatched = true;
                break;
            }
        }

        // Older natives stored this ternary in different compiler-local slots
        // (1004: V_25, 1022/1030: V_29). Bind the unique 8/12/4 branch
        // expression, never a compiler-local slot number or an unrelated
        // animation/network timeout.
        var timeoutStores = Enumerable.Range(5, Math.Max(0, code.Count - 5))
            .Where(i => NativeTimeoutPattern.IsActionTimeoutStore(code, i)).ToArray();
        if (timeoutStores.Length == 1)
        {
            var i = timeoutStores[0];
            code[i - 5] = ReplaceWithCall(code[i - 5], action8Method);
            code[i - 3] = ReplaceWithCall(code[i - 3], action12Method);
            code[i - 1] = ReplaceWithCall(code[i - 1], action4Method);
            actionPatched = true;
        }

        ArenaBoxBotPlugin.LogSource?.LogInfo("patched HsMod Arena timeout guards: mulligan=" + mulliganPatched +
            ", choice=" + choicePatched + ", no-package=" + nullChoicePatched + ", action=" + actionPatched +
            ", direct-fallback=" + fallbackGuardPatched);
        if (!mulliganPatched || !choicePatched || !nullChoicePatched || !actionPatched)
            ArenaBoxBotPlugin.LogSource?.LogWarning("HsMod Arena timeout guard pattern incomplete; missing recommendations will remain blocked where a guard was found");
        return code;
    }

    /// <summary>
    /// Direct replacement for HsMod's response gate. The original helper is
    /// private and has appeared inlined/indirect in different HsMod builds,
    /// so replacing the HandleGame call is more reliable than only prefixing
    /// the helper. Choices, mulligan replacement, and submitted/unknown end
    /// turns retain the native acknowledgement gate. A captured,
    /// unexecuted ordinary play or attack follows the repaired two-second
    /// recovery budget, including a transient empty Action recommendation.
    /// </summary>
    private static bool ResponseStallGate(GameState state)
    {
        var released = ResponseStallGateCore(state, out var recommendation, out var reason);
        // The executor observer cannot see waits that return before the
        // execution method is entered. Reuse this gate's existing read;
        // diagnostics must not invoke the stateful native reader again.
        NativeOptionExecutionDiagnostics.ObserveResponseGate(state, recommendation, released, reason);
        return released;
    }

    private static bool ResponseStallGateCore(GameState state, out object recommendation, out string reason)
    {
        recommendation = null;
        reason = "inactive-or-no-options";
        try
        {
            if (state == null) { s_missingResponseGate.Reset(); return false; }
            var stateType = state.GetType();
            var modeMethod = stateType.GetMethod("GetResponseMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var responseMode = modeMethod != null ? Convert.ToInt32(modeMethod.Invoke(state, null)) : -1;
            if (responseMode == 4) { s_missingResponseGate.Reset(); return false; }
            var options = stateType.GetMethod("GetOptionsPacket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(state, null);
            if (options == null) { s_missingResponseGate.Reset(); return false; }
            var adapter = Type.GetType("HsMod.BoxAiShadowAdapter, HsMod") ?? s_botType?.Assembly?.GetType("HsMod.BoxAiShadowAdapter");
            var readCurrent = adapter?.GetMethod("ReadCurrent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            recommendation = readCurrent?.Invoke(null, new[] { state });
            if (recommendation == null)
            {
                reason = "recommendation-unavailable";
                LogResponseDiagnostic("gate recommendation=null mode=" + responseMode);
                return PollAfterMissingRecommendationWait(state, Convert.ToInt32(options.GetType().GetProperty("ID")?.GetValue(options, null) ?? -1));
            }
            s_missingResponseGate.Reset();
            var recommendationType = recommendation.GetType();
            var action = RecommendationMemberAccess.ReadAction(recommendation);
            var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            var isChoiceAction = ResponseActionPolicy.IsChoiceLike(normalizedAction);
            LogActionSeen(normalizedAction, isChoiceAction);
            var alreadyExecutedMethod = adapter?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "AlreadyExecuted" && m.GetParameters().Length == 1);
            var alreadyExecuted = alreadyExecutedMethod != null &&
                alreadyExecutedMethod.Invoke(null, new[] { recommendation }) is bool executed && executed;
            LogResponseDiagnostic("gate action=" + normalizedAction + ";mode=" + responseMode + ";already=" + alreadyExecuted);
            // Match HsMod's recovery semantics without sacrificing most of the
            // player's turn. An existing recommendation with an empty Action
            // is a transient response state, not a reason to hold the whole
            // eight-second native gate. Ordinary play/attack recommendations
            // are polled again after two seconds; the duplicate ledger still
            // prevents resubmission. Choice/replace and end turns with any
            // current-packet ledger entry retain acknowledgement protection.
            // Choice/replace and duplicate submissions retain the
            // acknowledgement gate because releasing those early can submit
            // an invalid or repeated protocol action.
            if (alreadyExecuted || isChoiceAction)
            {
                reason = alreadyExecuted ? "submitted-action-acknowledgement" : "choice-replace-end-turn-acknowledgement";
                s_liveRecommendationReleasedRequestId = null;
                s_liveRecommendationReleasedOptionsId = -1;
                return false;
            }

            // A fresh end-turn request with no ledger entry is not an
            // acknowledgement wait. Native HandleGame otherwise returns at
            // IsResponsePacketBlocked before its executor can see it. Keep
            // submitted/unknown requests and all choice/replace guards held.
            if (normalizedAction == "end_turn" &&
                !CanPollUnsubmittedEndTurn(state, recommendation, adapter, responseMode, out var endTurnHold))
            {
                reason = "end-turn-held:" + endTurnHold;
                s_liveRecommendationReleasedRequestId = null;
                s_liveRecommendationReleasedOptionsId = -1;
                return false;
            }

            var optionId = options.GetType().GetProperty("ID")?.GetValue(options, null) ?? -1;
            var requestId = RecommendationMemberAccess.ReadText(recommendation, "RequestId");
            var optionIdValue = Convert.ToInt32(optionId);
            if (string.Equals(s_liveRecommendationReleasedRequestId, requestId, StringComparison.Ordinal) &&
                s_liveRecommendationReleasedOptionsId == optionIdValue)
            {
                // The same captured request can pass through HandleGame more
                // than once. Once its bounded recovery budget has released it,
                // do not start the budget again on the next pass.
                return true;
            }
            if (!string.Equals(s_liveRecommendationRequestId, requestId, StringComparison.Ordinal) ||
                s_liveRecommendationOptionsId != optionIdValue)
            {
                s_liveRecommendationRequestId = requestId;
                s_liveRecommendationOptionsId = optionIdValue;
                s_liveRecommendationBlockedSince = Time.realtimeSinceStartup;
                s_liveRecommendationReleasedRequestId = null;
                s_liveRecommendationReleasedOptionsId = -1;
            }
            var elapsed = Time.realtimeSinceStartup - s_liveRecommendationBlockedSince;
            reason = normalizedAction == "end_turn" ? "unsubmitted-end-turn-recovery-budget" : "ordinary-action-recovery-budget";
            const float ordinaryActionBudgetSeconds = 0.2f;
            var budget = ordinaryActionBudgetSeconds;
            if (elapsed < budget) return false;
            var actionLabel = string.IsNullOrWhiteSpace(normalizedAction) ? "<empty>" : normalizedAction;
            ArenaBoxBotPlugin.LogSource?.LogWarning($"BoxAI响应门控恢复：已收到未执行的 {actionLabel} 推荐，等待 {elapsed:0.0} 秒（预算 {budget:0.0} 秒），optionsId={optionId}");
            s_liveRecommendationReleasedRequestId = requestId;
            s_liveRecommendationReleasedOptionsId = optionIdValue;
            s_liveRecommendationBlockedSince = 0f;
            return true;
        }
        catch (Exception ex)
        {
            reason = "gate-exception:" + ex.GetType().Name;
            s_missingResponseGate.Reset();
            LogResponseDiagnostic("gate exception=" + ex.GetType().Name + ":" + ex.Message);
            return false;
        }
    }

    private static bool PollAfterMissingRecommendationWait(GameState state, int optionsId)
    {
        var friendly = state?.GetFriendlySidePlayer();
        var opposing = state?.GetOpposingSidePlayer();
        var active = state != null && state.IsGameCreated() && !state.IsGameOverNowOrPending() &&
            !state.WasConcedeRequested() && !state.IsMulliganManagerActive() && optionsId >= 0 &&
            state.GetOptionsPacket()?.ID == optionsId && friendly != null && opposing != null &&
            friendly.GetTag(GAME_TAG.CURRENT_PLAYER) == 1 && opposing.GetTag(GAME_TAG.CURRENT_PLAYER) == 0 &&
            s_boxStrategyMethod?.Invoke(null, null) is bool boxStrategy && boxStrategy;
        var handle = active ? GameMgr.Get()?.GetGameHandle().ToString() : null;
        var key = string.IsNullOrEmpty(handle) ? null : handle + ":" + state.GetTurn() + ":" + optionsId;
        var permitPolling = s_missingResponseGate.Observe(key, Time.realtimeSinceStartup, active, out var firstRelease);
        if (firstRelease)
            ArenaBoxBotPlugin.LogSource?.LogWarning("BoxAI响应门控无建议恢复: optionsId=" + optionsId +
                "; budgetSeconds=2; resumed native recommendation polling/timeout path; no action submitted by this gate");
        return permitPolling;
    }


    internal static string ReadExecutorStatus()
    {
        try
        {
            return s_botType?.GetMethod("RenderStatus", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null) as string;
        }
        catch { return null; }
    }
}

/// <summary>
/// Some current Arena draft pages receive the server's selected-card packet
/// before Unity has attached the three visual actors.  The native
/// DraftManager callback then throws a NullReferenceException from
/// DraftDisplay.OnChoiceSelected even though the network choice was accepted
/// and the next bucket can still be requested.  Swallow only that known
/// display-side exception while the Arena page is active; other exceptions
/// remain visible so real protocol failures are not hidden.
/// </summary>
internal static class ArenaDraftDisplaySafetyPatch
{
    private static Harmony s_harmony;
    private static float s_nextAttempt;
    private static float s_nextLog;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextAttempt) return;
        s_nextAttempt = Time.unscaledTime + 5f;
        try
        {
            var target = typeof(DraftManager).GetMethod(
                "InformDraftDisplayOfSelectedChoice",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var finalizer = typeof(ArenaDraftDisplaySafetyPatch).GetMethod(
                nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || finalizer == null) return;
            var harmony = new Harmony("local.hsmod.arenaboxbot.arena-draft-display-safety");
            harmony.Patch(target, finalizer: new HarmonyMethod(finalizer));
            s_harmony = harmony;
            ArenaBoxBotPlugin.LogSource?.LogInfo("Arena draft display safety patch installed");
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena draft display safety patch deferred: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static Exception Finalizer(Exception __exception)
    {
        if (__exception == null || !ArenaController.IsArenaUiActive || !HsModNativeArena.IsNativeArenaModeSelected())
            return __exception;
        if (__exception is NullReferenceException)
        {
            if (Time.unscaledTime >= s_nextLog)
            {
                s_nextLog = Time.unscaledTime + 15f;
                ArenaBoxBotPlugin.LogSource?.LogWarning("Arena draft display was not bound when the native choice callback arrived; ignored display-only exception and kept the draft state");
            }
            return null;
        }
        return __exception;
    }
}

/// <summary>
/// Settings that belong only to the Arena loop.  HsMod's existing
/// "自动脚本" section is for constructed play and battlegrounds; keeping
/// these entries in their own section prevents an Arena toggle from silently
/// changing the ladder script.
/// </summary>
internal static class ArenaSettings
{
    // This is the user-facing mode selector in HsMod's native settings card.
    // It mirrors the four HsMod modes and adds Arena without changing the
    // enum compiled into the installed HsMod assembly.
    internal enum NativeScriptMode
    {
        标准,
        狂野,
        休闲,
        酒馆战棋,
        竞技场
    }

    internal static ConfigEntry<NativeScriptMode> ScriptMode;

    internal enum ArenaMode
    {
        普通竞技场,
        地下竞技场
    }

    internal static ConfigEntry<ArenaMode> Mode;
    internal static ConfigEntry<bool> AutoDraft;
    internal static ConfigEntry<bool> AutoRedraft;
    internal static ConfigEntry<bool> AutoMatch;
    internal static ConfigEntry<bool> AutoPlay;
    internal static ConfigEntry<bool> UseBoxScores;
    internal static ConfigEntry<int> MaxGamesPerRun;
    internal static ConfigEntry<bool> StopOnTicketShortage;
    internal static ConfigEntry<bool> StopOnGoldShortage;
    internal static ConfigEntry<int> TicketGoldCost;

    internal static void Bind(ConfigFile config)
    {
        ScriptMode = config.Bind("竞技场脚本", "脚本模式", NativeScriptMode.标准,
            "HsMod 原生脚本模式。标准、狂野、休闲、酒馆战棋和竞技场都从这里切换。");
        Mode = config.Bind("竞技场脚本", "竞技场模式", ArenaMode.普通竞技场,
            "竞技场专用模式。门票数量以游戏服务器当前页面显示为准；该设置与‘自动脚本’里的标准、狂野、休闲和酒馆战棋相互独立。");
        AutoDraft = config.Bind("竞技场脚本", "自动选牌", true,
            "进入竞技场选牌页后按盒子推荐打法的明确选卡自动选择英雄和卡牌。关闭后保留页面，等待手动选牌。");
        AutoRedraft = config.Bind("竞技场脚本", "自动重选牌", true,
            "竞技场输掉一局后，按盒子推荐打法的明确选卡自动完成重选牌。关闭后停在重选牌页。");
        AutoMatch = config.Bind("竞技场脚本", "自动匹配", true,
            "套牌完成后自动开始下一局竞技场对战。关闭后停在套牌完成页。");
        AutoPlay = config.Bind("竞技场脚本", "自动对战", true,
            "进入竞技场对局后使用盒子推荐执行出牌、攻击、技能和结束回合。关闭后只负责选牌和套牌流程。");
        UseBoxScores = config.Bind("竞技场脚本", "使用盒子评分", true,
            "使用盒子推荐打法选择英雄、卡牌和重选牌；没有当前候选组对应的有效推荐时等待。");
        MaxGamesPerRun = config.Bind("竞技场脚本", "本轮最多对局数", 0,
            "同一竞技场轮次累计的自动对局上限；0 表示不限制。脚本启停或重连保留本轮计数。到限后完成当前对局及已有奖励结算再停止；提高上限或设为 0 可继续同一轮。");
        StopOnTicketShortage = config.Bind("竞技场脚本", "门票不足自动停止", true,
            "现有门票少于所选竞技场需要的数量时立即停止，不自动花费金币购票。关闭后才允许金币购票。");
        StopOnGoldShortage = config.Bind("竞技场脚本", "金币不足自动停止", true,
            "门票不足时检测金币；金币低于门票价格时停止竞技场流程。");
        TicketGoldCost = config.Bind("竞技场脚本", "门票金币价格", 150,
            "用于判断金币是否足够购买竞技场门票。默认 150；仅用于停止判断，不会修改游戏价格。");
    }

    private static int s_nativeTicketCost;
    private static bool s_nativeTicketCostUnderground;
    private static bool s_hasNativeTicketCost;
    internal static bool IsUnderground => Mode != null && Mode.Value == ArenaMode.地下竞技场;
    internal static int TicketCost => s_hasNativeTicketCost && s_nativeTicketCostUnderground == IsUnderground
        ? s_nativeTicketCost : (IsUnderground ? 2 : 1);

    private static DateTime s_paceReadAtUtc = DateTime.MinValue;
    private static bool s_cachedHumanPace = true;

    /// <summary>
    /// Reuse HsMod's own 操作节奏 setting instead of creating a second
    /// Arena-only speed switch.  The native HsMod config is intentionally read
    /// from the game directory so the setting also works when the controller
    /// is closed and Arena is started from the plugin UI.
    /// </summary>
    internal static bool UseHumanDraftPace
    {
        get
        {
            if (DateTime.UtcNow - s_paceReadAtUtc < TimeSpan.FromSeconds(2)) return s_cachedHumanPace;
            s_paceReadAtUtc = DateTime.UtcNow;
            s_cachedHumanPace = true;
            try
            {
                var path = ArenaRuntimePaths.Config("HsMod.cfg");
                if (!File.Exists(path)) return s_cachedHumanPace;
                var section = false;
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        section = line.Equals("[自动脚本]", StringComparison.OrdinalIgnoreCase) ||
                                  line.Equals("[AutoScript]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!section || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
                    var separator = line.IndexOf('=');
                    if (separator < 0) continue;
                    var key = line.Substring(0, separator).Trim();
                    if (!key.Equals("操作节奏", StringComparison.OrdinalIgnoreCase) &&
                        !key.Equals("Script Pace", StringComparison.OrdinalIgnoreCase) &&
                        !key.Equals("ScriptPace", StringComparison.OrdinalIgnoreCase)) continue;
                    s_cachedHumanPace = ArenaDraftPacing.IsHuman(line.Substring(separator + 1));
                    break;
                }
            }
            catch { }
            return s_cachedHumanPace;
        }
    }

    internal static void RefreshTicketCostFromNative()
    {
        try
        {
            var landing = ArenaLandingPageManager.Get();
            if (landing == null) return;
            var fieldName = IsUnderground ? "m_arenaUndergroundLandingPageDataModel" : "m_arenaNormalLandingPageDataModel";
            var field = typeof(ArenaLandingPageManager).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            var model = field?.GetValue(landing);
            var property = model?.GetType().GetProperty("ticketCost", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (property == null) return;
            var value = property.GetValue(model, null);
            if (value is int cost && cost > 0)
            {
                s_nativeTicketCost = cost;
                s_nativeTicketCostUnderground = IsUnderground;
                s_hasNativeTicketCost = true;
            }
        }
        catch { }
    }
}

/// <summary>
/// HsMod's normal script panel is intentionally for constructed play and
/// battlegrounds.  While this controller owns an Arena page, replace that
/// panel with a small Arena-specific panel so it cannot display a stale
/// ladder mode or ladder deck name.
/// </summary>
internal static class ArenaOverlayPatch
{
    private static Harmony s_harmony;
    private static float s_nextAttempt;

    internal static void EnsureInstalled()
    {
        if (s_harmony != null) return;
        if (Time.unscaledTime < s_nextAttempt) return;
        s_nextAttempt = Time.unscaledTime + 5f;
        try
        {
            var type = Type.GetType("HsMod.WildLadderBot, HsMod");
            var target = type?.GetMethod("OnGUI", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var prefix = typeof(ArenaOverlayPatch).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null) return;
            var harmony = new Harmony("local.hsmod.arenaboxbot.arena-overlay");
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            s_harmony = harmony;
            ArenaBoxBotPlugin.LogSource?.LogInfo("Arena overlay installed; constructed-play status panel will be restored outside Arena");
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena overlay install deferred: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool Prefix()
    {
        if (!ArenaController.IsArenaUiActive && !HsModNativeArena.IsNativeArenaModeSelected()) return true;
        ArenaController.DrawArenaOverlay();
        return false;
    }
}

internal sealed class ArenaBoxChoiceResult
{
    public int Choice;
    public string Detail;
    public bool SessionRejected;
}

/// <summary>
/// Small client for the same encrypted Arena scoring endpoints used by the
/// installed Box.  It reads the DPAPI session material that HsMod already
/// captured, sends only the current three candidates, and returns the highest
/// Box score/rank.  The caller runs it on a background task.
/// </summary>
internal static class ArenaBoxDirectClient
{
    private static readonly ArenaScoringReadinessGate ScoringReadiness = new ArenaScoringReadinessGate();
    private static readonly ArenaScoringProfileRefresh ProfileRefresh = new ArenaScoringProfileRefresh();
    private static string ProfilePath => ArenaRuntimePaths.File("boxai-direct-profile.dpapi");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HsAuto.NeteaseDirectRequestProfile.v1");
    private static readonly Regex StringProperty = new Regex("\\\"(?<name>Endpoint|Sid|Cookie|UserAgent|Rc4Key)\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"", RegexOptions.Compiled);
    private static DateTime s_profileValidationAtUtc = DateTime.MinValue;
    private static string s_profileValidationIdentity = "";
    private static bool s_profileValidationResult;
    private static string s_profileValidationDetail = "not checked";

    internal static bool TryValidateProfile(out string detail)
    {
        if (File.Exists(ArenaRuntimePaths.File("boxai-direct-profile.invalid")))
        { detail = "等待盒子刷新已被服务端拒绝的评分会话"; return false; }
        var currentFile = new FileInfo(ProfilePath);
        var currentIdentity = currentFile.Exists ? currentFile.LastWriteTimeUtc.Ticks + ":" + currentFile.Length : "missing";
        if (currentIdentity == s_profileValidationIdentity && DateTime.UtcNow - s_profileValidationAtUtc < TimeSpan.FromSeconds(2))
        {
            detail = s_profileValidationDetail;
            return s_profileValidationResult;
        }
        try
        {
            s_profileValidationIdentity = currentIdentity;
            var profile = LoadProfile();
            if (string.IsNullOrWhiteSpace(profile.Endpoint) ||
                string.IsNullOrWhiteSpace(profile.Sid) ||
                profile.Rc4Key == null || profile.Rc4Key.Length != 16)
            {
                s_profileValidationResult = false;
                s_profileValidationDetail = "profile fields incomplete";
                detail = s_profileValidationDetail;
                s_profileValidationAtUtc = DateTime.UtcNow;
                return s_profileValidationResult;
            }
            var file = new FileInfo(ProfilePath);
            var identity = file.LastWriteTimeUtc.Ticks + ":" + file.Length;
            s_profileValidationResult = ScoringReadiness.Check(identity, DateTime.UtcNow, () => Task.Run(() =>
            {
                var probe = Query(new[] { "HERO_08" }, false, true);
                return new ArenaScoreReply { Kind = probe.Choice > 0 ? ArenaScoreReplyKind.Success :
                    probe.SessionRejected ? ArenaScoreReplyKind.SessionKeyMissing : ArenaScoreReplyKind.Rejected,
                    Detail = probe.Detail };
            }), out s_profileValidationDetail);
        }
        catch (Exception ex)
        {
            s_profileValidationResult = false;
            s_profileValidationDetail = ex.GetType().Name;
        }
        s_profileValidationAtUtc = DateTime.UtcNow;
        detail = s_profileValidationDetail;
        return s_profileValidationResult;
    }

    public static ArenaBoxChoiceResult Query(IList<string> ids, bool underground, bool hero)
    {
        var result = new ArenaBoxChoiceResult { Choice = 0, Detail = "" };
        var stage = "profile";
        try
        {
            var profile = LoadProfile(value => stage = value);
            stage = "request-url";
            var endpoint = profile.Endpoint ?? "";
            var hs = endpoint.IndexOf("/hs/", StringComparison.Ordinal);
            if (hs <= 0) throw new InvalidOperationException("Box profile endpoint invalid");
            var path = hero ? "/hs/c/arena_hero_choice_info?hero_card_list=" : "/hs/c/arena_card_choice_info?card_list=";
            var gameType = underground ? "GT_UNDERGROUND_ARENA" : "GT_ARENA";
            var url = endpoint.Substring(0, hs) + path + Uri.EscapeDataString(string.Join(",", ids)) + "&sid=" + Uri.EscapeDataString(profile.Sid) + "&game_type=" + gameType;
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) })
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                stage = "request-headers";
                request.Headers.TryAddWithoutValidation("User-Agent", profile.UserAgent);
                request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(profile.Cookie) ? "ls_session_id=" + profile.Sid : profile.Cookie);
                request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,en,*");
                stage = "http-send";
                using (var response = http.SendAsync(request).GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode) { result.Detail = "HTTP " + (int)response.StatusCode; return result; }
                    stage = "response-text";
                    var wire = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                    stage = "response-decode";
                    var reply = ArenaBoxScoreProtocol.Decode(wire, profile.Rc4Key);
                    if (!reply.Success)
                    {
                        result.Detail = reply.Detail;
                        result.SessionRejected = reply.Kind == ArenaScoreReplyKind.SessionKeyMissing;
                        if (result.SessionRejected && ProfileRefresh.Signal(ProfilePath, profile.StorageFingerprint))
                        {
                            s_profileValidationResult = false;
                            s_profileValidationDetail = reply.Detail;
                            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena scoring session rejected with key null; requested the original Box profile capture refresh once");
                        }
                        return result;
                    }
                    stage = "response-scores";
                    result = Pick(reply.Json, ids, hero);
                    if (result.Choice > 0) ProfileRefresh.AfterVerifiedScore();
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            result.Detail = "stage=" + stage + "; " + ex.GetType().Name;
            // Only method identities: no URL, cookies, SID, key material or
            // response payload goes into the UI or diagnostic log.
            var frames = new StackTrace(ex, false).GetFrames();
            var methods = frames == null ? "unavailable" : string.Join(" <- ", frames.Take(8)
                .Select(frame => frame.GetMethod()).Where(method => method != null)
                .Select(method => method.DeclaringType?.FullName + "." + method.Name));
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena scoring request failed: stage=" + stage +
                "; exception=" + ex.GetType().Name + "; methods=" + methods);
            return result;
        }
    }

    private static ArenaBoxChoiceResult Pick(string json, IList<string> ids, bool hero)
    {
        // Box has used both a flat object keyed by card id and a nested
        // `data/results` response across releases.  Parse JSON first so a
        // nested object or numeric score is not mistaken for a missing score;
        // retain the small regex parser as a compatibility fallback for the
        // older encrypted endpoint payloads.
        var structured = PickStructured(json, ids, hero);
        if (structured.Choice > 0) return structured;
        return PickRegex(json, ids, hero);
    }

    private static ArenaBoxChoiceResult PickStructured(string json, IList<string> ids, bool hero)
    {
        var output = new ArenaBoxChoiceResult { Choice = 0, Detail = "status=true; no scored candidate" };
        try
        {
            var root = JObject.Parse(json);
            var status = FindProperty(root, "status");
            if (status != null && !StatusIsSuccessful(status))
            {
                output.Detail = "Box response status=false";
                return output;
            }
            var best = double.MinValue;
            var bestTie = long.MinValue;
            for (var i = 0; i < ids.Count; i++)
            {
                var node = FindCandidate(root, ids[i], hero);
                if (node == null) continue;
                double score;
                long tie;
                string rank = null;
                if (hero)
                {
                    rank = ReadTokenText(FindProperty(node, "hero_rank"));
                    score = RankScore(rank);
                    tie = ReadTokenLong(FindProperty(node, "win_rate"));
                }
                else
                {
                    score = ReadTokenDouble(FindProperty(node, "score"));
                    tie = ReadTokenLong(FindProperty(node, "select_cnt"));
                }
                if (score == double.MinValue || double.IsNaN(score)) continue;
                if (score > best || (Math.Abs(score - best) < 0.0001 && tie > bestTie))
                {
                    best = score;
                    bestTie = tie;
                    output.Choice = i + 1;
                    output.Detail = (hero ? "rank=" + rank + ",win_rate=" + tie :
                        "score=" + score.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ",select_cnt=" + tie) + ",card=" + ids[i];
                }
            }
        }
        catch
        {
            // The caller will try the legacy flat response parser below.
        }
        return output;
    }

    private static ArenaBoxChoiceResult PickRegex(string json, IList<string> ids, bool hero)
    {
        var output = new ArenaBoxChoiceResult { Choice = 0, Detail = "status=true; no scored candidate" };
        var best = double.MinValue;
        var bestTie = long.MinValue;
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var escaped = Regex.Escape(id);
            var match = Regex.Match(json, "\\\"" + escaped + "\\\"\\s*:\\s*\\{(?<body>[^}]*)\\}", RegexOptions.Singleline);
            if (!match.Success) continue;
            var body = match.Groups["body"].Value;
            double score;
            long tie;
            if (hero)
            {
                var rank = ReadString(body, "hero_rank");
                score = RankScore(rank);
                tie = ReadLong(body, "win_rate");
                if (score == double.MinValue) continue;
            }
            else
            {
                var scoreText = ReadString(body, "score");
                if (!double.TryParse(scoreText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out score)) continue;
                tie = ReadLong(body, "select_cnt");
            }
            if (score > best || (Math.Abs(score - best) < 0.0001 && tie > bestTie))
            {
                best = score;
                bestTie = tie;
                output.Choice = i + 1;
                output.Detail = (hero ? "rank=" + ReadString(body, "hero_rank") + ",win_rate=" + tie : "score=" + score.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",select_cnt=" + tie) + ",card=" + id;
            }
        }
        return output;
    }

    private static JToken FindCandidate(JToken root, string id, bool hero)
    {
        if (root == null || string.IsNullOrEmpty(id)) return null;
        if (root.Type == JTokenType.Object)
        {
            var direct = ((JObject)root)[id];
            if (direct != null && direct.Type == JTokenType.Object)
            {
                var directScored = FindScoredNode(direct, hero);
                if (directScored != null) return directScored;
            }
            var objectId = ReadTokenText(FindProperty(root, "card_id")) ??
                           ReadTokenText(FindProperty(root, "cardId")) ??
                           ReadTokenText(FindProperty(root, "id"));
            if (string.Equals(objectId, id, StringComparison.OrdinalIgnoreCase))
            {
                var objectScored = FindScoredNode(root, hero);
                if (objectScored != null) return objectScored;
            }
            foreach (var child in root.Children())
            {
                var found = FindCandidate(child, id, hero);
                if (found != null) return found;
            }
        }
        else if (root.Type == JTokenType.Array)
        {
            foreach (var child in root.Children())
            {
                var found = FindCandidate(child, id, hero);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static JToken FindScoredNode(JToken root, bool hero)
    {
        if (root == null) return null;
        if (root.Type == JTokenType.Object)
        {
            var score = FindProperty(root, hero ? "hero_rank" : "score");
            if (hero ? RankScore(ReadTokenText(score)) != double.MinValue : ReadTokenDouble(score) != double.MinValue)
                return root;
            foreach (var child in root.Children())
            {
                var found = FindScoredNode(child, hero);
                if (found != null) return found;
            }
        }
        else if (root.Type == JTokenType.Array)
        {
            foreach (var child in root.Children())
            {
                var found = FindScoredNode(child, hero);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static JToken FindProperty(JToken token, string name)
    {
        if (token == null || token.Type != JTokenType.Object) return null;
        var objectToken = token as JObject;
        if (objectToken == null) return null;
        var direct = objectToken[name];
        if (direct != null) return direct;
        foreach (var prop in objectToken.Properties())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) return prop.Value;
        return null;
    }

    private static bool StatusIsSuccessful(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token.Type == JTokenType.Boolean)
            return bool.TryParse(ReadTokenText(token), out var value) && value;
        var text = token.ToString().Trim();
        return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "success", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadTokenText(JToken token)
    {
        return token == null || token.Type == JTokenType.Null ? "" : token.ToString().Trim('"');
    }

    private static double ReadTokenDouble(JToken token)
    {
        var text = ReadTokenText(token);
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : double.MinValue;
    }

    private static long ReadTokenLong(JToken token)
    {
        var text = ReadTokenText(token);
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value)) return value;
        return (long)Math.Round(ReadTokenDouble(token) == double.MinValue ? 0 : ReadTokenDouble(token));
    }

    private static double RankScore(string rank)
    {
        if (string.IsNullOrEmpty(rank)) return double.MinValue;
        switch (rank.Trim().ToUpperInvariant())
        {
            case "S": return 600;
            case "A": return 500;
            case "B": return 400;
            case "C": return 300;
            case "D": return 200;
            case "E": return 100;
            case "F": return 0;
            default: return double.MinValue;
        }
    }

    private static string ReadString(string body, string name)
    {
        var m = Regex.Match(body, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(?:\\\"(?<s>(?:\\\\.|[^\\\"\\\\])*)\\\"|(?<n>null))", RegexOptions.Singleline);
        return m.Success && m.Groups["s"].Success ? m.Groups["s"].Value : "";
    }

    private static long ReadLong(string body, string name)
    {
        var m = Regex.Match(body, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(?<n>-?\\d+)");
        return m.Success && long.TryParse(m.Groups["n"].Value, out var n) ? n : 0;
    }

    private sealed class Profile
    {
        public string StorageFingerprint;
        public string Endpoint;
        public string Sid;
        public string Cookie;
        public string UserAgent;
        public byte[] Rc4Key;
    }

    private static Profile LoadProfile(Action<string> stage = null)
    {
        stage?.Invoke("profile-file-read");
        var cipher = File.ReadAllBytes(ProfilePath);
        stage?.Invoke("profile-dpapi");
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        stage?.Invoke("profile-utf8");
        var json = Encoding.UTF8.GetString(plain);
        stage?.Invoke("profile-fields");
        var p = new Profile();
        p.StorageFingerprint = ArenaScoringProfileRefresh.Fingerprint(cipher);
        foreach (Match m in StringProperty.Matches(json))
        {
            var value = Regex.Unescape(m.Groups["value"].Value).Replace("\\/", "/");
            switch (m.Groups["name"].Value)
            {
                case "Endpoint": p.Endpoint = value; break;
                case "Sid": p.Sid = value; break;
                case "Cookie": p.Cookie = value; break;
                case "UserAgent": p.UserAgent = value; break;
                case "Rc4Key": p.Rc4Key = Convert.FromBase64String(value); break;
            }
        }
        if (string.IsNullOrEmpty(p.Endpoint) || string.IsNullOrEmpty(p.Sid) || p.Rc4Key == null) throw new InvalidOperationException("Box profile incomplete");
        return p;
    }
}

internal static class ArenaStatsStore
{
    internal static string Path => ArenaRuntimePaths.File("arena-stats.json");
    internal static ArenaStatsData Data { get; private set; } = new ArenaStatsData();
    private static bool _canSave = true;

    internal static void Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                Data = ArenaStatsPersistence.Read(File.ReadAllText(Path), out var migrationDetail);
                if (!string.IsNullOrEmpty(migrationDetail))
                    ArenaBoxBotPlugin.LogSource?.LogWarning("Arena stats migration: " + migrationDetail);
            }
            _canSave = true;
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena stats load failed: " + ex.GetType().Name + ": " + ex.Message +
                "; original file retained; statistics saving suspended until a successful reload");
            Data = new ArenaStatsData();
            // Preserve the unreadable source for diagnosis instead of later
            // overwriting historical counters with the temporary empty view.
            _canSave = false;
        }
    }

    internal static void Save()
        => TrySave();

    internal static bool TrySave()
    {
        if (!_canSave) return false;
        string pending = null;
        try
        {
            var json = ArenaStatsPersistence.Write(Data);
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Stats are updated from the Unity main thread, but a client or
            // power loss can still interrupt the write. Replace the completed
            // JSON atomically so the next launch never sees a half-written
            // lifetime counter file.
            pending = Path + ".pending-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(pending, json, Encoding.UTF8);
            try
            {
                if (File.Exists(Path)) File.Replace(pending, Path, null);
                else File.Move(pending, Path);
                pending = null;
            }
            catch
            {
                // File.Replace is unavailable on some redirected/profile
                // filesystems. A completed copy is still safer than leaving
                // the old JSON absent, and the pending file is removed below.
                File.Copy(pending, Path, true);
            }
            return true;
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena stats save failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(pending))
            {
                try { File.Delete(pending); } catch { }
            }
        }
    }

    internal static bool SaveEntryReceipt(bool underground, ArenaEntryReceipt expected, out string failure)
    {
        if (ArenaStatsPersistence.SaveAndConfirmEntryReceipt(TrySave, () => File.ReadAllText(Path),
            underground, expected, out failure)) return true;
        failure += "; path=" + Path;
        ArenaBoxBotPlugin.LogSource?.LogWarning(failure);
        return false;
    }

    internal static void StartRun(int initialWins, int initialLosses, string runKey, bool confirmedNewRun,
        bool replaceActiveRun = false)
    {
        if (ArenaStatsLedger.StartRun(Data, runKey, initialWins, initialLosses, confirmedNewRun, replaceActiveRun)) Save();
    }

    internal static void RecordReward()
    {
        if (ArenaStatsLedger.CompleteRun(Data)) Save();
    }

    internal static void MarkRewardPending()
    {
        if (!Data.RunActive || Data.RewardPending) return;
        Data.RewardPending = true;
        Save();
    }

    internal static void ReconcileResults(int wins, int losses)
    {
        if (ArenaStatsLedger.Reconcile(Data, wins, losses)) Save();
    }

    internal static void BeginGame(string key)
    {
        if (ArenaStatsLedger.BeginGame(Data, key)) Save();
    }

    internal static bool SelectMode(bool underground)
    {
        if (!ArenaStatsLedger.SelectMode(Data, underground)) return false;
        Save();
        return true;
    }

    internal static bool FinishGame(string key, bool win)
    {
        if (!ArenaStatsLedger.FinishGame(Data, key, win)) return false;
        Save();
        return true;
    }
    internal static string Summary()
    {
        var d = Data ?? new ArenaStatsData();
        return "战绩：总 " + d.TotalWins + "胜 " + d.TotalLosses + "负｜本轮 " +
               d.CurrentRunWins + "胜 " + d.CurrentRunLosses + "负｜轮次 " + d.RunCount +
               "｜最高 " + d.HighestWins + "胜\n奖励：" + d.RewardsClaimed +
               " 次｜购票：" + d.TicketsPurchased + " 次｜金币：" + d.GoldSpent;
    }

}

[Serializable]
internal sealed class ArenaRuntimeStateData
{
    public long updatedAtUnixMs;
    public int gameProcessId;
    public long gameStartedAtUtcTicks;
    public bool arenaSelected;
    public bool runtimeEnabled;
    public bool inGameplay;
    public string scene = "";
    public string arenaState = "";
    public bool runActive;
    public int slot;
    public int maxSlot;
    public bool limitsKnown;
    public int maxWins = -1;
    public int maxLosses = -1;
    public int gamesThisRun;
    public string stopReason = "";
    public string runKey = "";
    public string progressKey = "";
    // Read-only self-description used by the optional controller.  The plugin
    // is fully autonomous: these fields exist so an external monitor can show
    // which of the five supported modes is active without having to duplicate
    // the plugin's mode logic.
    public string pluginVersion = ArenaModeCatalog.PluginVersion;
    public string mode = "";
    public string supportedModes = "";
    public bool controllerRequired = false;
}

internal sealed partial class ArenaController : MonoBehaviour
{
    // ProductPageJobs can take a few seconds to bind the native shop widget,
    // but an unavailable shop must not leave the Arena loop retrying forever.
    private const float TicketPurchaseTimeoutSeconds = 45f;
    private readonly ArenaLifecyclePolicy _normalLifecycle = new ArenaLifecyclePolicy();
    private readonly ArenaLifecyclePolicy _undergroundLifecycle = new ArenaLifecyclePolicy();
    private ArenaLifecyclePolicy _lifecycle => ArenaStatsStore.Data.RunUnderground ? _undergroundLifecycle : _normalLifecycle;
    private float _unexpectedSceneSince = -1f;
    private float _foreignGameplaySince = -1f;
    private readonly ArenaRewardReceiptPolicy _rewardReceipt = new ArenaRewardReceiptPolicy();
    private static string EnablePath => ArenaRuntimePaths.File("arena-bot.enabled");

    /// <summary>Scene heartbeat consumed by the controller before it enables the
    /// script: every mode must start from the main lobby, not from a settlement
    /// page or the middle of a mode transition.</summary>
    private static string ControllerScenePath => ArenaRuntimePaths.File("controller-scene.json");
    private static string ControllerReturnHubRequestPath => ArenaRuntimePaths.File("controller-return-hub.marker");
    private static string ControllerReturnHubReceiptPath => ArenaRuntimePaths.File("controller-return-hub.done");
    private static string ControllerStopAfterGamePath => ArenaRuntimePaths.File("controller-stop-after-game.marker");
    private static string InternalStopPath => ArenaRuntimePaths.File("arena-stop-requested.marker");
    private static string BoxHeartbeatPath => ArenaRuntimePaths.File("boxai-heartbeat.json");
    private static string HsModConfigPath => ArenaRuntimePaths.Config("HsMod.cfg");
    private static string EventLogPath => ArenaRuntimePaths.File("arena-box-bot.log");
    private static string RuntimeStatePath => ArenaRuntimePaths.File("arena-runtime-state.json");
    private static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static FieldInfo s_normalSessionField;
    private static FieldInfo s_undergroundSessionField;
    private static string s_entryReceiptWriteFailure;
    private readonly ArenaEntryRecoveryBudget _entryRecoveryBudget = new ArenaEntryRecoveryBudget();
    private static FieldInfo s_choicesField;
    private static FieldInfo s_cardIdField;
    private static FieldInfo s_actorField;
    private static MethodInfo s_confirmPackageChoiceMethod;
    private float _nextTick;
    private float _nextDraftCheck;
    private float _nextHeartbeatLog;
    private float _nextChoiceDiag;
    private bool _requestedDraft;
    private bool _undergroundRequested;
    private bool _requestedChoices;
    private bool _rewardFlowStarted;
    private bool _rewardAckRequested;
    private bool _rewardUiWasPending;
    private bool _rewardAutoOpenChanged;
    private bool _rewardAutoOpenPrevious;
    private float _rewardFlowStartedAt = -1f;
    private float _rewardNativeDoneRequestedAt = -1f;
    private bool _matchmakingRequested;
    private float _matchmakingRequestedAt = -1f;
    private float _nextMatchRetryAt;
    private bool _ticketPurchaseRequested;
    private bool _ticketPurchaseClicked;
    private bool _ticketPurchaseConfirmed;
    private float _nextTicketProductRetry;
    private bool _arenaAutomationEnabled;
    private bool _redraftPageStarted;
    private float _redraftPageStartedAt = -1f;
    private float _nextRedraftDiagnostic;
    private string _lastArenaState = "";
    private string _lastSceneMode = "";
    private bool _loginIntroDismissed;
    private float _nextPopupDismiss;
    private float _nextArenaReadyLog;
    private float _nextBoxExecutorRecoveryCheck;
    private bool _boxExecutorRecoveryAttempted;
    private int _gamesThisRun;
    private bool _runStopRequested;
    private string _runStopReason = "";
    private int _ticketPurchaseAttempts;
    private float _ticketPurchaseStartedAt = -1f;
    private float _nextResourceCheck;
    private float _nextRewardRefresh;
    private float _nextRuntimeStateWrite;
    private float _nextBudgetIdentityDiagnostic;
    private static GUIStyle s_arenaOverlayStyle;
    private static readonly System.Random s_draftPaceRandom = new System.Random();
    private static MethodInfo s_hsModUiActionDelayMethod;
    private static object s_hsModChoiceCategory;
    private static bool s_hsModChoiceDelayUnavailable;
    // HsMod's “拟人” delay starts after the explicit recommendation arrives.
    // The native-bucket stream retains that one-shot callback throughout the
    // independent visual and post-recommendation pacing gates.
    private string _draftRecommendationPaceKey = "";
    private float _draftRecommendationReadyAt = -1f;
    private float _nextPopupDiagnostic;
    private float _nextLegendaryOverlayDiagnostic;
    private string _lastLegendaryOverlayDiagnostic = "";
    private int _dismissedPopupCount;
    private static FieldInfo s_rewardBoxesDisplayField;
    private static MethodInfo s_onRewardBoxesDoneMethod;
    private static MethodInfo s_showAlreadyOpenedRewardsMethod;
    private static MethodInfo s_killRewardObjectsMethod;
    private static MethodInfo s_destroyRewardsWidgetMethod;
    private float _lastRewardWidgetDestroyLogAt = -100f;
    private static FieldInfo s_alertPopupInfoField;
    private static FieldInfo s_pendingPremiumField;
    private static FieldInfo s_premiumResponseCallbackField;
    private float _nextStaleRewardCleanup;
    private readonly HashSet<string> _attentionCandidateNames = new(StringComparer.Ordinal);
    private string _lastAttentionDiagnostic = "";
    private static readonly HashSet<string> DecorativeAttentionTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // These are always-on card/zone labels. They are not blocking
        // dialogs and should not pollute the diagnostic log every 10 seconds.
        "CardTypeBanner", "CardRuneBanner", "EnchantmentBanner", "NameBanner", "LoanerFlag"
    };

    internal static bool IsArenaUiActive { get; private set; }
    // True only while the Arena scene is a real Arena gameplay screen, the
    // automation toggle is enabled, and the Box bridge passed its readiness
    // gate.  HsMod's normal Tick uses this lease to avoid ladder actions in
    // Arena and to keep manual Arena play untouched when AutoPlay is off.
    internal static bool IsArenaGameplayExecutorEnabled { get; private set; }
    private static string s_arenaOverlayPhase = "等待进入竞技场";
    private static string s_arenaOverlayBudget = "对局：0/不限";
    private static string s_arenaOverlayResources = "资源：门票未知｜金币未知";
    private static string s_arenaOverlayStats = "战绩：暂无";
    private static string s_arenaOverlayStopReason = "";

    private void Awake()
    {
        ArenaStatsStore.Load();
        ArenaLifecycleEvents.EnsureInstalled();
        ArenaDeckDiscardStream.InitializeBoundary();
        ArenaDraftRecommendationStream.InitializeBoundary();
        ArenaDraftRecommendationRuntime.EnsureInstalled();
        ArenaDeckEditReceipts.EnsureInstalled();
        s_arenaOverlayStats = ArenaStatsStore.Summary();
    }

    private static float _nextControllerScenePublish;
    private static float _nextHubRequestAt;

    /// <summary>
    /// GAMEPLAY stays the active scene on the post-game results screen (Battlegrounds
    /// especially), so the scene name alone cannot prove a match is being played.
    /// Ask GameState: a created, not-finished game is live; anything else is a
    /// results/settlement screen that the controller may leave.
    /// </summary>
    private static bool LooksLikeLiveGameplay(string sceneName)
    {
        if (sceneName == "GAME_MODE") return true;
        if (sceneName != "GAMEPLAY") return false;
        try
        {
            var state = GameState.Get();
            if (state == null) return true;
            if (!state.IsGameCreated()) return false;
            if (state.IsGameOverNowOrPending()) return false;
            return true;
        }
        catch { return true; }
    }

    /// <summary>Writes a one-line scene heartbeat (1s cadence) so the controller
    /// can verify the client is on the main lobby before enabling the script.</summary>
    private static void PublishControllerSceneHeartbeat()
    {
        try
        {
            if (Time.unscaledTime < _nextControllerScenePublish) return;
            _nextControllerScenePublish = Time.unscaledTime + 1f;
            var scene = SceneMgr.Get();
            if (scene == null) return;
            var mode = scene.GetMode().ToString();
            var next = scene.GetNextMode().ToString();
            var gameplay = LooksLikeLiveGameplay(mode);
            var nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            var json = "{\"updatedAtUnixMs\":" + nowMs +
                       ",\"scene\":\"" + mode + "\",\"next\":\"" + next +
                       "\",\"gameplay\":" + (gameplay ? "true" : "false") + "}";
            System.IO.Directory.CreateDirectory(ArenaRuntimePaths.HsModDirectory);
            System.IO.File.WriteAllText(ControllerScenePath, json);
        }
        catch
        {
            // Diagnostics must never disturb the game loop.
        }
    }

    /// <summary>The controller asks for the main lobby before enabling the script.
    /// A match (GAMEPLAY/GAME_MODE) and LOGIN are never touched: the client owns
    /// those transitions and interrupting them is worse than waiting.</summary>
    /// <summary>
    /// A modal AlertPopup (client message / reconnect notice) blocks the login
    /// flow and every navigation request. Answer it with its own response path so
    /// the client's callback runs; fall back to hiding the widget.
    /// </summary>
    private bool TryDismissAlertPopup()
    {
        try
        {
            foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                var type = component.GetType();
                if (type.Name != "AlertPopup") continue;
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.Name != "Respond" && method.Name != "HideAlert" && method.Name != "Hide") continue;
                    var parameters = method.GetParameters();
                    object[] arguments;
                    if (parameters.Length == 0) arguments = Array.Empty<object>();
                    else if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
                    {
                        var enumType = parameters[0].ParameterType;
                        var okName = Enum.GetNames(enumType).FirstOrDefault(name =>
                            name.Equals("OK", StringComparison.OrdinalIgnoreCase));
                        if (okName == null) continue;
                        arguments = new object[] { Enum.Parse(enumType, okName) };
                    }
                    else continue;
                    method.Invoke(component, arguments);
                    Log("dismissed the alert popup via " + method.Name + "()");
                    return true;
                }
                component.gameObject.SetActive(false);
                Log("forced the alert popup inactive");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log("alert popup dismissal failed: " + ex.Message);
        }
        return false;
    }

    /// <summary>
    /// The popup display manager keeps a static "showing" flag. When a popup was
    /// hidden by something else (automation, a failed asset load) the flag can stay
    /// set, the login intro popup is queued forever and the client never reaches the
    /// hub. With nothing actually shown, resetting the flag is safe.
    /// </summary>
    private bool TryClearStuckPopupDisplay()
    {
        try
        {
            var type = typeof(PopupDisplayManager);
            var field = type.GetField("s_isShowing",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) return false;
            if (!(field.GetValue(null) is bool showing) || !showing) return false;
            field.SetValue(null, false);
            Log("cleared a stuck PopupDisplayManager.s_isShowing flag while waiting for the lobby");
            return true;
        }
        catch (Exception ex)
        {
            Log("popup display reset failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Login-time promotional widgets (InnKeepersSpecial and friends) keep the
    /// login flow waiting; the client never reaches the hub while they are open.
    /// Close that small allow-list only, and never touch other UI.
    /// </summary>
    private static float _nextLoginBlockerAttempt;

    private bool TryDismissLoginBlockingWidgets()
    {
        if (Time.unscaledTime < _nextLoginBlockerAttempt) return false;
        _nextLoginBlockerAttempt = Time.unscaledTime + 3f;
        var dismissed = false;
        try
        {
            foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                var type = component.GetType();
                if (type.Name != "InnKeepersSpecial" && type.Name != "InnKeepersSpecialAd" &&
                    type.Name != "BoxProductBanner") continue;
                var closed = false;
                foreach (var methodName in new[] { "Close", "Hide", "Dismiss", "OnCloseButton", "OnCloseButtonPressed", "CloseAd", "OnAdClosed" })
                {
                    var method = type.GetMethod(methodName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    if (method == null) continue;
                    method.Invoke(component, null);
                    Log("closed login blocker " + type.Name + " via " + methodName + "()");
                    closed = true;
                    break;
                }
                if (!closed || component.gameObject.activeInHierarchy)
                {
                    component.gameObject.SetActive(false);
                    Log("forced the login blocker " + type.Name + " inactive (handler=" + (closed ? "called" : "absent") + ")");
                }
                dismissed = true;
            }
        }
        catch (Exception ex)
        {
            Log("login blocker dismissal failed: " + ex.Message);
        }
        return dismissed;
    }

    private static float _nextLoginDump;

    /// <summary>Diagnostic: what is actually on screen while the login flow waits.</summary>
    private void DumpLoginSceneOnce()
    {
        if (Time.unscaledTime < _nextLoginDump) return;
        _nextLoginDump = Time.unscaledTime + 15f;
        try
        {
            // Only UI-blocking components matter here; the full list was too long
            // to read and hid the one popup that actually blocks the login flow.
            var interesting = new[] { "Popup", "Dialog", "Alert", "Login", "Inn", "Quest", "Banner", "Modal", "Toast", "Notification" };
            var names = new List<string>();
            foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component == null) continue;
                var type = component.GetType();
                var match = false;
                foreach (var token in interesting)
                {
                    if (type.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                }
                if (!match) continue;
                var suffix = component.gameObject.activeInHierarchy ? "" : "(inactive)";
                names.Add(type.Name + suffix);
            }
            names.Sort(StringComparer.Ordinal);
            var distinct = new List<string>();
            foreach (var name in names)
            {
                if (distinct.Count >= 45) break;
                if (!distinct.Contains(name)) distinct.Add(name);
            }
            var sceneName = "";
            try { sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            Log("login scene dump: scene=" + sceneName + "; types=" + string.Join(",", distinct));
        }
        catch (Exception ex)
        {
            Log("login scene dump failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The post-login "intro" is a LoginPopupSequencePopup; while it is open
    /// SceneMgr stays in LOGIN and the controller can never reach the lobby.
    /// Close that specific type only: prefer its own handler, fall back to
    /// hiding the widget. Never touch other popups here.
    /// </summary>
    private bool TryDismissLoginIntroPopup()
    {
        try
        {
            foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                var type = component.GetType();
                if (!type.Name.StartsWith("LoginPopupSequencePopup", StringComparison.Ordinal)) continue;
                foreach (var methodName in new[] { "Hide", "Dismiss", "Continue", "OnContinueButtonPressed", "Complete" })
                {
                    var method = type.GetMethod(methodName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    if (method == null) continue;
                    method.Invoke(component, null);
                    Log("closed the login intro popup via " + methodName + "(); type=" + type.Name);
                    return true;
                }
                component.gameObject.SetActive(false);
                Log("hid the login intro popup widget; type=" + type.Name);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log("login intro popup dismissal failed: " + ex.Message);
        }
        return false;
    }

    private void TryProcessControllerReturnHubRequest()
    {
        try
        {
            if (!System.IO.File.Exists(ControllerReturnHubRequestPath)) return;
            var scene = SceneMgr.Get();
            if (scene == null) return;
            var mode = scene.GetMode().ToString();
            var nowMs = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            if (mode == "HUB" || mode == "TOURNAMENT")
            {
                System.IO.File.WriteAllText(ControllerReturnHubReceiptPath, nowMs.ToString());
                System.IO.File.Delete(ControllerReturnHubRequestPath);
                Log("controller hub request completed; scene=" + mode);
                return;
            }
            // The login intro (Welcome Quests) blocks the transition to the hub while
            // the script is off. The controller only writes this marker when it is
            // waiting for the lobby, so dismissing it here is safe.
            if (mode == "LOGIN")
            {
                try
                {
                    var welcomeQuests = WelcomeQuests.Get();
                    if (welcomeQuests != null)
                    {
                        WelcomeQuests.Hide();
                        Log("dismissed the login welcome page on controller request");
                    }
                    else if (TryDismissLoginIntroPopup() || TryDismissAlertPopup() || TryDismissLoginBlockingWidgets() || TryClearStuckPopupDisplay())
                    {
                        // a known login blocker was closed
                    }
                    else
                    {
                        // Award/welcome banners also keep the scene in LOGIN. Use the
                        // same allow-list policy the running loop uses: it never
                        // closes functional dialogs (purchases, reconnect, journal).
                        DismissArenaAttentionPopups(scene.GetMode(), false);
                    }
                    DumpLoginSceneOnce();
                    if (Time.unscaledTime >= _nextHubRequestAt)
                    {
                        _nextHubRequestAt = Time.unscaledTime + 10f;
                        Log("controller waits for the lobby; login screen still open");
                    }
                }
                catch (Exception ex) { Log("login screen cleanup failed: " + ex.Message); }
                return;
            }
            // FATAL_ERROR cannot be navigated away from; the controller restarts the client.
            if (mode == "FATAL_ERROR") return;
            if (mode == "GAMEPLAY" || mode == "GAME_MODE")
            {
                // A real match is never interrupted. A finished match leaves the
                // scene on the results screen, which would otherwise trap the
                // controller forever ("stuck after the Battlegrounds match ends").
                if (LooksLikeLiveGameplay(mode)) return;
                scene.SetNextMode(SceneMgr.Mode.HUB);
                Log("match is over (results screen); leaving scene=" + mode + " for the hub");
                return;
            }
            if (Time.unscaledTime < _nextHubRequestAt) return;
            _nextHubRequestAt = Time.unscaledTime + 2f;
            scene.SetNextMode(SceneMgr.Mode.HUB);
            Log("controller requested the main lobby; leaving scene=" + mode);
        }
        catch (Exception ex)
        {
            Log("controller hub request failed: " + ex.Message);
        }
    }

    private void Update()
    {
        GameFrameTimingDiagnostics.ObserveFrame();
        PowerQueueDiagnostics.ObserveFrame();
        NativeChoicePacketOrderGuard.EnsureInstalled();
        NativeBgPositionProgressGuard.EnsureInstalled();
        NativeBgPreAdjustmentRecovery.EnsureInstalled();
        NativeBgShopToggleQueueGuard.EnsureInstalled();
        NativeBgShopSourceGuard.EnsureInstalled();
        NativeBgInvalidOptionGuard.EnsureInstalled();
        NativeBgRejectedRollbackGuard.EnsureInstalled();
        NativeBgHeroChoiceDiagnostics.EnsureInstalled();
        NativeBgCompletedGameGuard.EnsureInstalled();
        NativeMemoryCleanupGuard.EnsureInstalled();
        MaintainPopupOperations();
        ArenaOverlayPatch.EnsureInstalled();
        HsModNativeArenaPatch.EnsureInstalled();
        ArenaDraftDisplaySafetyPatch.EnsureInstalled();
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 0.5f;
        ArenaDeckDiscardStream.Poll();
        PublishControllerSceneHeartbeat();
        ControllerCommandRuntime.Apply(Log);
        TryProcessControllerReturnHubRequest();
        var arenaModeSelected = HsModNativeArena.IsNativeArenaModeSelected();
        var nativeArenaRunning = arenaModeSelected && HsModNativeArena.IsRuntimeEnabled();
        try { SynchronizeArenaStatsMode(arenaModeSelected); }
        catch (Exception ex)
        {
            Log("Arena current-run context check failed: " + ex.Message);
            return;
        }
        // Clear a previous activation's transient stop before reconciling new
        // receipts. A fast reward ACK in this tick can then latch the reached
        // budget without the activation transition erasing that fresh stop.
        ObserveNativeArenaActivation(arenaModeSelected, nativeArenaRunning);
        // Results and paid transactions can arrive after leaving Draft or
        // turning automation off. Reconcile them before any action gate.
        try
        {
            var pendingManager = DraftManager.Get();
            PollNativeArenaResults(pendingManager);
            CompleteTicketPurchaseStats(pendingManager);
            if (HasConfirmedPendingRewardReceipt(pendingManager)) CompleteArenaRunStats(true);
        }
        catch (Exception ex)
        {
            _rewardReceipt.Reset();
            Log("Arena pending receipt check failed: " + ex.Message);
        }
        // The marker is retained only for controller stop-after-game requests.
        // It no longer starts Arena automation by itself: after installation,
        // HsMod's mode selector and F12 are the single source of truth.
        if (ArenaSettings.Mode == null || !nativeArenaRunning)
        {
            // Installing the plugin must not give it ownership of manual
            // browsing. In particular Journal and reconnect dialogs belong
            // to the user/client even when their GameObjects remain active.
            _attentionCandidateNames.Clear();
            _lastAttentionDiagnostic = "";
            if (_arenaAutomationEnabled) SetArenaAutomation(false);
            IsArenaGameplayExecutorEnabled = false;
            RestoreHsModRewardAutomation();
            try { PopupDisplayManager.SuppressPopupsTemporarily = false; } catch { }
            IsArenaUiActive = false;
            PublishDisabledRuntimeState(arenaModeSelected, nativeArenaRunning);
            return;
        }
        try
        {
            var scene = SceneMgr.Get();
            if (scene == null)
            {
                if (Time.unscaledTime >= _nextHeartbeatLog) { _nextHeartbeatLog = Time.unscaledTime + 10f; Log("enabled, waiting for SceneMgr"); }
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, "INITIALIZING", false, "", null);
                return;
            }
            var mode = scene.GetMode();
            var modeName = mode.ToString();
            if (modeName == "TOURNAMENT")
            {
                if (_unexpectedSceneSince < 0f) _unexpectedSceneSince = Time.unscaledTime;
                if (ArenaLifecyclePolicy.ShouldReturnToHub(modeName, scene.GetNextMode().ToString(),
                    _runStopRequested || File.Exists(ControllerStopAfterGamePath),
                    Time.unscaledTime - _unexpectedSceneSince))
                {
                    _requestedDraft = false;
                    _matchmakingRequested = false;
                    _requestedChoices = false;
                    _unexpectedSceneSince = Time.unscaledTime;
                    scene.SetNextMode(SceneMgr.Mode.HUB);
                    Log("Arena remained on TOURNAMENT for 30s; requested Hub to restore the Arena page");
                }
            }
            else _unexpectedSceneSince = -1f;
            var undergroundGameplay = false;
            var arenaGameplay = false;
            if (mode == SceneMgr.Mode.GAMEPLAY)
            {
                try
                {
                    var gameplayDraftManager = DraftManager.Get();
                    undergroundGameplay = gameplayDraftManager != null && gameplayDraftManager.IsUnderground();
                    var game = GameMgr.Get();
                    arenaGameplay = game != null && (game.IsArena() || game.IsUndergroundArena());
                }
                catch
                {
                    undergroundGameplay = false;
                    arenaGameplay = false;
                }
            }
            if (mode == SceneMgr.Mode.GAMEPLAY && !arenaGameplay)
            {
                // The client is sitting on another mode's gameplay/result screen
                // (for example Battlegrounds). Arena navigation cannot start from
                // there, so ask for the Hub once no game is running any more.
                if (_foreignGameplaySince < 0f)
                {
                    _foreignGameplaySince = Time.unscaledTime;
                    Log("foreign gameplay scene while Arena is selected; waiting for the match to end before returning to Hub");
                }
                var gameInProgress = true;
                try
                {
                    // A live match (still created and not finished) must never be
                    // abandoned; only a finished/result game may release the scene.
                    var foreignState = GameState.Get();
                    gameInProgress = foreignState != null && foreignState.IsGameCreated() &&
                        !foreignState.IsGameOverNowOrPending() && !foreignState.WasConcedeRequested();
                }
                catch { gameInProgress = true; }
                if (ArenaSceneRecoveryPolicy.ShouldLeaveForeignGameplay(modeName,
                        scene.GetNextMode().ToString(), arenaGameplay, gameInProgress,
                        _runStopRequested || File.Exists(ControllerStopAfterGamePath),
                        Time.unscaledTime - _foreignGameplaySince))
                {
                    _foreignGameplaySince = Time.unscaledTime;
                    scene.SetNextMode(SceneMgr.Mode.HUB);
                    Log("foreign gameplay ended (no running game for 20s); requested Hub so Arena navigation can resume");
                }
            }
            else _foreignGameplaySince = -1f;            // DRAFT is owned by this controller and is switched to the
            // Arena session below.  GAMEPLAY must pass the native
            // Arena-session check so an ordinary constructed match
            // cannot inherit Arena automation or the Arena status panel.
            IsArenaUiActive = mode == SceneMgr.Mode.DRAFT || (mode == SceneMgr.Mode.GAMEPLAY && arenaGameplay);
            var controllerStopAfterGame = File.Exists(ControllerStopAfterGamePath);
            if (controllerStopAfterGame && mode == SceneMgr.Mode.GAMEPLAY && arenaGameplay)
            {
                s_arenaOverlayStopReason = "控制端请求：本局结束后停止";
            }
            else if (controllerStopAfterGame && mode != SceneMgr.Mode.GAMEPLAY)
            {
                // Leaving GAMEPLAY does not prove that a reward page has
                // loaded. A real terminal game passed through this gap with
                // DraftManager still reporting its previous state. Retain the
                // request until the native Draft view below is bound; its
                // REWARDS/NO_RUN flow determines when to consume the marker.
                s_arenaOverlayStopReason = "控制端请求：等待竞技场页面及奖励状态确认";
            }
            if (mode == SceneMgr.Mode.GAMEPLAY)
            {
                s_arenaOverlayPhase = "对战中";
            }
            else if (mode == SceneMgr.Mode.DRAFT)
            {
                try
                {
                    s_arenaOverlayPhase = DraftManager.Get()?.GetArenaState(ArenaSettings.IsUnderground).ToString() ?? "竞技场页面";
                }
                catch
                {
                    s_arenaOverlayPhase = "竞技场页面";
                }
            }
            else
            {
                s_arenaOverlayPhase = "等待进入竞技场";
            }
            // Match only reviewed notification types. The navigation stack
            // and functional dialogs stay owned by their native flow.
            if ((mode == SceneMgr.Mode.LOGIN || mode == SceneMgr.Mode.HUB ||
                 mode == SceneMgr.Mode.DRAFT || mode == SceneMgr.Mode.GAMEPLAY) &&
                Time.unscaledTime >= _nextPopupDismiss)
            {
                _nextPopupDismiss = Time.unscaledTime + 2f;
                DismissArenaAttentionPopups(mode, arenaGameplay);
            }
            var sceneChanged = !string.Equals(modeName, _lastSceneMode, StringComparison.Ordinal);
            if (sceneChanged)
            {
                var previousMode = _lastSceneMode;
                _lastSceneMode = modeName;
                if (mode == SceneMgr.Mode.HUB &&
                    (previousMode == SceneMgr.Mode.DRAFT.ToString() || previousMode == SceneMgr.Mode.GAMEPLAY.ToString()))
                {
                    // A failed native transition, a finished run, and some
                    // login recovery paths all return to HUB.  The first
                    // request was already consumed, so allow the next tick
                    // to open the Arena draft page again.
                    _requestedDraft = false;
                    _matchmakingRequested = false;
                    _requestedChoices = false;
                    _nextStaleRewardCleanup = 0f;
                    _redraftPageStarted = false;
                    Log("returned to Hub; reset Arena page-cycle gates");
                }
                if (mode == SceneMgr.Mode.DRAFT && previousMode == SceneMgr.Mode.GAMEPLAY.ToString())
                {
                    // The native Arena client returns to Draft after both a
                    // win and a loss.  Matchmaking belongs to the just-finished
                    // game, so clear its latch before the next MIDRUN or
                    // redraft decision is evaluated.
                    _matchmakingRequested = false;
                    _requestedChoices = false;
                    _nextStaleRewardCleanup = 0f;
                    Log("returned from Arena gameplay; reset page-cycle gates");
                }
            }
            if (Time.unscaledTime >= _nextHeartbeatLog)
            {
                _nextHeartbeatLog = Time.unscaledTime + 10f;
                Log("enabled, scene=" + mode + ", next=" + scene.GetNextMode());
            }
            if (mode == SceneMgr.Mode.LOGIN)
            {
                // Hearthstone can leave the post-login Welcome Quests
                // attention grabber above the hub.  It keeps SceneMgr in LOGIN
                // until the native click catcher is dismissed, so close that
                // native page before requesting the Arena draft scene.
                var welcomeQuests = WelcomeQuests.Get();
                if (!_loginIntroDismissed && welcomeQuests != null)
                {
                    WelcomeQuests.Hide();
                    _loginIntroDismissed = true;
                    Log("closed native Welcome Quests page before Arena loop");
                }
                // LOGIN also covers authentication/reconnect failures. Let
                // LoginManager confirm its own transition; elapsed time and
                // a responsive local API do not prove an authenticated session.
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, "LOGIN", null);
                return;
            }
            _loginIntroDismissed = false;
            // Journal, connection prompts and confirmation panels use the
            // same native popup infrastructure. Keep it available; reviewed
            // notices are handled individually above.
            PopupDisplayManager.SuppressPopupsTemporarily = false;
            if (mode == SceneMgr.Mode.HUB && !_requestedDraft)
            {
                if (!controllerStopAfterGame && ArenaSettings.UseBoxScores.Value &&
                    (ArenaSettings.AutoDraft.Value || ArenaSettings.AutoRedraft.Value || ArenaSettings.AutoPlay.Value) &&
                    !IsBoxReadyForArena())
                {
                    if (Time.unscaledTime >= _nextArenaReadyLog)
                    {
                        _nextArenaReadyLog = Time.unscaledTime + 5f;
                        Log("waiting for Box recommended-choice capture/heartbeat before opening Arena draft (" + GetBoxReadinessDiagnostic() + ")");
                    }
                    s_arenaOverlayPhase = "等待盒子推荐采集就绪";
                    PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, "WAITING_BOX", null);
                    return;
                }
                // The first post-login Hub can render before the Arena season
                // response has populated DraftManager. Loading DRAFT during
                // that gap makes the native ArenaLandingPageManager throw and
                // leaves the client on the ordinary mode menu. Wait for the
                // same native data the Arena screen requires, then enter it.
                var hubDraftManager = DraftManager.Get();
                if (hubDraftManager == null || hubDraftManager.CurrentSeason == null || Network.Get() == null)
                {
                    if (Time.unscaledTime >= _nextArenaReadyLog)
                    {
                        _nextArenaReadyLog = Time.unscaledTime + 5f;
                        Log("waiting for native Arena season/network data before opening Draft");
                    }
                    return;
                }
                if (!PrepareNativeArenaModeBeforeEntry(hubDraftManager))
                {
                    if (!TryCompleteNavigationStop(hubDraftManager))
                        PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, "STOP_PENDING", hubDraftManager);
                    return;
                }
                _requestedDraft = true;
                Log("requesting Draft scene from Hub");
                scene.SetNextMode(SceneMgr.Mode.DRAFT);
                return;
            }
            if (mode == SceneMgr.Mode.GAMEPLAY)
            {
                if (!arenaGameplay)
                {
                    _boxExecutorRecoveryAttempted = false;
                    _nextBoxExecutorRecoveryCheck = 0f;
                    if (_arenaAutomationEnabled) SetArenaAutomation(false);
                    try { PopupDisplayManager.SuppressPopupsTemporarily = false; } catch { }
                    s_arenaOverlayPhase = "普通对战（竞技场脚本未接管）";
                    PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, "", null);
                    return;
                }
                TrackCurrentArenaGame();
                _gamesThisRun = ArenaStatsStore.Data.AutomaticGamesInRun;
                UpdateArenaBudgetOverlay();
                // Reuse HsMod's existing Box recommendation executor only after
                // Hearthstone has actually entered an Arena game. This avoids
                // enabling the normal constructed-ladder queue.
                if (ArenaSettings.AutoPlay.Value && !IsBoxReadyForArena(false))
                {
                    if (_arenaAutomationEnabled) SetArenaAutomation(false);
                    s_arenaOverlayPhase = "等待盒子就绪";
                    if (Time.unscaledTime >= _nextBoxExecutorRecoveryCheck)
                    {
                        _nextBoxExecutorRecoveryCheck = Time.unscaledTime + 5f;
                        Log("Arena gameplay is waiting for the Box file source and a fresh Box heartbeat (" + GetBoxReadinessDiagnostic(false) + ")");
                    }
                    PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, true, "GAMEPLAY", null);
                    return;
                }
                SetArenaAutomation(ArenaSettings.AutoPlay.Value);
                if (ArenaSettings.AutoPlay.Value)
                {
                    var executorStatus = HsModNativeArenaPatch.ReadExecutorStatus();
                    if (!string.IsNullOrWhiteSpace(executorStatus)) s_arenaOverlayPhase = executorStatus;
                }
                if (ArenaSettings.AutoPlay.Value) TryRecoverBoxExecutor();
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, true, "GAMEPLAY", null);
                return;
            }
            _boxExecutorRecoveryAttempted = false;
            _nextBoxExecutorRecoveryCheck = 0f;
            if (_arenaAutomationEnabled) SetArenaAutomation(false);
            if (mode != SceneMgr.Mode.DRAFT)
            {
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, s_arenaOverlayPhase, null);
                return;
            }
            var manager = DraftManager.Get();
            if (manager == null)
            {
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, "", null);
                return;
            }
            // The server sends the normal/underground ticket price in the
            // native landing-page model.  Read it before every budget check so
            // a seasonal pricing change cannot make the controller stop too
            // early or spend against a stale hard-coded count.
            ArenaSettings.RefreshTicketCostFromNative();
            UpdateArenaResourceOverlay(manager);
            if (manager.CurrentSeason == null)
            {
                if (Time.unscaledTime >= _nextArenaReadyLog)
                {
                    _nextArenaReadyLog = Time.unscaledTime + 5f;
                    Log("Draft scene waiting for native Arena season data");
                }
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, "INITIALIZING", manager);
                return;
            }
            if (!EnsureNativeArenaModePage(manager))
            {
                if (!TryCompleteNavigationStop(manager))
                    PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false,
                        _runStopRequested ? "STOP_PENDING" : "MODE_NAVIGATION", manager);
                return;
            }
            var arenaState = manager.GetArenaState(ArenaSettings.IsUnderground).ToString();
            _lifecycle.Observe(arenaState, Time.unscaledTime);
            PollNativeArenaResults(manager);
            PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, arenaState, manager);
            UpdateArenaBudgetOverlay();
            // A game-server socket can be accepted and then closed before the
            // client receives the handshake packet.  The native HsMod state
            // remains SERVER_GAME_CONNECTING in that case, so the old latch
            // would leave Arena parked on DRAFT forever.  Cancel only after a
            // bounded wait and leave a short backoff before trying the same
            // native queue again.
            if (_matchmakingRequested && _matchmakingRequestedAt >= 0f &&
                Time.unscaledTime - _matchmakingRequestedAt >= 60f &&
                SceneMgr.Get()?.GetMode() != SceneMgr.Mode.GAMEPLAY)
            {
                try
                {
                    var network = Network.Get();
                    if (network != null && network.IsConnectedToGameServer())
                    {
                        network.DisconnectFromGameServer(Network.DisconnectReason.Network_Unreachable);
                        Log("Arena matchmaking timeout recovery requested native game-server disconnect");
                    }
                    GameMgr.Get()?.CancelFindGame();
                    Log("Arena matchmaking handshake timed out after 60 seconds; cancelled native queue and will retry");
                }
                catch (Exception ex)
                {
                    Log("Arena matchmaking timeout recovery failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                _matchmakingRequested = false;
                _matchmakingRequestedAt = -1f;
                // CancelFindGame can leave the game-server socket in its
                // disconnecting phase for more than one minute.  Give the
                // client time to finish that teardown before issuing another
                // FindGame call; otherwise the native GameMgr rejects the
                // retry as "already waiting for a game to start".
                _nextMatchRetryAt = Time.unscaledTime + 90f;
            }
            // Defer the controller stop marker until after the state-transition
            // block below.  Returning here used to skip NO_RUN statistics
            // finalization when the controller stopped immediately after the
            // reward acknowledgement.
            var controllerStopAfterReward = File.Exists(ControllerStopAfterGamePath) && arenaState == "NO_RUN";
            var rewardState = arenaState == "REWARDS" || arenaState == "IN_REWARDS";
            // A final game can enter rewards directly. Latch the limit now;
            // the reward action gate below still lets its receipt complete.
            var budgetDecision = RequestGameLimitStopIfNeeded();
            // A stop request is a boundary request, not permission to turn
            // off HsMod immediately.  The current game must finish and a
            // reward page must be acknowledged before the native runtime is
            // released.  The action gate is applied after the state
            // transition below so NO_RUN can close stats and consume markers.
            var stopBlocksFurtherActions = ArenaStopPolicy.BlocksFurtherActions(_runStopRequested, rewardState);
            // A partially initialized client can report IsActive=true while
            // its session limits are still 0/0. Treat that placeholder as no
            // usable run so the landing page can recover into ticket purchase
            // instead of waiting forever with an empty deck.
            var nativeRunActive = manager.HasCurrentlyActiveRun(ArenaSettings.IsUnderground);
            var controllerStopAtDraftBoundary = File.Exists(ControllerStopAfterGamePath) &&
                ArenaStopPolicy.CanConsumeControllerMarker(modeName, scene.GetNextMode().ToString(),
                    scene.IsTransitioning(), true, arenaState, nativeRunActive);
            stopBlocksFurtherActions |= controllerStopAtDraftBoundary;
            if (TryGetVerifiedNativeSession(manager, ArenaSettings.IsUnderground, out var observedSession) &&
                manager.IsUnderground() == ArenaSettings.IsUnderground)
            {
                // The current native client uses SessionState, not the legacy
                // IsInRewards bit, to represent a completed Arena session.
                // Only matching explicit NO_RUN states can acknowledge empty.
                var serverInRewards = ArenaSessionStatePolicy.BlocksEmptySessionFromRewards(
                    manager.GetArenaState(ArenaSettings.IsUnderground).ToString(), observedSession.State.ToString(),
                    observedSession.HasIsInRewards && observedSession.IsInRewards);
                var chestReady = manager.IsRewardChestReady();
                if (_lifecycle.CanRetireMissingRun(observedSession.State.ToString(), true, observedSession.IsActive,
                        serverInRewards, chestReady, Time.unscaledTime) && ArenaStatsLedger.RetireMissingRun(ArenaStatsStore.Data))
                {
                    ArenaStatsStore.Save();
                    s_arenaOverlayStats = ArenaStatsStore.Summary();
                    Log("Arena stale run detached after stable server NO_RUN: key=" + ArenaStatsStore.Data.RunKey +
                        "; no reward or lifetime history added");
                }
                if (_lifecycle.CanObserveEmptyRun(observedSession.State.ToString(), true, observedSession.IsActive,
                        serverInRewards, chestReady, Time.unscaledTime) &&
                    ArenaStatsLedger.ObserveEmptyRun(ArenaStatsStore.Data,
                        GetNativeRunKey(manager, ArenaSettings.IsUnderground), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                    ArenaStatsStore.Save();
            }
            else _lifecycle.ResetNativeEvidence();
            if (!ObserveAutomaticArenaEntry(manager, arenaState) && !_runStopRequested)
            {
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, arenaState, manager);
                return;
            }
            // Receipt failures use the ordinary safe-boundary shutdown below.
            // Returning here after RequestArenaStop would leave HsMod enabled
            // forever; refresh the action gate after a newly requested stop.
            stopBlocksFurtherActions = ArenaStopPolicy.BlocksFurtherActions(_runStopRequested, rewardState) || controllerStopAtDraftBoundary;
            var undergroundRunActive = nativeRunActive && manager.GetMaxSlot() > 0 &&
                                       (manager.GetSlot() > 0 ||
                                        arenaState == "MIDRUN" ||
                                        arenaState == "MIDRUN_REDRAFT_PENDING" ||
                                        arenaState == "REDRAFTING" ||
                                        arenaState == "DRAFTING");
            var newUndergroundRunNeeded = !undergroundRunActive &&
                (arenaState == "NO_RUN" || arenaState == "INVALID" || arenaState == "EDITING_DECK");
            // Do not treat a placeholder DRAFTING or momentary NO_RUN as a
            // purchased run. The first accepted draft slot (or a resumed
            // playable deck) confirms entry; rewards confirm its end.
            if (_lifecycle.CanConfirmRun(arenaState, nativeRunActive,
                manager.GetSlot(), manager.GetMaxSlot(), Time.unscaledTime))
            {
                StartArenaRunStats(manager, arenaState);
                _lifecycle.ConfirmedRun();
            }
            // Disabled manual play can create another native identity. Recheck
            // its budget after the existing stable attachment, before queueing.
            budgetDecision = RequestGameLimitStopIfNeeded();
            stopBlocksFurtherActions = ArenaStopPolicy.BlocksFurtherActions(_runStopRequested, rewardState) || controllerStopAtDraftBoundary;
            if (!stopBlocksFurtherActions && !rewardState && budgetDecision == ArenaRunBudgetDecision.WaitForIdentity)
            {
                if (Time.unscaledTime >= _nextBudgetIdentityDiagnostic)
                {
                    _nextBudgetIdentityDiagnostic = Time.unscaledTime + 10f;
                    Log("Arena budget waiting for native run identity: tracked=" + ArenaStatsStore.Data.RunKey +
                        "; native=" + GetNativeRunKey(manager, ArenaSettings.IsUnderground) + "; state=" + arenaState +
                        "; games=" + ArenaStatsStore.Data.AutomaticGamesInRun + "; limit=" + ArenaSettings.MaxGamesPerRun.Value);
                }
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, arenaState, manager);
                return;
            }
            if (ArenaStatsStore.Data.RunActive && !ArenaStatsStore.Data.RewardPending &&
                _lifecycle.CanDiscardLegacyPlaceholder(arenaState, nativeRunActive, ArenaStatsStore.Data.RunKey, Time.unscaledTime))
            {
                ArenaStatsLedger.DiscardLegacyPlaceholder(ArenaStatsStore.Data);
                ArenaStatsStore.Save();
                Log("Legacy Arena run marker had no server deck identity and NO_RUN persisted 30s; cleared marker, retained historical counters");
            }
            // A reached game limit already owns the stop reason. Do not run
            // next-entry resource checks after the reward ACK and replace it
            // with an unrelated ticket-shortage stop.
            if (!stopBlocksFurtherActions && newUndergroundRunNeeded && CanBeginArenaRun() &&
                !CanAttemptTicketPurchase(manager))
                stopBlocksFurtherActions = _runStopRequested;
            var arenaStateChanged = !string.Equals(arenaState, _lastArenaState, StringComparison.Ordinal);
            if (arenaStateChanged)
            {
                var rewardStateChanged = arenaState == "REWARDS" || arenaState == "IN_REWARDS";
                if (rewardStateChanged) ArenaStatsStore.MarkRewardPending();
                _lastArenaState = arenaState;
                Log("Arena state=" + arenaState + " slot=" + manager.GetSlot() + "/" + manager.GetMaxSlot());

                // These gates belong to one native run.  A completed Arena game
                // returns to the same Draft scene for redraft or a new ticket,
                // so leaving them latched would strand the next run at the deck
                // editor after the first game.
                if (arenaState == "MIDRUN_REDRAFT_PENDING" || arenaState == "REDRAFTING")
                {
                    _matchmakingRequested = false;
                    _requestedChoices = false;
                }
                else if (arenaState == "DRAFTING")
                {
                    _requestedChoices = false;
                }
                else if (arenaState == "NO_RUN")
                {
                    // A natural completed run starts a fresh budget.  If the
                    // controller explicitly stopped for the configured game
                    // limit or for missing resources, keep that stop latch
                    // through the reward acknowledgement and NO_RUN page so
                    // it cannot immediately buy another ticket.
                    var keepStop = _runStopRequested || ArenaStatsStore.Data.RunActive;
                    if (!keepStop)
                    {
                        _gamesThisRun = 0;
                        _runStopReason = "";
                        s_arenaOverlayStopReason = "";
                    }
                    _matchmakingRequested = false;
                    _requestedChoices = false;
                    _rewardFlowStarted = false;
                    _rewardAckRequested = false;
                    _rewardUiWasPending = false;
                    _rewardNativeDoneRequestedAt = -1f;
                    _redraftPageStarted = false;
                    _redraftPageStartedAt = -1f;
                    _nextRedraftDiagnostic = 0f;
                    UpdateArenaBudgetOverlay();
                }
            }

            // The native run is now known to be outside gameplay/rewards.
            // Consume either controller or plugin stop markers only at this
            // safe boundary.  For a live run (MIDRUN/redraft/editing) leave
            // the page visible and block all new choices/matchmaking.
            if (TryFinishControllerRunStop(scene, manager, arenaState)) return;
            stopBlocksFurtherActions |= ArenaStopPolicy.BlocksFurtherActions(_runStopRequested, rewardState);
            if (stopBlocksFurtherActions || controllerStopAfterReward)
            {
                if (_arenaAutomationEnabled) SetArenaAutomation(false);
                var safeBoundary = ArenaStopPolicy.IsSafeBoundary(arenaState, nativeRunActive);
                if (!safeBoundary) return;

                // NO_RUN can arrive one tick after ACK, before the existing
                // three-second native receipt proof has persisted completion.
                // Keep that observer alive and publish an unsafe stop phase;
                // otherwise the controller closes the game with stale rewards.
                if (ArenaRewardStopGate.MustWaitForReceipt(arenaState, ArenaStatsStore.Data.RewardPending))
                {
                    if (Time.unscaledTime >= _nextArenaReadyLog)
                    {
                        _nextArenaReadyLog = Time.unscaledTime + 5f;
                        Log("Arena stop waiting for confirmed native reward receipt; run=" + ArenaStatsStore.Data.RunKey);
                    }
                    PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false,
                        ArenaRewardStopGate.PendingState, manager);
                    return;
                }

                // Publish a cause for the already confirmed marker stop so
                // the controller can honor CloseGameAfterStop. F12 OFF has
                // no marker and retains its existing/manual cause.
                _runStopReason = ControllerMarkerStopReason.Resolve(controllerStopAfterReward,
                    controllerStopAtDraftBoundary, _runStopReason);
                HsModNativeArena.SetRuntimeEnabled(false);
                s_arenaOverlayStopReason = controllerStopAfterReward
                    ? "控制端已在奖励结算后停止"
                    : (_runStopReason ?? "竞技场流程已停止");
                try { File.Delete(EnablePath); } catch { }
                try { File.Delete(InternalStopPath); } catch { }
                try { File.Delete(ControllerStopAfterGamePath); } catch { }
                Log(controllerStopAfterReward
                    ? "controller stop-after-game request completed after Arena rewards"
                    : controllerStopAtDraftBoundary ? "controller stop-after-game request completed at a bound Arena page"
                    : "Arena stop request completed at a safe boundary");
                PublishRuntimeState(arenaModeSelected, false, modeName, false, "STOPPED", manager);
                return;
            }

            // Transient NO_RUN must not reopen the shop, respend a ticket or
            // submit a cached hero. Keep the entry request latched through
            // its acknowledgement window and keep a confirmed run until a
            // reward acknowledgement actually closes it.
            if (newUndergroundRunNeeded && !CanBeginArenaRun())
                return;

            if (arenaState != "MIDRUN_REDRAFT_PENDING" && arenaState != "REDRAFTING")
            {
                _redraftPageStarted = false;
                _redraftPageStartedAt = -1f;
            }

            var redraftClientState = manager.GetCurrentClientState().ToString();
            var shouldOpenRedraftPage = ArenaRedraftPolicy.ShouldOpenPage(arenaState, redraftClientState);

            // TriggerRedraft is asynchronous.  A few client builds accept the
            // click but drop the first request while the landing page is still
            // binding its deck.  Retry after a bounded window instead of
            // leaving the run permanently parked on the old deck.  The
            // diagnostic includes the native slot and choice count so the
            // developer log can distinguish a dropped click from a server wait.
            if (shouldOpenRedraftPage &&
                _redraftPageStarted && _redraftPageStartedAt >= 0f &&
                Time.unscaledTime - _redraftPageStartedAt >= 8f)
            {
                _redraftPageStarted = false;
                _redraftPageStartedAt = -1f;
                Log("redraft TriggerRedraft timed out after 8s; allowing a native retry");
            }

            if ((arenaState == "MIDRUN_REDRAFT_PENDING" || arenaState == "REDRAFTING") &&
                Time.unscaledTime >= _nextRedraftDiagnostic)
            {
                _nextRedraftDiagnostic = Time.unscaledTime + 5f;
                try
                {
                    var diagDisplay = GetDraftDisplay();
                    EnsureChoiceReflection(diagDisplay);
                    var diagChoices = diagDisplay != null ? s_choicesField?.GetValue(diagDisplay) as IList : null;
                    Log("redraft diagnostic: state=" + arenaState +
                        ", isRedrafting=" + manager.IsRedrafting() +
                        ", slot=" + manager.GetRedraftSlot() + "/" + manager.GetMaxRedraftSlot() +
                        ", choices=" + (diagChoices == null ? "null" : diagChoices.Count.ToString()) +
                        ", client=" + redraftClientState +
                        ", inDrafting=" + manager.IsClientStateInAnyDrafting() +
                        ", triggerStarted=" + _redraftPageStarted);
                }
                catch (Exception ex)
                {
                    Log("redraft diagnostic failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // After a loss, the server first exposes MIDRUN_REDRAFT_PENDING
            // while the landing page still shows the old deck.  The native
            // Play button calls DraftDisplay.TriggerRedraft(), which opens the
            // actual redraft choices and requests them from the server. A
            // restored REDRAFTING run can already have all three choices while
            // remaining on the landing page: choices.Count cannot certify that
            // the client entered redrafting. Call the same native entry point.
            if (shouldOpenRedraftPage && !_redraftPageStarted &&
                ArenaSettings.AutoDraft.Value && ArenaSettings.AutoRedraft.Value)
            {
                var pendingDisplay = GetDraftDisplay();
                if (pendingDisplay != null && pendingDisplay == DraftDisplay.Get() &&
                    pendingDisplay.IsLoadingComplete() && !pendingDisplay.IsRewardsFlowPending &&
                    !GameUtils.IsAnyTransitionActive())
                {
                    if (Network.Get() == null)
                    {
                        Log("redraft pending; native network is not ready yet");
                    }
                    else
                    {
                        try
                        {
                            _redraftPageStarted = true;
                            _redraftPageStartedAt = Time.unscaledTime;
                            ResetDraftSelection();
                            Log("redraft pending; opening native TriggerRedraft from client=" + redraftClientState +
                                "; cached choices do not imply an open selection page");
                            pendingDisplay.TriggerRedraft(false);
                            // TriggerRedraft(false) already requests choices;
                            // allow its native page transition to run first.
                            _requestedChoices = true;
                            return;
                        }
                        catch (Exception ex)
                        {
                            _redraftPageStarted = false;
                            _redraftPageStartedAt = -1f;
                            Log("native TriggerRedraft not ready: " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }
            }

            // The account can arrive with a completed run waiting for rewards.
            // Let the game's own reward flow acknowledge it before buying the
            // next ticket; otherwise RequestDraftBegin is rejected as "run in progress".
            if (arenaState == "REWARDS" || arenaState == "IN_REWARDS")
            {
                EnableHsModRewardAutomation();
                var rewardDisplay = GetDraftDisplay();
                // Start the grace clock even when the display singleton is
                // still being instantiated. This leaves time for the native
                // reward widget to appear before the headless ACK fallback.
                if (_rewardFlowStartedAt < 0f)
                    _rewardFlowStartedAt = Time.unscaledTime;
                // The landing page can start the native reward animation in
                // the same frame that the controller first observes REWARDS.
                // Mark that flow as started even when IsRewardsFlowPending is
                // already true; otherwise the timeout fallback never arms and
                // a box with auto-open disabled can strand the run forever.
                if (rewardDisplay != null && !_rewardFlowStarted)
                {
                    _rewardFlowStarted = true;
                    _rewardFlowStartedAt = Time.unscaledTime;
                    if (rewardDisplay.IsRewardsFlowPending)
                    {
                        Log("existing Arena run is in rewards; native reward flow already pending");
                    }
                    else
                    {
                        Log("existing Arena run is in rewards; opening native reward flow");
                        rewardDisplay.ShowRewardsIfNecessary();
                    }
                }
                // On a cold login the session state can arrive before the
                // DraftRetired/DraftChoicesAndContents packet that contains
                // the actual chest.  ShowRewardsIfNecessary() waits for that
                // packet, so periodically ask the native endpoint for the
                // current choices/chest instead of leaving the page in
                // REWARDS forever.
                if (!manager.IsRewardChestReady() && Time.unscaledTime >= _nextRewardRefresh)
                {
                    _nextRewardRefresh = Time.unscaledTime + 5f;
                    try
                    {
                        var rewardNetwork = Network.Get();
                        if (rewardNetwork != null)
                        {
                            rewardNetwork.RequestDraftChoicesAndContents(ArenaSettings.IsUnderground);
                            Log("Arena rewards chest not ready; requested native DraftChoicesAndContents refresh");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Arena rewards refresh not ready: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                // Some client builds render the reward chest without exposing a
                // DraftDisplay singleton. Once the server has supplied the
                // chest, acknowledge it through the same native network call the
                // reward widget uses, then let the server move the run to NO_RUN.
                var rewardUiPending = rewardDisplay != null && rewardDisplay.IsRewardsFlowPending;
                if (rewardUiPending)
                    _rewardUiWasPending = true;
                // A completed native reward widget already sent its own ACK in
                // OnRewardBoxesDone. Do not send a second ACK during the short
                // window before DraftManager receives that packet; the
                // 60-second timeout below remains the recovery path if the
                // native packet never completes.
                var rewardUiCompleted = _rewardUiWasPending &&
                                        (rewardDisplay == null || !rewardUiPending);
                var rewardUiGraceElapsed = _rewardFlowStartedAt < 0f ||
                                           Time.unscaledTime - _rewardFlowStartedAt >= 8f;
                var rewardUiTimedOut = _rewardFlowStartedAt >= 0f &&
                                       Time.unscaledTime - _rewardFlowStartedAt >= 60f;
                // OnRewardBoxesDone normally transitions the native state in
                // the same packet round trip.  If a client swallowed that
                // callback, do not leave the run locked behind a completed
                // reward widget forever: release the latch after 12 seconds
                // and let the direct ACK/visual cleanup fallback run.
                var rewardNativeDoneTimedOut = _rewardNativeDoneRequestedAt >= 0f &&
                                               Time.unscaledTime - _rewardNativeDoneRequestedAt >= 12f;
                if (rewardNativeDoneTimedOut)
                {
                    _rewardAckRequested = false;
                    _rewardNativeDoneRequestedAt = -1f;
                    Log("native Arena reward Done did not advance state within 12s; retrying with direct ACK fallback");
                }
                if (!_rewardAckRequested && manager.IsRewardChestReady() &&
                    (rewardUiTimedOut ||
                     (rewardUiPending && rewardUiGraceElapsed) ||
                     rewardNativeDoneTimedOut ||
                     (!rewardUiCompleted &&
                      ((rewardDisplay == null && rewardUiGraceElapsed) ||
                       (!rewardUiPending && rewardUiGraceElapsed)))))
                {
                    // GetDraftDeck(selected-mode) is the expected Arena path;
                    // retain the native active-deck fallback for clients that
                    // have not switched m_undergroundActive yet during a cold
                    // login. Never send an ACK with the blank deck (ID 0).
                    var rewardDeck = manager.GetDraftDeck(ArenaSettings.IsUnderground) ?? manager.GetDraftDeck();
                    if (rewardDeck != null && rewardDeck.ID > 0)
                    {
                        // Prefer the exact native Done handler as soon as the
                        // chest has been visible for the normal eight-second
                        // grace period.  Waiting for the old 60-second
                        // timeout left the next draft hidden behind the chest.
                        if (rewardUiPending && rewardUiGraceElapsed &&
                            TryFinishNativeRewardUi(rewardDisplay, forceCloseAnimation: true))
                        {
                            _rewardAckRequested = true;
                            _rewardNativeDoneRequestedAt = Time.unscaledTime;
                            Log("native Arena reward UI completed before timeout; continuing to next Arena page");
                            return;
                        }
                        _rewardAckRequested = true;
                        try
                        {
                            Log(rewardUiPending
                                ? "native reward Done unavailable; using direct Arena reward ACK fallback"
                                : "reward display unavailable; acknowledging native Arena rewards");
                            var rewardNetwork = Network.Get();
                            if (rewardNetwork == null)
                            {
                                _rewardAckRequested = false;
                                _rewardNativeDoneRequestedAt = -1f;
                            }
                            else
                            {
                                try
                                {
                                    rewardNetwork.AckDraftRewards(rewardDeck.ID, manager.GetSlot(), ArenaSettings.IsUnderground);
                                    Log("direct Arena reward ACK sent");
                                }
                                catch (Exception ex)
                                {
                                    _rewardAckRequested = false;
                                    _rewardNativeDoneRequestedAt = -1f;
                                    Log("native reward ACK failed; will retry: " + ex.GetType().Name + ": " + ex.Message);
                                }
                                if (_rewardAckRequested && rewardDisplay != null && rewardDisplay.IsRewardsFlowPending)
                                {
                                    try
                                    {
                                        // This path already sent the ACK
                                        // directly, so only clean up the
                                        // visual object here; do not call
                                        // OnRewardBoxesDone a second time.
                                        s_rewardBoxesDisplayField ??= typeof(DraftDisplay).GetField("m_rewardBoxesDisplay", InstancePrivate);
                                        var rewardBoxes = s_rewardBoxesDisplayField?.GetValue(rewardDisplay);
                                        s_showAlreadyOpenedRewardsMethod ??= rewardBoxes?.GetType().GetMethod("ShowAlreadyOpenedRewards", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        s_showAlreadyOpenedRewardsMethod?.Invoke(rewardBoxes, null);
                                        rewardDisplay.OnOpenRewardsComplete();
                                        TryKillRewardObjects(rewardDisplay, rewardBoxes);
                                        TryDestroyRewardsWidget(rewardDisplay);
                                        Log("cleaned stale Arena reward chest UI after direct ACK");
                                    }
                                    catch (Exception ex)
                                    {
                                        Log("reward UI cleanup failed after direct ACK: " + ex.GetType().Name + ": " + ex.Message);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _rewardAckRequested = false;
                            _rewardNativeDoneRequestedAt = -1f;
                            Log("native reward ACK transport not ready; will retry: " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                    else if (Time.unscaledTime >= _nextChoiceDiag)
                    {
                        _nextChoiceDiag = Time.unscaledTime + 5f;
                        Log("reward chest is ready but no valid draft deck ID is available; delaying ACK");
                    }
                }
                return;
            }

            if (arenaState == "NO_RUN")
            {
                RestoreHsModRewardAutomation();
                _rewardFlowStarted = false;
                _rewardAckRequested = false;
                _rewardUiWasPending = false;
                _rewardFlowStartedAt = -1f;
                _rewardNativeDoneRequestedAt = -1f;
                _nextStaleRewardCleanup = 0f;
            }

            if (!EnsureNativeDraftSelectionEntry(manager, arenaState))
            {
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, arenaState, manager);
                return;
            }
            if (newUndergroundRunNeeded && arenaState == "NO_RUN") PrepareRemainingTicketPurchase(manager);
            if (_lifecycle.CanSelect(arenaState, nativeRunActive, manager.GetMaxSlot(), Time.unscaledTime) && !_requestedChoices)
            {
                var choiceNetwork = Network.Get();
                if (choiceNetwork != null)
                {
                    choiceNetwork.RequestDraftChoicesAndContents(ArenaSettings.IsUnderground);
                    _requestedChoices = true;
                    Log("Arena state=" + arenaState + "; requested current draft choices");
                }
                else
                {
                    Log("Arena state=" + arenaState + "; native network is not ready");
                }
            }
            else if (newUndergroundRunNeeded && manager.GetNumTicketsOwned() < ArenaSettings.TicketCost && !_ticketPurchaseRequested)
            {
                if (!CanAttemptTicketPurchase(manager)) return;
                var ticketDisplay = UnityEngine.Object.FindObjectOfType<TavernTicketDisplay>();
                if (ticketDisplay != null)
                {
                    if (_ticketPurchaseStartedAt < 0f) _ticketPurchaseStartedAt = Time.unscaledTime;
                    ticketDisplay.OpenTavernTicketProductPage(true);
                    _ticketPurchaseRequested = true;
                    Log((ArenaSettings.IsUnderground ? "underground" : "normal") + " Arena needs " + ArenaSettings.TicketCost + " ticket(s); opened native ticket product page");
                }
                else if (Time.unscaledTime >= _nextTicketProductRetry)
                {
                    _nextTicketProductRetry = Time.unscaledTime + 5f;
                    Log("Arena page is still loading its native ticket display; waiting before purchase");
                }
            }
            if (newUndergroundRunNeeded && manager.GetNumTicketsOwned() < ArenaSettings.TicketCost && _ticketPurchaseRequested && !_ticketPurchaseClicked)
            {
                if (!CanAttemptTicketPurchase(manager)) return;
                if (_ticketPurchaseStartedAt >= 0f &&
                    Time.unscaledTime - _ticketPurchaseStartedAt >= TicketPurchaseTimeoutSeconds)
                {
                    if (ArenaSettings.StopOnTicketShortage.Value)
                    {
                        RequestArenaStop("门票购买页面超时，已停止自动流程");
                        return;
                    }
                    _ticketPurchaseStartedAt = Time.unscaledTime;
                    Log("ticket purchase page timed out; retrying because ticket-shortage stop is disabled");
                }
                _ticketPurchaseClicked = TryPurchaseTicketWithGold();
                if (!_ticketPurchaseClicked && Time.unscaledTime >= _nextTicketProductRetry)
                {
                    _nextTicketProductRetry = Time.unscaledTime + 5f;
                    try
                    {
                        var ticketDisplay = UnityEngine.Object.FindObjectOfType<TavernTicketDisplay>();
                        if (ticketDisplay != null)
                        {
                            ticketDisplay.OpenTavernTicketProductPage(true);
                            Log("native ticket product still loading; retried native product page");
                        }
                        else
                        {
                            // SpendTicket can run before the Arena landing
                            // widget finishes binding. Re-enter through the
                            // same native event once its display exists.
                            var retryTicketDisplay = UnityEngine.Object.FindObjectOfType<TavernTicketDisplay>();
                            retryTicketDisplay?.OpenTavernTicketProductPage(true);
                            Log("native ticket display not ready; retried native SPEND_TICKET entry");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("native ticket product retry not ready: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            if (newUndergroundRunNeeded && arenaState == "EDITING_DECK" && Time.unscaledTime >= _nextArenaReadyLog)
            {
                _nextArenaReadyLog = Time.unscaledTime + 5f;
                Log("arena purchase state: tickets=" + manager.GetNumTicketsOwned() +
                    " active=" + undergroundRunActive + " slot=" + manager.GetSlot() + "/" + manager.GetMaxSlot() +
                    " requested=" + _ticketPurchaseRequested + " clicked=" + _ticketPurchaseClicked +
                    " confirmed=" + _ticketPurchaseConfirmed);
            }
            else if (arenaState == "EDITING_DECK" && Time.unscaledTime >= _nextArenaReadyLog)
            {
                _nextArenaReadyLog = Time.unscaledTime + 5f;
                Log("arena session state: tickets=" + manager.GetNumTicketsOwned() +
                    " nativeActive=" + nativeRunActive + " usableActive=" + undergroundRunActive +
                    " slot=" + manager.GetSlot() + "/" + manager.GetMaxSlot() +
                    " requested=" + _ticketPurchaseRequested + " clicked=" + _ticketPurchaseClicked);
            }
            if (newUndergroundRunNeeded && manager.GetNumTicketsOwned() < ArenaSettings.TicketCost && _ticketPurchaseClicked && !_ticketPurchaseConfirmed)
            {
                _ticketPurchaseConfirmed = TryConfirmTicketPurchase();
                if (!_ticketPurchaseConfirmed && _ticketPurchaseStartedAt >= 0f &&
                    Time.unscaledTime - _ticketPurchaseStartedAt >= TicketPurchaseTimeoutSeconds)
                {
                    RequestArenaStop("门票购买结果未确认，已停止以避免重复扣费；请查看金币和门票记录");
                    PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, arenaState, manager);
                    return;
                }
            }
            if (newUndergroundRunNeeded && manager.GetNumTicketsOwned() >= ArenaSettings.TicketCost &&
                CanBeginArenaRun())
            {
                try
                {
                    TryBeginNativeArenaRun(manager, arenaState);
                }
                catch (Exception ex)
                {
                    Log("native Arena draft begin not ready: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // Redraft editing is its own native phase. It must finish and be
            // acknowledged by the server before FindGame, even when an older
            // playable deck still exists under the same main deck ID.
            if (HandleArenaDeckEditing(manager, arenaState))
            {
                PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, arenaState, manager);
                return;
            }
            var undergroundDeckReady = undergroundRunActive && manager.IsUnderground() == ArenaSettings.IsUnderground &&
                                       manager.GetMaxSlot() > 0 && manager.GetSlot() >= manager.GetMaxSlot() &&
                                       manager.GetSlot() > 0 && HasReadyUndergroundDeck(manager);
            if (budgetDecision == ArenaRunBudgetDecision.Ready && ArenaSettings.AutoMatch.Value && !_matchmakingRequested && Time.unscaledTime >= _nextMatchRetryAt && undergroundDeckReady && arenaState == "MIDRUN")
            {
                if (ArenaSettings.AutoPlay.Value && !IsBoxReadyForArena(false))
                {
                    s_arenaOverlayPhase = "等待盒子就绪后匹配";
                    if (Time.unscaledTime >= _nextBoxExecutorRecoveryCheck)
                    {
                        _nextBoxExecutorRecoveryCheck = Time.unscaledTime + 5f;
                        Log("Arena matchmaking held until the Box file source and heartbeat are ready (" + GetBoxReadinessDiagnostic(false) + ")");
                    }
                    PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, arenaState, manager);
                    return;
                }
                _matchmakingRequested = true;
                _matchmakingRequestedAt = Time.unscaledTime;
                try
                {
                    var findDeck = manager.GetDraftDeck(ArenaSettings.IsUnderground);
                    Log("Arena deck complete; requesting native CODE_FIND_GAME matchmaking from " + arenaState +
                        " deckId=" + (findDeck?.ID.ToString() ?? "null") +
                        " seasonId=" + manager.CurrentSeasonId +
                        " gameType=" + (ArenaSettings.IsUnderground ? "GT_UNDERGROUND_ARENA" : "GT_ARENA"));
                }
                catch (Exception ex)
                {
                    Log("Arena matchmaking parameter diagnostics failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                try
                {
                    manager.FindGame();
                }
                catch (Exception ex)
                {
                    _matchmakingRequested = false;
                    _matchmakingRequestedAt = -1f;
                    _nextMatchRetryAt = Time.unscaledTime + 10f;
                    Log("native Arena matchmaking not ready: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            else if (!ArenaSettings.AutoMatch.Value && !_matchmakingRequested &&
                     (arenaState == "EDITING_DECK" || arenaState == "MIDRUN"))
            {
                s_arenaOverlayPhase = "自动匹配已关闭，等待手动开始对局";
            }
            else if (ArenaSettings.AutoMatch.Value && !undergroundDeckReady && !_matchmakingRequested &&
                     (arenaState == "EDITING_DECK" || arenaState == "MIDRUN") &&
                     Time.unscaledTime >= _nextArenaReadyLog)
            {
                _nextArenaReadyLog = Time.unscaledTime + 5f;
                Log("Arena waiting for native session/deck data before matchmaking; slot=" + manager.GetSlot() + "/" + manager.GetMaxSlot());
                try
                {
                    var deck = manager.GetDraftDeck(ArenaSettings.IsUnderground);
                    Log("Arena deck readiness: active=" + undergroundRunActive +
                        " nativeMode=" + manager.IsUnderground() +
                        " slots=" + (deck?.GetSlots()?.Count ?? -1) +
                        " ready=" + (undergroundRunActive && HasReadyUndergroundDeck(manager)));
                }
                catch (Exception ex)
                {
                    Log("Arena deck readiness diagnostics failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (Time.unscaledTime >= _nextDraftCheck)
            {
                _nextDraftCheck = Time.unscaledTime + 0.25f;
                TryCloseStaleRewardUi(manager, arenaState);
                if (ArenaSettings.AutoDraft.Value &&
                    _lifecycle.CanSelect(arenaState, nativeRunActive, manager.GetMaxSlot(), Time.unscaledTime) &&
                    (!manager.IsRedrafting() || ArenaSettings.AutoRedraft.Value))
                    TryDraftChoice(manager);
            }
            PublishRuntimeState(arenaModeSelected, nativeArenaRunning, modeName, false, arenaState, manager);
        }
        catch (Exception ex)
        {
            Log("controller exception: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool HasConfirmedPendingRewardReceipt(DraftManager manager)
    {
        var scene = SceneMgr.Get();
        var data = ArenaStatsStore.Data;
        var nativeReady = manager != null && manager.CurrentSeason != null &&
            manager.IsUnderground() == data.RunUnderground;
        return _rewardReceipt.Observe((data.RunUnderground ? "underground:" : "normal:") + data.RunKey,
            data.RewardPending, scene?.GetMode().ToString() ?? "UNKNOWN",
            scene?.GetNextMode().ToString() ?? "UNKNOWN", nativeReady,
            nativeReady ? manager.GetArenaState(data.RunUnderground).ToString() : "UNKNOWN",
            !nativeReady || manager.HasCurrentlyActiveRun(data.RunUnderground),
            !nativeReady || manager.IsRewardChestReady(), Time.unscaledTime);
    }

    private void PublishDisabledRuntimeState(bool arenaSelected, bool runtimeEnabled)
    {
        // This is a read-only observation. F12 only releases automation;
        // it cannot certify that a live game or reward flow has finished.
        var sceneName = "UNKNOWN";
        var nextScene = "UNKNOWN";
        var nativeState = "UNKNOWN";
        DraftManager manager = null;
        try
        {
            var scene = SceneMgr.Get();
            if (scene != null)
            {
                sceneName = scene.GetMode().ToString();
                nextScene = scene.GetNextMode().ToString();
            }
        }
        catch { }
        try
        {
            manager = DraftManager.Get();
            if (manager != null && manager.CurrentSeason != null)
                nativeState = manager.GetArenaState(ArenaStatsStore.Data.RunUnderground).ToString();
        }
        catch { }
        var observation = ArenaDisabledRuntimePolicy.Observe(sceneName, nextScene, nativeState);
        PublishRuntimeState(arenaSelected, runtimeEnabled, observation.Scene,
            observation.InGameplay, observation.ArenaState, manager);
    }

    /// <summary>
    /// Publishes the authoritative Arena phase for the optional controller.
    /// Logs remain useful for diagnosis, but a controller must never infer
    /// that a game is safe to stop from a stale text line alone.
    /// </summary>
    private void PublishRuntimeState(bool arenaSelected, bool runtimeEnabled, string scene,
        bool inGameplay, string arenaState, DraftManager manager)
    {
        if (Time.unscaledTime < _nextRuntimeStateWrite) return;
        _nextRuntimeStateWrite = Time.unscaledTime + 1f;
        try
        {
            var data = new ArenaRuntimeStateData
            {
                updatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                gameProcessId = s_runStopGameIdentity.Item1,
                gameStartedAtUtcTicks = s_runStopGameIdentity.Item2,
                arenaSelected = arenaSelected,
                runtimeEnabled = runtimeEnabled,
                inGameplay = inGameplay,
                scene = scene ?? "",
                arenaState = arenaState ?? "",
                runActive = ArenaStatsStore.Data != null && ArenaStatsStore.Data.RunActive,
                gamesThisRun = _gamesThisRun,
                stopReason = s_arenaOverlayStopReason ?? "",
                mode = arenaSelected ? "竞技场" : HsModNativeArena.ReadHsModModeText(),
                supportedModes = ArenaModeCatalog.Summary
            };
            data.runKey = ArenaStatsStore.Data?.RunKey ?? "";
            // Observational only: publish the server-populated limits for
            // the selected mode, so manual monitoring need not assume that
            // ordinary and Underground Arena share terminal rules. The
            // native manager initializes these to int.MaxValue before data.
            try
            {
                var limits = manager ?? DraftManager.Get();
                if (arenaSelected && limits?.CurrentSeason != null)
                {
                    var maxWins = limits.GetMaxWins(ArenaSettings.IsUnderground);
                    var maxLosses = limits.GetMaxLosses(ArenaSettings.IsUnderground);
                    if (maxWins > 0 && maxWins < 10000 && maxLosses > 0 && maxLosses < 10000)
                    {
                        data.limitsKnown = true;
                        data.maxWins = maxWins;
                        data.maxLosses = maxLosses;
                    }
                }
            }
            catch { /* Unknown limits never authorize a stop or action. */ }
            if (manager != null)
            {
                try
                {
                    data.slot = manager.GetSlot();
                    data.maxSlot = manager.GetMaxSlot();
                }
                catch { }
            }
            data.progressKey = data.scene + ":" + data.arenaState + ":" + data.runKey + ":" +
                data.slot + "/" + data.maxSlot + ":" + data.gamesThisRun;
            var json = JsonUtility.ToJson(data, false);
            var pending = RuntimeStatePath + ".pending-" + Guid.NewGuid().ToString("N");
            var directory = Path.GetDirectoryName(RuntimeStatePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(pending, json, Encoding.UTF8);
            try
            {
                if (File.Exists(RuntimeStatePath)) File.Replace(pending, RuntimeStatePath, null);
                else File.Move(pending, RuntimeStatePath);
            }
            catch
            {
                File.Copy(pending, RuntimeStatePath, true);
                File.Delete(pending);
            }
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= _nextHeartbeatLog)
                Log("Arena runtime state publish failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool CanBeginArenaRun()
        => !HasControllerRunStop() &&
           (!ArenaSettings.UseBoxScores.Value || IsBoxReadyForArena()) &&
           !ArenaStatsLedger.HasUnresolvedEntry(ArenaStatsStore.Data) &&
           ArenaStatsLedger.OtherUnresolvedFinancialEntry(ArenaStatsStore.Data) == null &&
           _lifecycle.CanRequestBegin(ArenaStatsStore.Data.RunActive, Time.unscaledTime,
            ArenaStatsStore.Data.EntryRequestedAtUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private bool ObserveAutomaticArenaEntry(DraftManager manager, string arenaState)
    {
        if (!string.IsNullOrEmpty(s_entryReceiptWriteFailure))
        {
            RequestArenaStop("入场身份记录未能保存或确认；可能已经花票，禁止重复入场：" + s_entryReceiptWriteFailure);
            return false;
        }
        var data = ArenaStatsStore.Data;
        var otherPending = ArenaStatsLedger.OtherUnresolvedFinancialEntry(data);
        if (otherPending != null)
        {
            RequestArenaStop("另一竞技场模式入场未决，禁止再次花票；mode=" +
                (otherPending.Underground ? "地下" : "普通") + "; phase=" + otherPending.Phase +
                "; nonce=" + otherPending.Nonce + "；请先核对原模式的原生轮次");
            return false;
        }
        if (!ArenaStatsLedger.HasUnresolvedEntry(data)) return true;
        if (!TryGetVerifiedNativeSession(manager, data.RunUnderground, out var session) ||
            manager.IsUnderground() != data.RunUnderground)
        {
            if (!MaintainEntryConfirmationBudget(manager, arenaState)) return false;
            return HoldNativeDraftEntry("入场结果未决，原生会话未加载或模式未确认；phase=" + data.EntryReceipt.Phase +
                "; nonce=" + data.EntryReceipt.Nonce, arenaState, manager.GetCurrentClientState().ToString());
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inRewards = session.HasIsInRewards && session.IsInRewards;
        var key = GetNativeRunKey(manager, data.RunUnderground);
        var receipt = data.EntryReceipt;
        var before = receipt.Copy();
        var entryState = ArenaStatsLedger.EntryDraftState(receipt, key, session.State.ToString(),
            arenaState, session.IsActive, inRewards);
        if (receipt.Phase == "response-seen" && key == receipt.ResponseRunKey &&
            _lifecycle.CanAttachNativeRun(key, entryState, session.Wins, session.Losses,
                session.IsActive, inRewards, Time.unscaledTime) &&
            ArenaStatsLedger.BindEntryResponse(data, key, entryState, session.IsActive, inRewards,
                session.Wins, session.Losses, manager.GetSlot(), manager.GetMaxSlot(), true, now))
        {
            if (!ArenaStatsStore.SaveEntryReceipt(data.RunUnderground, receipt.Copy(), out var failure))
            {
                ArenaStatsLedger.RestoreEntryReceipt(data, data.RunUnderground, before);
                s_entryReceiptWriteFailure = failure;
                RequestArenaStop("入场身份绑定保存失败；可能已经花票，禁止重复入场：" + failure);
                return false;
            }
            Log("Arena automatic entry identity bound: nonce=" + receipt.Nonce + "; key=" + key +
                "; previous=" + receipt.PreviousRunKey + "; requestAt=" + receipt.RequestedAtUnixMs +
                "; previousNative=" + receipt.PreviousNativeRunKey +
                "; responseAt=" + receipt.ResponseAtUnixMs + "; stableNative=true; not yet credited");
        }
        // Resume a paid draft left by older packages without clearing
        // its financial history or manufacturing a missing Begin response.
        if (receipt.Phase == "accepted" && session.HasIsUnderground &&
            session.IsUnderground == data.RunUnderground &&
            ArenaStatsLedger.AttachPendingExistingDraft(data, key, session.State.ToString(), arenaState,
                session.IsActive, inRewards, session.Wins, session.Losses, manager.GetSlot(), manager.GetMaxSlot(),
                _lifecycle.CanAttachNativeRun(key, arenaState, session.Wins, session.Losses,
                    session.IsActive, inRewards, Time.unscaledTime)))
        {
            if (!ArenaStatsStore.SaveEntryReceipt(data.RunUnderground, receipt.Copy(), out var failure))
            {
                s_entryReceiptWriteFailure = failure;
                RequestArenaStop("已有选英雄轮次关联保存失败；禁止再次花票：" + failure);
                return false;
            }
            _lifecycle.ConfirmedRun();
            Log("Arena pending entry resumed as existing draft: nonce=" + receipt.Nonce + "; key=" + key +
                "; sessionState=" + session.State + "; managerState=" + arenaState +
                "; countedNewRun=false; no repeated spending; receiptPreserved=true");
        }
        var empty = _lifecycle.CanRetireMissingRun(session.State.ToString(), true, session.IsActive,
            inRewards, manager.IsRewardChestReady(), Time.unscaledTime);
        before = data.EntryReceipt.Copy();
        if (ArenaStatsLedger.ResolveEntryAsEmpty(data, empty, now))
        {
            if (!ArenaStatsStore.SaveEntryReceipt(data.RunUnderground, data.EntryReceipt.Copy(), out var failure))
            {
                ArenaStatsLedger.RestoreEntryReceipt(data, data.RunUnderground, before);
                data.EntryRequestedAtUnixMs = before.RequestedAtUnixMs;
                s_entryReceiptWriteFailure = failure;
                RequestArenaStop("入场未决记录释放未能保存；禁止重复入场：" + failure);
                return false;
            }
            Log("Arena automatic entry lock released after stable acknowledged NO_RUN: nonce=" + receipt.Nonce +
                "; previous=" + receipt.PreviousRunKey + "; no run count or purchase added");
        }
        if (!MaintainEntryConfirmationBudget(manager, arenaState)) return false;
        if (ArenaStatsLedger.HasUnresolvedEntry(data) && arenaState == "NO_RUN")
            return HoldNativeDraftEntry("等待未决入场的原生响应；未确认前禁止再次花票；phase=" + data.EntryReceipt.Phase +
                "; nonce=" + data.EntryReceipt.Nonce, arenaState, manager.GetCurrentClientState().ToString());
        return true;
    }

    private bool MaintainEntryConfirmationBudget(DraftManager manager, string arenaState)
    {
        var data = ArenaStatsStore.Data;
        if (!ArenaStatsLedger.HasUnresolvedEntry(data)) return true;
        var receipt = data.EntryReceipt;
        var recovery = _entryRecoveryBudget.Observe(receipt, arenaState, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Time.unscaledTime);
        if (recovery == ArenaEntryRecoveryDecision.RequestFreshSession)
        {
            // One read-only confirmation is necessary to distinguish cached
            // NO_RUN from a post-request server response. Do not poll forever.
            manager.RefreshCurrentSessionFromServer();
            Log("Arena unresolved entry requested one fresh native session response; nonce=" + receipt.Nonce +
                "; no repeated spending; awaiting acknowledged NO_RUN");
        }
        if (recovery == ArenaEntryRecoveryDecision.Stop)
        {
            RequestArenaStop("竞技场入场结果仍未确认，已保留防重复锁并停止；可能已花票；phase=" +
                receipt.Phase + "; nonce=" + receipt.Nonce + "；请查看原生轮次与开发者日志");
            return false;
        }
        return true;
    }

    internal static void RecordNativeArenaBegin(DraftManager manager, Network.BeginDraft response)
    {
        if (!string.IsNullOrEmpty(s_entryReceiptWriteFailure) || response == null || response.Session == null ||
            manager == null || manager.IsRedrafting())
        {
            LogNativeArenaBeginReadOnlyDiagnostic("null-context", manager, response);
            return;
        }
        var session = response.Session;
        var underground = manager.IsUnderground();
        // Without the response's explicit mode discriminator a mode switch
        // may have rebound DraftManager; do not guess financial ownership.
        if (!session.HasIsUnderground || session.IsUnderground != underground || !session.HasState ||
            !session.HasIsActive || response.DeckID <= 0)
        {
            LogNativeArenaBeginReadOnlyDiagnostic("explicit-field/mode", manager, response);
            return;
        }
        var key = (underground ? "underground:" : "normal:") + manager.CurrentSeasonId + ":" + response.DeckID;
        if (GetNativeRunKey(manager, underground) != key)
        {
            LogNativeArenaBeginReadOnlyDiagnostic("identity", manager, response);
            return;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var context = underground ? ArenaStatsStore.Data.UndergroundRun : ArenaStatsStore.Data.NormalRun;
        var before = (ArenaStatsStore.Data.RunUnderground == underground ? ArenaStatsStore.Data.EntryReceipt : context.EntryReceipt).Copy();
        if (response.Wins != 0 || !ArenaStatsLedger.ObserveEntryResponse(ArenaStatsStore.Data, underground, key, session.State.ToString(),
            session.IsActive, session.HasIsInRewards && session.IsInRewards, session.Wins, session.Losses,
            manager.GetSlot(), response.MaxSlot, now, manager.GetArenaState(underground).ToString(), response.Heroes?.Count ?? 0))
        {
            LogNativeArenaBeginReadOnlyDiagnostic("ObserveEntryResponse", manager, response, now);
            return;
        }
        var receipt = ArenaStatsStore.Data.RunUnderground == underground ? ArenaStatsStore.Data.EntryReceipt : context.EntryReceipt;
        if (!ArenaStatsStore.SaveEntryReceipt(underground, receipt.Copy(), out var failure))
        {
            ArenaStatsLedger.RestoreEntryReceipt(ArenaStatsStore.Data, underground, before);
            s_entryReceiptWriteFailure = failure;
        }
        Log("Arena native entry response observed: nonce=" + receipt.Nonce + "; key=" + key +
            "; mode=" + (underground ? "underground" : "normal") + "; durable=" + string.IsNullOrEmpty(failure) +
            "; actualBeginResponse=true; sessionState=" + session.State +
            "; managerState=" + manager.GetArenaState(underground) + "; heroes=" + (response.Heroes?.Count ?? 0) +
            "; awaiting stable identity");
    }

    // This helper describes rejected native Begin responses only. It must never
    // request another packet, initialize a context, or participate in admission.
    private static void LogNativeArenaBeginReadOnlyDiagnostic(string gate, DraftManager manager,
        Network.BeginDraft response, long observedAtUnixMs = 0)
    {
        try
        {
            var now = observedAtUnixMs > 0 ? observedAtUnixMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var session = response?.Session;
            var underground = manager != null && manager.IsUnderground();
            var redrafting = manager != null && manager.IsRedrafting();
            var key = manager != null && response != null
                ? (underground ? "underground:" : "normal:") + manager.CurrentSeasonId + ":" + response.DeckID : "";
            var nativeKey = manager != null ? GetNativeRunKey(manager, underground) : "";
            var slot = manager != null ? manager.GetSlot() : -1;
            var state = session != null ? session.State.ToString() : "missing";
            var data = ArenaStatsStore.Data;
            var context = data != null ? (underground ? data.UndergroundRun : data.NormalRun) : null;
            var selected = data != null && data.RunUnderground == underground;
            var receipt = selected ? data.EntryReceipt : context?.EntryReceipt;
            var trackedActive = selected ? data.RunActive : context?.Active == true;
            var rewardPending = selected ? data.RewardPending : context?.RewardPending == true;
            var trackedKey = selected ? data.RunKey : context?.RunKey;
            var completedKey = selected ? data.LastCompletedRunKey : context?.LastCompletedRunKey;
            var prefix = underground ? "underground:" : "normal:";
            var receiptValid = receipt != null && Guid.TryParse(receipt.Nonce, out var nonce) && nonce != Guid.Empty &&
                receipt.Underground == underground && receipt.RequestedAtUnixMs > 0 && receipt.TicketCost > 0 &&
                receipt.TicketsBefore >= receipt.TicketCost &&
                (string.IsNullOrEmpty(receipt.PreviousRunKey) || receipt.PreviousRunKey.StartsWith(prefix, StringComparison.Ordinal)) &&
                (string.IsNullOrEmpty(receipt.PreviousNativeRunKey) || receipt.PreviousNativeRunKey.StartsWith(prefix, StringComparison.Ordinal));
            var elapsed = receipt != null ? now - receipt.RequestedAtUnixMs : -1;
            // These names report failed observations; they do not authorize,
            // retry, or replace any condition in RecordNativeArenaBegin/Ledger.
            var reasons = new List<string>();
            if (!string.IsNullOrEmpty(s_entryReceiptWriteFailure)) reasons.Add("prior-receipt-write-failure");
            if (response == null) reasons.Add("response-missing");
            if (session == null) reasons.Add("session-missing");
            if (manager == null) reasons.Add("manager-missing");
            if (redrafting) reasons.Add("redrafting");
            if (session != null)
            {
                if (!session.HasIsUnderground || session.IsUnderground != underground) reasons.Add("explicit-mode-missing-or-mismatch");
                if (!session.HasState || !session.HasIsActive) reasons.Add("explicit-state-or-active-missing");
                if (state != "DRAFTING" || !session.IsActive) reasons.Add("session-not-active-DRAFTING");
                if (session.HasIsInRewards && session.IsInRewards) reasons.Add("legacy-reward-true");
                if (session.Wins != 0 || session.Losses != 0) reasons.Add("nonzero-score");
            }
            if (response != null && response.DeckID <= 0) reasons.Add("response-deck-invalid");
            if (manager != null && response != null && nativeKey != key) reasons.Add("native-deck-identity-mismatch");
            if (data == null || !data.ModeContextsInitialized) reasons.Add("mode-context-uninitialized");
            if (trackedActive || rewardPending) reasons.Add("tracked-active-or-reward-pending");
            if (!receiptValid) reasons.Add("receipt-invalid");
            if (receipt != null)
            {
                if (receipt.Phase != "accepted") reasons.Add("receipt-not-accepted");
                if ((trackedKey ?? "") != receipt.PreviousRunKey) reasons.Add("tracked-previous-identity-mismatch");
                if (string.IsNullOrEmpty(key) || !key.StartsWith(prefix, StringComparison.Ordinal) ||
                    key == receipt.PreviousRunKey || key == receipt.PreviousNativeRunKey || key == completedKey ||
                    (data?.CountedRunKeys != null && data.CountedRunKeys.Contains(key))) reasons.Add("new-run-identity-rejected");
                if (elapsed < 0 || elapsed > ArenaStatsLedger.EntryResponseWindowMs) reasons.Add("outside-response-window");
            }
            if (slot != 0 || response == null || response.MaxSlot <= 0) reasons.Add("initial-slot-or-maxslot-invalid");
            Log("Arena native Begin read-only rejection: gate=" + gate + "; reasons=" + string.Join(",", reasons) +
                "; pid=" + Process.GetCurrentProcess().Id + "; response=" + (response != null) + "; session=" + (session != null) +
                "; sessionHasMode=" + (session?.HasIsUnderground == true) + "; sessionUnderground=" + (session?.IsUnderground == true) +
                "; sessionHasState=" + (session?.HasState == true) + "; sessionState=" + state +
                "; sessionHasActive=" + (session?.HasIsActive == true) + "; sessionActive=" + (session?.IsActive == true) +
                "; sessionHasRewards=" + (session?.HasIsInRewards == true) + "; sessionRewards=" + (session?.IsInRewards == true) +
                "; wins=" + (session?.Wins ?? -1) + "; losses=" + (session?.Losses ?? -1) +
                "; responseDeckId=" + (response?.DeckID ?? 0) + "; responseMaxSlot=" + (response?.MaxSlot ?? -1) +
                "; manager=" + (manager != null) + "; managerMode=" + (manager == null ? "missing" : underground ? "underground" : "normal") +
                "; managerRedrafting=" + redrafting + "; managerState=" + (manager == null ? "missing" : manager.GetArenaState(underground).ToString()) +
                "; nativeKey=" + nativeKey + "; responseKey=" + key + "; slot=" + slot +
                "; modeContexts=" + (data?.ModeContextsInitialized == true) + "; selectedContext=" + selected +
                "; trackedActive=" + trackedActive + "; rewardPending=" + rewardPending + "; trackedKey=" + trackedKey +
                "; lastCompletedKey=" + completedKey + "; receipt=" + (receipt != null) + "; receiptValid=" + receiptValid +
                "; receiptPhase=" + receipt?.Phase + "; nonce=" + receipt?.Nonce + "; receiptMode=" + (receipt?.Underground == true) +
                "; receiptPreviousKey=" + receipt?.PreviousRunKey + "; receiptPreviousNativeKey=" + receipt?.PreviousNativeRunKey +
                "; requestedAt=" + (receipt?.RequestedAtUnixMs ?? 0) + "; elapsedMs=" + elapsed +
                "; ticketsBefore=" + (receipt?.TicketsBefore ?? 0) + "; ticketCost=" + (receipt?.TicketCost ?? 0) +
                "; responseWindowMs=" + ArenaStatsLedger.EntryResponseWindowMs);
        }
        catch (Exception ex)
        {
            // Even the diagnostic sink can throw. Neither path may affect the
            // original rejection or receipt acceptance/save behavior.
            try { Log("Arena native Begin read-only diagnostic failed: gate=" + gate + "; exception=" + ex.GetType().Name); }
            catch { }
        }
    }

    internal static void RecordNativeArenaSubmission(string nonce, bool underground)
    {
        var data = ArenaStatsStore.Data;
        if (!string.IsNullOrEmpty(s_entryReceiptWriteFailure) || data.RunUnderground != underground) return;
        var before = data.EntryReceipt.Copy();
        if (!ArenaStatsLedger.MarkEntryAccepted(data, nonce)) return;
        if (!ArenaStatsStore.SaveEntryReceipt(underground, data.EntryReceipt.Copy(), out var failure))
        {
            ArenaStatsLedger.RestoreEntryReceipt(data, underground, before);
            s_entryReceiptWriteFailure = failure;
        }
        Log("Arena native DraftBegin submission returned inside automatic entry scope: nonce=" + nonce +
            "; mode=" + (underground ? "underground" : "normal") + "; durable=" + string.IsNullOrEmpty(failure) +
            "; server response still pending");
    }

    internal static void RecordNativeArenaSessionResponse(PegasusUtil.ArenaSessionResponse response, bool underground)
    {
        if (!string.IsNullOrEmpty(s_entryReceiptWriteFailure) || response == null || response.ErrorCode != 0 ||
            (underground ? !response.HasUndergroundSession : !response.HasSession)) return;
        var session = underground ? response.UndergroundSession : response.Session;
        if (session == null || !session.HasState || !session.HasIsActive ||
            (session.HasIsUnderground && session.IsUnderground != underground)) return;
        var data = ArenaStatsStore.Data;
        var context = underground ? data.UndergroundRun : data.NormalRun;
        var before = (data.RunUnderground == underground ? data.EntryReceipt : context.EntryReceipt).Copy();
        if (!ArenaStatsLedger.ObserveEntryEmptyAcknowledgement(data, underground, session.State.ToString(),
            session.IsActive, session.HasIsInRewards && session.IsInRewards, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return;
        var receipt = data.RunUnderground == underground ? data.EntryReceipt : context.EntryReceipt;
        if (!ArenaStatsStore.SaveEntryReceipt(underground, receipt.Copy(), out var failure))
        {
            ArenaStatsLedger.RestoreEntryReceipt(data, underground, before);
            s_entryReceiptWriteFailure = failure;
        }
        Log("Arena entry received actual session response: nonce=" + receipt.Nonce + "; state=" + session.State +
            "; emptyAckAt=" + receipt.EmptyAcknowledgedAtUnixMs + "; durable=" + string.IsNullOrEmpty(failure));
    }

    private void SynchronizeArenaStatsMode(bool arenaModeSelected)
    {
        var game = GameMgr.Get();
        var inArenaGame = SceneMgr.Get()?.GetMode() == SceneMgr.Mode.GAMEPLAY && game != null &&
            (game.IsArena() || game.IsUndergroundArena());
        if (!inArenaGame && !arenaModeSelected) return;
        var underground = ArenaLifecyclePolicy.ResolveStatsMode(ArenaSettings.IsUnderground, inArenaGame,
            game != null && game.IsUndergroundArena());
        var stats = ArenaStatsStore.Data;
        if (!stats.ModeContextsInitialized && stats.RunActive && string.IsNullOrEmpty(stats.RunKey) && !inArenaGame)
        {
            var legacyManager = DraftManager.Get();
            // A legacy file has no mode discriminator. Prefer the one
            // server-confirmed active run if it is unambiguous, and avoid
            // assigning that legacy run while login caches are still empty.
            if (legacyManager == null || legacyManager.CurrentSeason == null) return;
            var normalActive = legacyManager.HasCurrentlyActiveRun(false);
            var undergroundActive = legacyManager.HasCurrentlyActiveRun(true);
            if (normalActive != undergroundActive) ArenaStatsStore.SelectMode(undergroundActive);
            else if (SceneMgr.Get()?.GetMode() != SceneMgr.Mode.DRAFT) return;
            else Log("Legacy Arena current run has no mode identity; retaining its counters under the selected mode without changing lifetime totals");
        }
        var changed = ArenaStatsStore.SelectMode(underground);
        _gamesThisRun = ArenaStatsStore.Data.AutomaticGamesInRun;
        if (!changed) return;
        _rewardReceipt.Reset();
        if (!inArenaGame)
        {
            // Mode changes invalidate the page's cached draft actors and
            // recommendation. Purchase ownership is global because both
            // modes spend the same tavern-ticket balance.
            _undergroundRequested = false;
            _requestedChoices = false;
            _requestedDraft = false;
            _matchmakingRequested = false;
            _redraftPageStarted = false;
            _redraftPageStartedAt = -1f;
            _lastArenaState = "";
            _lifecycle.Observe("MODE_SWITCH", Time.unscaledTime);
            _lifecycle.ResetNativeEvidence();
            ResetDraftSelection();
            _draftRecommendationPaceKey = "";
            _draftRecommendationReadyAt = -1f;
        }
        s_arenaOverlayStats = ArenaStatsStore.Summary();
        Log("Arena current-run context restored: mode=" + (underground ? "underground" : "normal") +
            ", run=" + ArenaStatsStore.Data.RunKey + ", active=" + ArenaStatsStore.Data.RunActive);
    }

    private void PollNativeArenaResults(DraftManager manager)
    {
        try
        {
            if (manager == null || !ArenaStatsStore.Data.RunActive) return;
            var underground = string.IsNullOrEmpty(ArenaStatsStore.Data.RunKey)
                ? ArenaSettings.IsUnderground : ArenaStatsStore.Data.RunUnderground;
            if (!TryGetVerifiedNativeSession(manager, underground, out var session) ||
                string.IsNullOrEmpty(ArenaStatsStore.Data.RunKey) ||
                GetNativeRunKey(manager, underground) != ArenaStatsStore.Data.RunKey) return;
            var wins = session.Wins;
            var losses = session.Losses;
            if (wins < 0 || losses < 0) return;
            // Server caches can reset to 0 during page/connection changes.
            // Only reconcile increases for the persisted current run.
            ArenaStatsStore.ReconcileResults(wins, losses);
            s_arenaOverlayStats = ArenaStatsStore.Summary();
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= _nextChoiceDiag)
                Log("Arena native win/loss polling failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void StartArenaRunStats(DraftManager manager, string arenaState)
    {
        ArenaStatsStore.SelectMode(ArenaSettings.IsUnderground);
        var runKey = GetNativeRunKey(manager, ArenaSettings.IsUnderground);
        if (string.IsNullOrEmpty(runKey)) return;
        if (ArenaStatsStore.Data.RunActive && ArenaStatsStore.Data.RunKey == runKey) return;
        if (!TryGetVerifiedNativeSession(manager, ArenaSettings.IsUnderground, out var session)) return;
        var entryState = ArenaStatsLedger.EntryDraftState(ArenaStatsStore.Data.EntryReceipt, runKey,
            session.State.ToString(), arenaState, session.IsActive, session.HasIsInRewards && session.IsInRewards);
        if (!_lifecycle.CanAttachNativeRun(runKey, entryState, session.Wins, session.Losses,
            session.IsActive, session.HasIsInRewards && session.IsInRewards, Time.unscaledTime)) return;
        var confirmedNewRun = ArenaStatsLedger.HasNewRunEvidence(ArenaStatsStore.Data, runKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var previousKey = ArenaStatsStore.Data.RunKey;
        ArenaStatsStore.StartRun(session.Wins, session.Losses, runKey, confirmedNewRun, true);
        Log("Arena run attached: key=" + runKey + ", origin=" + ArenaStatsStore.Data.RunOrigin +
            ", previousKey=" + previousKey + ", baseline=" + session.Wins + "/" + session.Losses +
            "; countedNewRun=" + confirmedNewRun);
        s_arenaOverlayStats = ArenaStatsStore.Summary();
    }

    private static bool TryGetVerifiedNativeSession(DraftManager manager, bool underground,
        out PegasusUtil.ArenaSession session)
    {
        session = null;
        if (manager == null || manager.CurrentSeason == null) return false;
        s_normalSessionField ??= typeof(DraftManager).GetField("m_currentNormalSession", InstancePrivate);
        s_undergroundSessionField ??= typeof(DraftManager).GetField("m_currentUndergroundSession", InstancePrivate);
        session = (underground ? s_undergroundSessionField : s_normalSessionField)?.GetValue(manager) as PegasusUtil.ArenaSession;
        // These mode-specific objects are assigned from successful native
        // ArenaSessionResponse packets, unlike the initial zeroed win fields.
        return session != null && session.HasIsActive && session.HasState &&
            (!session.HasIsUnderground || session.IsUnderground == underground) &&
            session.Wins >= 0 && session.Losses >= 0;
    }

    private static string GetNativeRunKey(DraftManager manager, bool underground)
    {
        var deck = manager?.GetDraftDeck(underground);
        return deck == null || deck.ID <= 0 ? "" :
            (underground ? "underground:" : "normal:") + manager.CurrentSeasonId + ":" + deck.ID;
    }

    private static string GetNativeGameKey(GameMgr game)
    {
        if (game == null) return "";
        // SDK queue events go straight to Network.GotoGameServer and leave
        // LastGameData.GameConnectionInfo null. Both native connection paths
        // retain the server in Network and expose its handle through GameMgr.
        var server = Network.Get()?.GetLastGameServerJoined();
        var info = game?.LastGameData?.GameConnectionInfo;
        return ArenaGameIdentity.Resolve(ArenaStatsStore.Data.RunKey, game.GetGameHandle(),
            server?.Address, server?.Port ?? 0, server?.GameHandle ?? 0, server?.ClientHandle ?? 0,
            info?.Address, info?.Port ?? 0, info?.GameHandle ?? 0, info?.ClientHandle ?? 0,
            info?.HasGameHandle == true, info?.HasClientHandle == true, ArenaStatsStore.Data.PendingGameKey);
    }

    private void TrackCurrentArenaGame()
    {
        var game = GameMgr.Get();
        if (game == null || (!game.IsArena() && !game.IsUndergroundArena())) return;
        if (SpectatorManager.Get()?.IsInSpectatorMode() == true) return;
        ArenaStatsStore.SelectMode(game.IsUndergroundArena());
        var manager = DraftManager.Get();
        var underground = game.IsUndergroundArena();
        var runKey = GetNativeRunKey(manager, underground);
        if (manager == null || string.IsNullOrEmpty(runKey))
        {
            _lifecycle.ResetNativeEvidence();
            return;
        }
        if (!ArenaStatsStore.Data.RunActive || ArenaStatsStore.Data.RunKey != runKey)
        {
            if (!TryGetVerifiedNativeSession(manager, underground, out var session))
            {
                _lifecycle.ResetNativeEvidence();
                return;
            }
            if (!_lifecycle.CanAttachNativeRun(runKey, session.State.ToString(), session.Wins, session.Losses,
                session.IsActive, session.HasIsInRewards && session.IsInRewards, Time.unscaledTime)) return;
            var previousKey = ArenaStatsStore.Data.RunKey;
            ArenaStatsStore.StartRun(session.Wins, session.Losses, runKey,
                ArenaStatsLedger.HasNewRunEvidence(ArenaStatsStore.Data, runKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), true);
            Log("Arena gameplay run attached: key=" + runKey + ", previousKey=" + previousKey +
                ", origin=" + ArenaStatsStore.Data.RunOrigin + ", baseline=" + session.Wins + "/" + session.Losses);
        }
        ArenaStatsStore.BeginGame(GetNativeGameKey(game));
    }

    internal static void RecordNativeGameOver(TAG_PLAYSTATE result)
    {
        var game = GameMgr.Get();
        if (game == null || (!game.IsArena() && !game.IsUndergroundArena())) return;
        if (SpectatorManager.Get()?.IsInSpectatorMode() == true) return;
        if (result != TAG_PLAYSTATE.WON && result != TAG_PLAYSTATE.LOST) return;
        ArenaStatsStore.SelectMode(game.IsUndergroundArena());
        var key = GetNativeGameKey(game);
        if (ArenaStatsStore.FinishGame(key, result == TAG_PLAYSTATE.WON))
        {
            s_arenaOverlayStats = ArenaStatsStore.Summary();
            Log("Arena game result persisted before scene exit: " + result + ", key=" + key);
        }
    }

    private void CompleteArenaRunStats(bool rewardsSeen)
    {
        // A native reward receipt can arrive before this tick observes the
        // rewards page. Preserve the reached limit before RecordReward clears
        // the completed run's counter; otherwise NO_RUN could buy a new entry.
        RequestGameLimitStopIfNeeded(rewardsSeen);
        if (rewardsSeen) ArenaStatsStore.RecordReward();

        _gamesThisRun = 0;
        _ticketPurchaseRequested = false;
        _ticketPurchaseClicked = false;
        _ticketPurchaseConfirmed = false;
        _ticketPurchaseStartedAt = -1f;
        ResetTicketPopupOwnership();
        s_arenaOverlayStats = ArenaStatsStore.Summary();
    }

    private void BeginTicketPurchaseStats(DraftManager manager)
    {
        var data = ArenaStatsStore.Data;
        if (data.PurchasePending) return;
        data.PurchasePending = true;
        data.PurchaseRequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        data.PurchaseTicketsBefore = manager?.GetNumTicketsOwned() ?? 0;
        data.PurchaseGoldBefore = TryGetGoldBalance(out var gold) ? gold : -1;
        data.PurchaseCounted = false;
        data.PurchaseGoldCounted = 0;
        ArenaStatsStore.Save();
    }

    private void CompleteTicketPurchaseStats(DraftManager manager)
    {
        var data = ArenaStatsStore.Data;
        if (!data.PurchasePending || manager == null) return;
        var afterGold = TryGetGoldBalance(out var gold) ? gold : -1;
        if (!ArenaStatsLedger.ReconcilePurchase(data, manager.GetNumTicketsOwned(), afterGold,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return;
        Log("Arena ticket receipt persisted: total=" + data.TicketsPurchased + ", transactionGold=" +
            data.PurchaseGoldCounted + ", pending=" + data.PurchasePending);
        ArenaStatsStore.Save();
        s_arenaOverlayStats = ArenaStatsStore.Summary();
    }

    private ArenaRunBudgetDecision RequestGameLimitStopIfNeeded(bool confirmedRewardReceipt = false)
    {
        SynchronizeRunBudgetProjection();
        if (HasControllerRunStop()) return ArenaRunBudgetDecision.Ready;
        var limit = ArenaSettings.MaxGamesPerRun?.Value ?? 0;
        // Unlimited runs retain the existing navigation and lifecycle paths.
        if (limit <= 0) return ArenaRunBudgetDecision.Ready;
        var data = ArenaStatsStore.Data;
        var manager = DraftManager.Get();
        var runtimeEnabled = HsModNativeArena.IsNativeArenaModeSelected() && HsModNativeArena.IsRuntimeEnabled();
        var scene = SceneMgr.Get()?.GetMode().ToString();
        if (confirmedRewardReceipt)
        {
            var receiptModeMatches = manager != null && manager.CurrentSeason != null &&
                manager.IsUnderground() == data.RunUnderground;
            if (TryRequestConfirmedRewardBudgetStop(true, receiptModeMatches,
                receiptModeMatches ? manager.GetArenaState(data.RunUnderground).ToString() : "UNKNOWN",
                !receiptModeMatches || manager.HasCurrentlyActiveRun(data.RunUnderground),
                !receiptModeMatches || manager.IsRewardChestReady(), runtimeEnabled, scene, limit))
                return ArenaRunBudgetDecision.Ready;
        }
        var nativeReady = TryGetVerifiedNativeSession(manager, data.RunUnderground, out var session) &&
            manager.IsUnderground() == data.RunUnderground;
        var state = nativeReady ? session.State.ToString() : "UNKNOWN";
        var active = nativeReady && session.IsActive;
        var inRewards = nativeReady && (state == "NO_RUN"
            ? ArenaSessionStatePolicy.BlocksEmptySessionFromRewards(
                manager.GetArenaState(data.RunUnderground).ToString(), state,
                session.HasIsInRewards && session.IsInRewards)
            : session.HasIsInRewards && session.IsInRewards);
        var confirmedEmpty = nativeReady && _lifecycle.CanObserveEmptyRun(state, true, active, inRewards,
            manager.IsRewardChestReady(), Time.unscaledTime);
        var decision = EvaluateNativeRunBudget(nativeReady, nativeReady && manager.IsUnderground(),
            nativeReady ? GetNativeRunKey(manager, data.RunUnderground) : "", state, active, inRewards,
            nativeReady ? manager.GetSlot() : -1, nativeReady ? manager.GetMaxSlot() : 0,
            confirmedEmpty, confirmedRewardReceipt);
        RequestAlignedGameLimitStop(runtimeEnabled, scene, limit, decision);
        return decision;
    }

    private void RequestArenaStop(string reason)
    {
        if (_runStopRequested && string.Equals(_runStopReason, reason, StringComparison.Ordinal)) return;
        _runStopRequested = true;
        CancelPopupOperations();
        _runStopReason = reason ?? "竞技场流程已停止";
        s_arenaOverlayStopReason = _runStopReason;
        s_arenaOverlayPhase = _runStopReason;
        if (_arenaAutomationEnabled) SetArenaAutomation(false);
        try { File.WriteAllText(InternalStopPath, _runStopReason + "\n", Encoding.UTF8); } catch { }
        Log("Arena automation stopped: " + _runStopReason + "; games=" + _gamesThisRun);
    }

    private void EnableHsModRewardAutomation()
    {
        if (_rewardAutoOpenChanged) return;
        try
        {
            var configType = Type.GetType("HsMod.PluginConfig, HsMod");
            var field = configType?.GetField("isAutoOpenBoxesRewardEnable",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var entry = field?.GetValue(null);
            var valueProperty = entry?.GetType().GetProperty("Value",
                BindingFlags.Instance | BindingFlags.Public);
            if (valueProperty == null || valueProperty.PropertyType != typeof(bool)) return;
            var current = (bool)valueProperty.GetValue(entry, null);
            _rewardAutoOpenPrevious = current;
            if (!current)
            {
                valueProperty.SetValue(entry, true, null);
                Log("enabled HsMod native reward-box auto-open for Arena rewards");
            }
            _rewardAutoOpenChanged = true;
        }
        catch (Exception ex)
        {
            Log("HsMod reward-box auto-open is unavailable; Arena ACK fallback remains active: " +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    private bool TryFinishNativeRewardUi(DraftDisplay display, bool forceCloseAnimation)
    {
        if (display == null) return false;
        try
        {
            // OnRewardBoxesDone is the exact handler behind the native Done
            // button.  It sends the ACK, exits the reward visual controller,
            // clears the reward widget and transitions the Arena page.  The
            // old fallback called only OnOpenRewardsComplete, which cleared a
            // pointer but left the visible chest GameObject on screen.
            if (forceCloseAnimation)
            {
                s_rewardBoxesDisplayField ??= typeof(DraftDisplay).GetField("m_rewardBoxesDisplay", InstancePrivate);
                var rewardBoxes = s_rewardBoxesDisplayField?.GetValue(display);
                if (rewardBoxes != null)
                {
                    s_showAlreadyOpenedRewardsMethod ??= rewardBoxes.GetType().GetMethod(
                        "ShowAlreadyOpenedRewards", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    s_showAlreadyOpenedRewardsMethod?.Invoke(rewardBoxes, null);
                }
            }
            s_onRewardBoxesDoneMethod ??= typeof(DraftDisplay).GetMethod(
                "OnRewardBoxesDone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (s_onRewardBoxesDoneMethod == null) return false;
            s_onRewardBoxesDoneMethod.Invoke(display, null);
            // A few client builds finish the native callback but leave the
            // reward GameObjects alive until the next animation tick.  Ask
            // the native reward display to destroy its objects explicitly so
            // the next draft cannot render behind the old chest.
            TryKillRewardObjects(display);
            TryDestroyRewardsWidget(display);
            Log(forceCloseAnimation
                ? "forced native Arena reward Done action; closed reward chest UI"
                : "completed native Arena reward Done action");
            return true;
        }
        catch (Exception ex)
        {
            Log("native Arena reward Done action failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private bool TryHideStaleRewardUi(DraftDisplay display)
    {
        if (display == null || !display.IsRewardsFlowPending) return false;
        try
        {
            s_rewardBoxesDisplayField ??= typeof(DraftDisplay).GetField("m_rewardBoxesDisplay", InstancePrivate);
            var rewardBoxes = s_rewardBoxesDisplayField?.GetValue(display);
            s_showAlreadyOpenedRewardsMethod ??= rewardBoxes?.GetType().GetMethod(
                "ShowAlreadyOpenedRewards", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_showAlreadyOpenedRewardsMethod?.Invoke(rewardBoxes, null);
            display.OnOpenRewardsComplete();
            TryKillRewardObjects(display, rewardBoxes);
            TryDestroyRewardsWidget(display);
            Log("closed stale Arena reward widget after the server had already entered the next draft");
            return true;
        }
        catch (Exception ex)
        {
            Log("stale Arena reward widget cleanup failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static void TryKillRewardObjects(DraftDisplay display, object knownRewardBoxes = null)
    {
        try
        {
            var rewardBoxes = knownRewardBoxes;
            if (rewardBoxes == null && display != null)
            {
                s_rewardBoxesDisplayField ??= typeof(DraftDisplay).GetField("m_rewardBoxesDisplay", InstancePrivate);
                rewardBoxes = s_rewardBoxesDisplayField?.GetValue(display);
            }
            if (rewardBoxes == null) return;
            s_killRewardObjectsMethod ??= rewardBoxes.GetType().GetMethod(
                "KillRewardObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            s_killRewardObjectsMethod?.Invoke(rewardBoxes, null);
        }
        catch
        {
            // Reward cleanup is best effort. The native callback has already
            // acknowledged the chest; a missing visual method must not stop
            // the Arena state machine.
        }
    }

    private void TryDestroyRewardsWidget(DraftDisplay display)
    {
        if (display == null) return;
        try
        {
            // OnRewardBoxesDone clears the reward pointer, but some client
            // builds keep the root reward widget alive until this private
            // teardown method is called.  Invoke the same method used by the
            // native DraftDisplay lifecycle only after the ACK/completion
            // callback, never while a real reward animation is pending.
            s_destroyRewardsWidgetMethod ??= typeof(DraftDisplay).GetMethod(
                "DestroyRewardsWidget", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (s_destroyRewardsWidgetMethod == null) return;
            s_destroyRewardsWidgetMethod.Invoke(display, null);
            if (Time.unscaledTime - _lastRewardWidgetDestroyLogAt >= 5f)
            {
                _lastRewardWidgetDestroyLogAt = Time.unscaledTime;
                Log("native Arena reward widget explicitly destroyed after ACK");
            }
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= _nextPopupDiagnostic)
            {
                _nextPopupDiagnostic = Time.unscaledTime + 10f;
                Log("native Arena reward widget destroy deferred: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private void RestoreHsModRewardAutomation()
    {
        if (!_rewardAutoOpenChanged) return;
        try
        {
            var configType = Type.GetType("HsMod.PluginConfig, HsMod");
            var field = configType?.GetField("isAutoOpenBoxesRewardEnable",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var entry = field?.GetValue(null);
            var valueProperty = entry?.GetType().GetProperty("Value",
                BindingFlags.Instance | BindingFlags.Public);
            if (valueProperty != null && valueProperty.PropertyType == typeof(bool))
                valueProperty.SetValue(entry, _rewardAutoOpenPrevious, null);
        }
        catch (Exception ex)
        {
            Log("HsMod reward-box auto-open restore failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _rewardAutoOpenChanged = false;
            Log("restored HsMod reward-box auto-open setting after Arena rewards");
        }
    }

    private bool CanAttemptTicketPurchase(DraftManager manager)
    {
        if (manager == null || manager.GetNumTicketsOwned() >= ArenaSettings.TicketCost) return true;
        if (ArenaSettings.StopOnTicketShortage.Value)
        {
            RequestArenaStop("门票不足（现有 " + manager.GetNumTicketsOwned() + "/需要 " +
                ArenaSettings.TicketCost + "），按设置停止，不购买门票");
            return false;
        }
        var elapsed = _ticketPurchaseStartedAt >= 0f
            ? Time.unscaledTime - _ticketPurchaseStartedAt
            : -1f;
        if (!ArenaSettings.StopOnGoldShortage.Value && !ArenaSettings.StopOnTicketShortage.Value) return true;
        if (Time.unscaledTime < _nextResourceCheck) return !_runStopRequested;
        _nextResourceCheck = Time.unscaledTime + 5f;

        if (TryGetGoldBalance(out var gold))
        {
            var price = Mathf.Max(0, ArenaSettings.TicketGoldCost.Value);
            var decision = ArenaTicketPolicy.Decide(
                manager.GetNumTicketsOwned(), ArenaSettings.TicketCost, true, gold,
                price, ArenaSettings.StopOnTicketShortage.Value,
                ArenaSettings.StopOnGoldShortage.Value, elapsed,
                TicketPurchaseTimeoutSeconds, true);
            Log("Arena resources: tickets=" + manager.GetNumTicketsOwned() + ", gold=" + gold + ", ticketPrice=" + price);
            if (decision == ArenaTicketGateResult.StopGoldShortage)
            {
                RequestArenaStop("门票不足且金币不足（金币 " + gold + "/" + price + "）");
                return false;
            }
            if (gold < price)
                Log("Arena gold is below the configured ticket price, but gold-shortage stop is disabled; allowing native purchase attempt");
        }
        else if (Time.unscaledTime >= _nextChoiceDiag)
        {
            _nextChoiceDiag = Time.unscaledTime + 5f;
            Log("Arena gold balance is not exposed by this client; ticket purchase will use the native shop result");
        }
        return !_runStopRequested;
    }

    private static bool TryGetGoldBalance(out int gold)
    {
        gold = -1;
        try
        {
            // CurrencyManager is the same native service DraftManager uses for
            // tavern tickets.  The previous reflection probe looked for a
            // global type named "CurrencyManager", but the live client keeps
            // it under Blizzard.T5.Services, so it silently failed and could
            // not stop before a gold-shortage purchase loop.
            var currency = ServiceManager.Get<CurrencyManager>();
            if (currency == null) return false;
            // A zero balance is also used while the wallet is still loading;
            // do not mistake that transient state for a real shortage.
            if (!currency.IsBalanceAvailable(CurrencyType.GOLD)) return false;
            var value = currency.GetBalance(CurrencyType.GOLD);
            gold = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            return gold >= 0;
        }
        catch { }
        return false;
    }

    private void UpdateArenaBudgetOverlay()
    {
        var limit = ArenaSettings.MaxGamesPerRun?.Value ?? 0;
        s_arenaOverlayBudget = limit > 0
            ? "对局：" + _gamesThisRun + "/" + limit
            : "对局：" + _gamesThisRun + "/不限";
        s_arenaOverlayStopReason = _runStopReason ?? "";
    }

    /// <summary>
    /// Clear page and executor latches when HsMod's native Arena switch is
    /// toggled. These are deliberately transient: the run counter changes
    /// only at a confirmed run boundary. Re-enable re-evaluates the stop
    /// decision without granting a new budget; a scene reload retains it.
    /// </summary>
    private void ResetArenaPageTransientState()
    {
        CancelPopupOperations();
        ResetDraftSelection();
        // F12 off/on starts a new page check. A prior 120-second wait must
        // not instantly time out this attempt while its visual state settles.
        _modeNavigationTarget = null;
        _modeNavigationLandingId = 0;
        _modeNavigationConfirmed = false;
        _modeNavigationReloads = 0;
        _modeNavigationWaitSince = -1f;
        _modeNavigationNextLog = 0f;
        _modeNavigationStable.Reset();
        _entryRecoveryBudget.Reset();
        _requestedDraft = false;
        _undergroundRequested = false;
        _requestedChoices = false;
        _rewardFlowStarted = false;
        _rewardAckRequested = false;
        _rewardUiWasPending = false;
        _rewardNativeDoneRequestedAt = -1f;
        _matchmakingRequested = false;
        _ticketPurchaseRequested = false;
        _ticketPurchaseClicked = false;
        _ticketPurchaseConfirmed = false;
        _redraftPageStarted = false;
        _redraftPageStartedAt = -1f;
        _nextRedraftDiagnostic = 0f;
        _nextStaleRewardCleanup = 0f;
        _lastArenaState = "";
        _lastSceneMode = "";
        _ticketPurchaseAttempts = 0;
        _ticketPurchaseStartedAt = -1f;
        _boxExecutorRecoveryAttempted = false;
        _nextBoxExecutorRecoveryCheck = 0f;
        _draftRecommendationPaceKey = "";
        _draftRecommendationReadyAt = -1f;
        IsArenaUiActive = false;
        IsArenaGameplayExecutorEnabled = false;
    }

    private static void UpdateArenaResourceOverlay(DraftManager manager)
    {
        try
        {
            if (manager == null) return;
            if (TryGetGoldBalance(out var gold))
                s_arenaOverlayResources = "资源：门票 " + manager.GetNumTicketsOwned() + "｜金币 " + gold;
            else
                s_arenaOverlayResources = "资源：门票 " + manager.GetNumTicketsOwned() + "｜金币未知";
        }
        catch
        {
            s_arenaOverlayResources = "资源：门票未知｜金币未知";
        }
    }

    private void SetArenaAutomation(bool enabled)
    {
        if (_arenaAutomationEnabled == enabled) return;
        _arenaAutomationEnabled = enabled;
        IsArenaGameplayExecutorEnabled = enabled;
        if (HsModNativeArena.IsNativeArenaModeSelected())
        {
            // In native Arena mode HsMod's F12 state belongs to the whole
            // Arena run.  Do not turn it off when a gameplay scene ends or
            // turn it on only for a single match; the draft/reward loop must
            // remain under the same native switch.
            Log("native Arena mode keeps HsMod runtime state unchanged");
            return;
        }
        try
        {
            var type = Type.GetType("HsMod.WildLadderBot, HsMod");
            var method = type?.GetMethod("SetRuntimeEnabled", BindingFlags.Static | BindingFlags.NonPublic);
            method?.Invoke(null, new object[] { enabled });
            Log("native Box play executor " + (enabled ? "enabled for Arena gameplay" : "disabled outside Arena gameplay"));
        }
        catch (Exception ex)
        {
            Log("native Box play executor toggle failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal static void DrawArenaOverlay()
    {
        if (Event.current == null || Event.current.type != UnityEngine.EventType.Repaint) return;
        if (s_arenaOverlayStyle == null)
        {
            s_arenaOverlayStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = Color.white }
            };
        }

        var oldColor = GUI.color;
        var oldBackground = GUI.backgroundColor;
        try
        {
            var scale = Mathf.Max(1f, Screen.height / 1080f);
            var width = 330f * scale;
            var height = 260f * scale;
            var panel = new Rect(8f * scale, 8f * scale, width, height);
            GUI.color = new Color(1f, 1f, 1f, 0.94f);
            GUI.backgroundColor = new Color(0.035f, 0.06f, 0.09f, 0.96f);
            GUI.Box(panel, GUIContent.none);
            var text = "脚本：" + (File.Exists(EnablePath) || HsModNativeArena.IsRuntimeEnabled() ? "开启" : "关闭") +
                       "\n模式：" + (ArenaSettings.IsUnderground ? "地下竞技场" : "普通竞技场") +
                       "\n选牌：" + (ArenaSettings.UseBoxScores?.Value == true ? "盒子推荐打法" : "手动") +
                       "\n流程：" + (ArenaSettings.AutoDraft?.Value == true ? "自动选牌" : "手动选牌") +
                       (ArenaSettings.AutoRedraft?.Value == true ? "｜自动重选" : "｜手动重选") +
                       "\n状态：" + s_arenaOverlayPhase +
                       "\n对战：" + (ArenaSettings.AutoPlay?.Value == true ? "盒子推荐" : "手动") +
                       "｜匹配：" + (ArenaSettings.AutoMatch?.Value == true ? "自动" : "手动") +
                       "\n" + s_arenaOverlayBudget +
                       "\n" + s_arenaOverlayResources +
                       "\n" + s_arenaOverlayStats +
                       (string.IsNullOrEmpty(s_arenaOverlayStopReason) ? "" : "\n停止：" + s_arenaOverlayStopReason);
            GUI.Label(new Rect(panel.x + 12f * scale, panel.y + 9f * scale,
                               panel.width - 24f * scale, panel.height - 18f * scale), text, s_arenaOverlayStyle);
        }
        finally
        {
            GUI.color = oldColor;
            GUI.backgroundColor = oldBackground;
        }
    }

    private static bool IsBoxReadyForArena(bool requireScoring = true)
    {
        return string.Equals(GetBoxReadinessDiagnostic(requireScoring), "ready", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the exact reason the Arena gate is closed. A heartbeat can be
    /// fresh while belonging to a previous Hearthstone process, and a partial
    /// atomic write can briefly make JSON unreadable. Keeping the reason in
    /// the Arena log makes those cases distinguishable from a missing Box
    /// configuration and avoids silent matchmaking stalls.
    /// </summary>
    private static string GetBoxReadinessDiagnostic(bool requireScoring = true)
    {
        try
        {
            // Arena play is held until HsMod is configured for the file bridge.
            // The direct bridge has no Arena deck injection path and can report
            // a live heartbeat while still producing an empty cardgroup.
            if (!File.Exists(HsModConfigPath)) return "HsMod.cfg missing";
            var config = File.ReadAllText(HsModConfigPath);
            if (!Regex.IsMatch(config, @"推荐来源\s*=\s*(盒子文件|BoxFile)", RegexOptions.IgnoreCase)) return "source is not BoxFile";

            if (!File.Exists(BoxHeartbeatPath)) return "heartbeat missing";
            var payload = File.ReadAllText(BoxHeartbeatPath);
            const string marker = "\"ts\":";
            var start = payload.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return "heartbeat ts missing";
            start += marker.Length;
            while (start < payload.Length && char.IsWhiteSpace(payload[start])) start++;
            var end = start;
            while (end < payload.Length && char.IsDigit(payload[end])) end++;
            if (!long.TryParse(payload.Substring(start, end - start), out var heartbeatTs)) return "heartbeat ts invalid";
            var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - heartbeatTs;
            if (heartbeatTs <= 0) return "heartbeat ts <= 0";
            if (ageMs < -5000) return "heartbeat from the future (" + ageMs + "ms)";
            if (ageMs > 30000) return "heartbeat stale (" + ageMs + "ms)";
            // A timestamp alone can survive a crashed/restarted Box process.
            // When the producer publishes PIDs, require both the Box process
            // and this Hearthstone process to match the heartbeat. Legacy
            // heartbeat files without PIDs keep the timestamp-only behavior.
            var gamePid = ReadHeartbeatInt(payload, "gamePid");
            // The producer PID is useful for diagnostics, but it is not a
            // safe readiness gate: HSAng is a 32-bit Qt process and Unity's
            // Mono runtime can fail to inspect that PID even while the fresh
            // heartbeat and scoring files are valid. Timestamp freshness plus
            // the profile validation below is the authoritative bridge test.
            if (gamePid > 0 && gamePid != Process.GetCurrentProcess().Id)
                return "heartbeat belongs to Hearthstone PID " + gamePid + ", current PID " + Process.GetCurrentProcess().Id;
            if (requireScoring && (ArenaSettings.AutoDraft.Value || ArenaSettings.AutoRedraft.Value) &&
                ArenaSettings.UseBoxScores != null && ArenaSettings.UseBoxScores.Value &&
                !ArenaDraftRecommendationRuntime.CaptureReady(out var profileDetail))
                return "Arena recommended-choice capture unavailable (" + profileDetail + ")";
            return "ready";
        }
        catch (Exception ex)
        {
            return "heartbeat read failed: " + ex.GetType().Name;
        }
    }

    private static int ReadHeartbeatInt(string payload, string name)
    {
        var match = Regex.Match(payload ?? "", "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(?<n>-?\\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["n"].Value, out var value) ? value : 0;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using (var process = Process.GetProcessById(pid)) return !process.HasExited;
        }
        catch { return false; }
    }

    private void TryRecoverBoxExecutor()
    {
        if (_boxExecutorRecoveryAttempted || Time.unscaledTime < _nextBoxExecutorRecoveryCheck) return;
        _nextBoxExecutorRecoveryCheck = Time.unscaledTime + 3f;

        long heartbeatTs = 0;
        try
        {
            if (!File.Exists(BoxHeartbeatPath)) return;
            var payload = File.ReadAllText(BoxHeartbeatPath);
            var marker = "\"ts\":";
            var start = payload.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return;
            start += marker.Length;
            while (start < payload.Length && char.IsWhiteSpace(payload[start])) start++;
            var end = start;
            while (end < payload.Length && char.IsDigit(payload[end])) end++;
            long.TryParse(payload.Substring(start, end - start), out heartbeatTs);
        }
        catch
        {
            return;
        }

        var ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - heartbeatTs;
        if (heartbeatTs <= 0 || ageMs < -5000 || ageMs > 30000)
        {
            _boxExecutorRecoveryAttempted = false;
            return;
        }

        try
        {
            var configType = Type.GetType("HsMod.PluginConfig, HsMod");
            var enabledField = configType?.GetField("scriptRuntimeEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var runtimeEnabled = enabledField?.GetValue(null) as bool?;
            if (runtimeEnabled == true)
            {
                _boxExecutorRecoveryAttempted = true;
                return;
            }

            var botType = Type.GetType("HsMod.WildLadderBot, HsMod");
            var toggle = botType?.GetMethod("SetRuntimeEnabled", BindingFlags.Static | BindingFlags.NonPublic);
            if (toggle == null) return;
            toggle.Invoke(null, new object[] { false });
            toggle.Invoke(null, new object[] { true });
            _boxExecutorRecoveryAttempted = true;
            Log("Box heartbeat recovered during Arena gameplay; re-enabled native Box executor");
        }
        catch (Exception ex)
        {
            Log("Box executor recovery failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void DismissArenaAttentionPopups(SceneMgr.Mode mode, bool arenaGameplay)
    {
        if (!PopupActionsAllowed(mode.ToString())) return;

        // Functional dialogs keep their own callbacks. A generic Back can
        // cancel a purchase, disconnect the client, or close the user's Journal.
        if (mode == SceneMgr.Mode.DRAFT && HasOwnedTicketPurchase) TryConfirmTicketPurchase();
        var dismissed = mode == SceneMgr.Mode.DRAFT && TryConfirmPremiumRemovalPopup() ? 1 : 0;
        dismissed += DismissExplicitAttentionPopups(mode);
        if (dismissed > 0)
        {
            _dismissedPopupCount += dismissed;
            Log("Arena submitted known UI callbacks: count=" + dismissed + ", total=" + _dismissedPopupCount);
        }
        else LogActiveAttentionDiagnostics(mode.ToString());
    }

    private void LogActiveAttentionDiagnostics(string location)
    {
        if (Time.unscaledTime < _nextPopupDiagnostic || _attentionCandidateNames.Count == 0) return;
        var types = string.Join(",", _attentionCandidateNames.OrderBy(name => name, StringComparer.Ordinal));
        var key = location + ":" + types;
        if (key == _lastAttentionDiagnostic) return;
        _lastAttentionDiagnostic = key;
        _nextPopupDiagnostic = Time.unscaledTime + 30f;
        Log("active functional/unknown UI left open; location=" + location + "; types=" + types);
    }

    private static bool IsNativeArenaRewardState()
    {
        try
        {
            var manager = DraftManager.Get();
            var state = manager?.GetArenaState(ArenaSettings.IsUnderground).ToString();
            return state == "REWARDS" || state == "IN_REWARDS";
        }
        catch { return false; }
    }

    private static PropertyInfo FindZeroIndexProperty(Type type, string name, BindingFlags flags)
    {
        // Some client builds expose duplicate inherited properties with the
        // same name.  Type.GetProperty(name, flags) then throws
        // AmbiguousMatchException and prevents the popup cleanup loop from
        // reaching the functional ticket confirmation handler.
        return type.GetProperties(flags)
            .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal) &&
                                        property.GetIndexParameters().Length == 0);
    }

    private static MethodInfo FindZeroArgMethod(Type type, string name, BindingFlags flags)
    {
        return type.GetMethods(flags)
            .FirstOrDefault(method => string.Equals(method.Name, name, StringComparison.Ordinal) &&
                                      method.GetParameters().Length == 0);
    }

    private int DismissExplicitAttentionPopups(SceneMgr.Mode mode)
    {
        var running = HsModNativeArena.IsNativeArenaModeSelected() && HsModNativeArena.IsRuntimeEnabled();
        var scene = mode.ToString();
        if (!PopupActionsAllowed(scene)) return 0;
        var rewardState = IsNativeArenaRewardState();
        var dismissed = 0;
        _attentionCandidateNames.Clear();

        // One scan of behaviour components replaces dozens of whole-scene
        // scans. Classify the concrete type so a permitted base class cannot
        // accidentally match Journal, reconnect or another functional panel.
        foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (component == null || !component.gameObject.activeInHierarchy) continue;
            var type = component.GetType();
            var typeName = type.FullName ?? type.Name;
            var action = ArenaPopupPolicy.Decide(running, scene, typeName, rewardState,
                HasOwnedTicketPurchase, _ticketAcknowledgedPopups.Contains(component.GetInstanceID()));
            if (action == ArenaPopupAction.LeaveOpen)
            {
                if (_attentionCandidateNames.Count < 32 &&
                    !DecorativeAttentionTypeNames.Contains(type.Name) &&
                    (type.Name.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     type.Name.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    type.Name != "DialogManager" && type.Name != "PopupDisplayManagerBones")
                    _attentionCandidateNames.Add(typeName);
                continue;
            }

            try
            {
                if (_notificationCloses.ContainsKey(component.GetInstanceID())) continue;
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var shownProperty = FindZeroIndexProperty(type, "IsShown", flags);
                if (shownProperty != null && shownProperty.PropertyType == typeof(bool) &&
                    !Equals(shownProperty.GetValue(component, null), true)) continue;
                var shownMethod = FindZeroArgMethod(type, "IsShown", flags) ??
                    FindZeroArgMethod(type, "IsShowingToast", flags) ??
                    FindZeroArgMethod(type, "IsBannerVisible", flags);
                if (shownMethod != null && shownMethod.ReturnType == typeof(bool) &&
                    !Equals(shownMethod.Invoke(component, null), true)) continue;

                if (action == ArenaPopupAction.ConfirmOwnedTicketPurchase)
                {
                    if (TryAcknowledgeOwnedTicketResult(component)) dismissed++;
                    continue;
                }

                if (typeName == "WelcomeQuests")
                    WelcomeQuests.Hide();
                else if (typeName == "BoxProductBanner" || typeName == "ArenaRewardsClaimPopup")
                    component.gameObject.SetActive(false);
                else
                {
                    var close = typeName == "Hearthstone.Progression.QuestXpReward"
                        ? FindZeroArgMethod(type, "ClearAndHide", flags) : null;
                    close ??= FindZeroArgMethod(type, "Hide", flags) ??
                        FindZeroArgMethod(type, "Close", flags) ??
                        FindZeroArgMethod(type, "GoBack", flags);
                    if (close == null) continue;
                    var result = close.Invoke(component, null);
                    if (result is IEnumerator iterator)
                    {
                        if (StartNotificationClose(component, scene, typeName, iterator)) dismissed++;
                        continue;
                    }
                }
                dismissed++;
                Log("submitted native notification close callback type=" + typeName);
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= _nextPopupDiagnostic)
                {
                    _nextPopupDiagnostic = Time.unscaledTime + 10f;
                    Log("known notification callback deferred for " + typeName + ": " + ex.GetType().Name);
                }
            }
        }
        return dismissed;
    }

    private bool TryConfirmPremiumRemovalPopup()
    {
        try
        {
            var manager = DraftManager.Get();
            s_pendingPremiumField ??= typeof(DraftManager).GetField(
                "m_pendingRequestToDisablePremiums", InstancePrivate);
            var pending = manager != null && s_pendingPremiumField != null &&
                          (bool)s_pendingPremiumField.GetValue(manager);
            if (!pending) return false;
            foreach (var popup in UnityEngine.Object.FindObjectsByType<AlertPopup>(FindObjectsSortMode.None))
            {
                if (popup == null || !popup.gameObject.activeInHierarchy ||
                    popup.m_confirmButton == null || !popup.m_confirmButton.gameObject.activeInHierarchy ||
                    !popup.m_confirmButton.enabled)
                    continue;
                var isPremium = IsPremiumRemovalPopup(popup, manager);
                if (!isPremium) continue;
                popup.m_confirmButton.TriggerRelease();
                Log("confirmed native Arena premium-removal dialog before redraft");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log("Arena premium-removal dialog confirmation not ready: " + ex.GetType().Name + ": " + ex.Message);
        }
        return false;
    }

    private static bool IsPremiumRemovalPopup(AlertPopup popup, DraftManager manager)
    {
        try
        {
            s_alertPopupInfoField ??= typeof(AlertPopup).GetField("m_popupInfo", InstancePrivate);
            var info = s_alertPopupInfoField?.GetValue(popup);
            if (info == null) return false;
            s_premiumResponseCallbackField ??= info.GetType().GetField("m_responseCallback",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var callback = s_premiumResponseCallbackField?.GetValue(info) as Delegate;
            // Native PromptToDisablePremium wires this exact callback. A
            // pending flag alone must never confirm an unrelated AlertPopup.
            return ArenaPopupPolicy.CanConfirmPremiumRemoval(true, callback?.Target == manager,
                callback?.Method.DeclaringType?.FullName, callback?.Method.Name);
        }
        catch { return false; }
    }

    private bool HasReadyUndergroundDeck(DraftManager manager)
    {
        try
        {
            var deck = manager.GetDraftDeck(ArenaSettings.IsUnderground);
            if (deck == null) return false;
            var slots = deck.GetSlots();
            // A current Arena deck can contain fewer distinct slot objects
            // than its max slot because duplicate cards share one slot.  The
            // server's slot progress is card quantity, so sum each slot's
            // Count instead of comparing the collection length.
            var pendingEdit = ArenaDeckEditReceipts.Load(manager.IsUnderground());
            return slots != null && ArenaDeckEditPolicy.CanMatch(manager.GetArenaState().ToString(),
                slots.Sum(slot => slot?.Count ?? 0), manager.GetMaxSlot(), pendingEdit != null && pendingEdit.Stage != "Confirmed");
        }
        catch (Exception ex)
        {
            Log("Arena deck readiness check failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private bool TryPurchaseTicketWithGold()
    {
        try
        {
            if (!TicketPurchaseActionsAllowed()) return false;
            _ticketPurchaseAttempts++;
            var shop = Shop.Get();
            var page = shop?.ProductPageController?.CurrentProductPage;
            var widget = page?.WidgetComponent;
            if (widget != null)
            {
                // Never buy the arbitrary product currently open in Shop.
                // Native Shop recognizes the ticket tag; the selected native
                // price must explicitly be GOLD (ALT does not always mean gold).
                if (!shop.IsTavernTicketProductPageOpen() || page.IsAnimating) return false;
                if (page.GetVariantQuantityByIndex(page.GetSelectedVariantIndex()) != 1)
                {
                    LogTicketOwnershipDeferred("native ticket page quantity is not one");
                    return false;
                }
                var product = page.GetSelectedVariant();
                var prices = (PopupProperty(product, "Prices") as IEnumerable)?.Cast<object>().ToArray();
                var goldIndex = prices == null ? -1 : Array.FindIndex(prices,
                    price => Convert.ToInt32(PopupProperty(price, "Currency") ?? -1) == (int)CurrencyType.GOLD);
                var nativeTryBuy = typeof(ProductPage).GetMethod("TryBuy", InstancePrivate,
                    null, new[] { typeof(int) }, null);
                if (goldIndex < 0 || nativeTryBuy == null ||
                    !BeginTicketPopupOwnership(TicketProductId(product), page))
                {
                    LogTicketOwnershipDeferred("native ticket product or GOLD price is unavailable");
                    return false;
                }
                BeginTicketPurchaseStats(DraftManager.Get());
                _submittingNativeTicketPurchase = true;
                // ProductPage.OnWidgetEvent dispatches directly to TryBuy.
                // Calling that same native handler keeps the order creation
                // inside our scope even when a widget queues events by frame.
                try { nativeTryBuy.Invoke(page, new object[] { goldIndex }); }
                finally { _submittingNativeTicketPurchase = false; }
                _ticketPurchaseStartedAt = Time.unscaledTime;
                Log("requested native tavern-ticket purchase with GOLD; product=" + _ticketProductId);
                return true;
            }

            // Some client builds expose the product page through ArenaStore
            // while Shop.ProductPageController.CurrentProductPage is null.
            // Invoke the same private native BuyWithGold path instead of
            // spinning on an unavailable generic shop widget.
            var storeType = Type.GetType("ArenaStore, Assembly-CSharp") ??
                            typeof(DraftManager).Assembly.GetType("ArenaStore");
            var getStore = storeType?.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var store = getStore?.Invoke(null, null);
            var buyWithGold = store?.GetType().GetMethod("BuyWithGold", InstancePrivate,
                null, new[] { typeof(UIEvent) }, null);
            var nativeProduct = store?.GetType().GetMethod("GetDraftTicketProduct",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(store, null);
            if (buyWithGold != null && BeginTicketPopupOwnership(TicketProductId(nativeProduct), null))
            {
                BeginTicketPurchaseStats(DraftManager.Get());
                _submittingNativeTicketPurchase = true;
                try { buyWithGold.Invoke(store, new object[] { null }); }
                finally { _submittingNativeTicketPurchase = false; }
                _ticketPurchaseStartedAt = Time.unscaledTime;
                Log("requested native ArenaStore BuyWithGold for tavern ticket");
                return true;
            }

            if (Time.unscaledTime >= _nextChoiceDiag)
            {
                _nextChoiceDiag = Time.unscaledTime + 5f;
                Log("native ticket purchase widget unavailable; shop=" + (shop != null) +
                    " page=" + (page != null) + " attempts=" + _ticketPurchaseAttempts);
            }
            return false;
        }
        catch (Exception ex)
        {
            Log("native ticket purchase not ready: " + ex.GetType().Name + ": " + ex.Message);
            // Once the native order exists a failed UI callback is not
            // permission to send another gold purchase.
            if (_ticketNativePurchaseRoutine != null) return true;
            return false;
        }
    }

    private bool TryConfirmTicketPurchase()
    {
        try
        {
            if (!HasOwnedTicketPurchase) return false;
            var popups = UnityEngine.Object.FindObjectsByType<AlertPopup>(FindObjectsSortMode.None);
            foreach (var popup in popups)
            {
                if (popup == null || !popup.gameObject.activeInHierarchy)
                    continue;
                if (TryAcknowledgeOwnedBalanceWarning(popup)) continue;
                if (popup.m_confirmButton == null) continue;
                if (!NativePopupButtonReady(popup.m_confirmButton))
                    continue;
                if (Equals(PopupField(popup, "m_wasPressed"), true)) continue;
                if (!IsOwnedTicketAlert(popup, out var callback))
                {
                    LogTicketOwnershipDeferred("AlertPopup callback is not the current native GOLD ticket order");
                    continue;
                }
                // This is purchase consent, not a successful receipt. Keep
                // later consent/result stages runnable for the same order.
                _ticketConfirmedCallbacks.Add(callback);
                _submittingNativeTicketPurchase = true;
                try { popup.m_confirmButton.TriggerRelease(); }
                finally { _submittingNativeTicketPurchase = false; }
                _ticketPurchaseStartedAt = Time.unscaledTime;
                Log("submitted owned native GOLD ticket confirmation: " + callback.Method.DeclaringType?.FullName + "." + callback.Method.Name);
            }
        }
        catch (Exception ex)
        {
            Log("native ticket purchase confirmation not ready: " + ex.GetType().Name + ": " + ex.Message);
        }
        return _ticketPurchaseConfirmed;
    }

    private int TryGetArenaBoxChoice(List<string> cards, DraftManager manager, bool hero, out bool pending, out string detail)
    {
        if (!ArenaDraftRecommendationRuntime.Installed)
        { pending = true; detail = "等待原生选牌候选组监听"; return 0; }
        var redraft = manager.IsRedrafting();
        var key = ArenaDraftRecommendationStream.Bucket(manager.IsUnderground(),
            manager.GetDraftDeck(manager.IsUnderground())?.ID ?? 0,
            redraft ? manager.GetRedraftDeck()?.ID ?? 0 : 0, redraft,
            redraft ? manager.GetRedraftSlot() : manager.GetSlot(), manager.GetSlotType().ToString(), cards);
        var choice = ArenaDraftRecommendationStream.Read(key, cards, out detail);
        pending = choice <= 0;
        return choice;
    }
    private void TryCloseStaleRewardUi(DraftManager manager, string arenaState)
    {
        if (manager == null || (arenaState != "DRAFTING" && arenaState != "REDRAFTING") ||
            Time.unscaledTime < _nextStaleRewardCleanup) return;
        _nextStaleRewardCleanup = Time.unscaledTime + 2f;
        LogLegendaryDraftOverlayDiagnostic(manager, arenaState);
        // An acknowledged chest may leave its visual behind. A normal draft
        // has no reward widget: never restart its landing animation, refill
        // the choice bucket or call OnMoveFromLanding from this cleanup.
        try
        {
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<DraftDisplay>())
            {
                if (candidate == null) continue;
                EnsureChoiceReflection(candidate);
                var choices = s_choicesField?.GetValue(candidate) as IList;
                if (!ArenaLifecyclePolicy.ShouldCloseStaleReward(arenaState, candidate.IsRewardsFlowPending,
                    manager.IsRewardChestReady(), choices?.Count ?? 0)) continue;
                if (!TryHideStaleRewardUi(candidate)) continue;
                _nextStaleRewardCleanup = Time.unscaledTime + 5f;
                Log("closed an acknowledged stale reward visual; preserved current draft choices and animation");
            }
        }
        catch (Exception ex)
        {
            _nextStaleRewardCleanup = Time.unscaledTime + 5f;
            Log("stale Arena reward visual cleanup deferred: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Read-only correlation for the known legendary package overlay issue.
    /// The native page name alone cannot prove that a card is visually covering
    /// the ordinary draft. Record the active popup/card objects together with
    /// the server slot and current DraftDisplay choices so the controller log
    /// can correlate a screenshot with actual slot progress.
    /// </summary>
    private void LogLegendaryDraftOverlayDiagnostic(DraftManager manager, string arenaState)
    {
        try
        {
            var display = GetDraftDisplay();
            var page = display?.m_arenaDraftScreenBroadcaster?.State ?? "missing";
            if (page.IndexOf("LEGENDARY", StringComparison.OrdinalIgnoreCase) < 0 &&
                page.IndexOf("BUCKET_POPUP", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            if (Time.unscaledTime < _nextLegendaryOverlayDiagnostic) return;

            EnsureChoiceReflection(display);
            var choiceIds = new List<string>();
            var choices = display == null ? null : s_choicesField?.GetValue(display) as IList;
            if (choices != null)
            {
                foreach (var item in choices)
                {
                    var id = s_cardIdField?.GetValue(item) as string;
                    if (string.IsNullOrEmpty(id) && s_actorField != null)
                        id = ReadActorCardId(s_actorField.GetValue(item));
                    if (!string.IsNullOrEmpty(id)) choiceIds.Add(id);
                    if (choiceIds.Count >= 4) break;
                }
            }

            var objects = new List<string>();
            foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                var typeName = component.GetType().FullName ?? component.GetType().Name;
                var objectName = component.gameObject.name ?? "";
                var probe = (typeName + "/" + objectName).ToLowerInvariant();
                if (!probe.Contains("popup") && !probe.Contains("legendary") &&
                    !probe.Contains("cardzoom") && !probe.Contains("card_zoom") &&
                    !probe.Contains("tooltip") && !probe.Contains("card_hand_")) continue;
                var entry = typeName + "@" + objectName + "#" + component.GetInstanceID();
                if (!objects.Contains(entry)) objects.Add(entry);
                if (objects.Count >= 16) break;
            }

            var key = page + ":" + manager.GetSlot() + "/" + manager.GetMaxSlot() + ":" +
                      string.Join(",", choiceIds) + ":" + string.Join(",", objects);
            if (key == _lastLegendaryOverlayDiagnostic && Time.unscaledTime < _nextLegendaryOverlayDiagnostic + 10f)
                return;
            _lastLegendaryOverlayDiagnostic = key;
            _nextLegendaryOverlayDiagnostic = Time.unscaledTime + 10f;
            Log("Arena draft legendary overlay diagnostic: page=" + page +
                "; serverState=" + arenaState +
                "; slot=" + manager.GetSlot() + "/" + manager.GetMaxSlot() +
                "; ordinaryDraftContinuing=" + (manager.GetSlot() > 0 && manager.GetMaxSlot() > 0) +
                "; display=" + (display != null) +
                "; displayId=" + (display != null ? display.GetInstanceID().ToString() : "0") +
                "; choiceCount=" + (choices?.Count ?? 0) +
                "; choiceIds=" + string.Join(",", choiceIds) +
                "; activeOverlayObjects=" + (objects.Count == 0 ? "none" : string.Join("|", objects)) +
                "; visualOcclusion=unverified; screenshotRequired=true");
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= _nextLegendaryOverlayDiagnostic)
            {
                _nextLegendaryOverlayDiagnostic = Time.unscaledTime + 10f;
                Log("Arena draft legendary overlay diagnostic unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
    private static void EnsureChoiceReflection(DraftDisplay display)
    {
        if (s_choicesField != null) return;
        s_choicesField = typeof(DraftDisplay).GetField("m_choices", InstancePrivate);
        var nested = typeof(DraftDisplay).GetNestedType("DraftChoice", BindingFlags.NonPublic);
        // DraftChoice exposes these payload fields publicly on the current
        // client. Include Public as well as NonPublic so card IDs are read
        // from the native choices instead of incorrectly falling back to the
        // first visual card every time.
        var choiceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        s_cardIdField = nested?.GetField("m_cardID", choiceFields);
        s_actorField = nested?.GetField("m_actor", choiceFields);
    }

    private static string ReadActorCardId(object actor)
    {
        try
        {
            if (actor == null) return "";
            var getDef = actor.GetType().GetMethod("GetEntityDef", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var def = getDef?.Invoke(actor, null);
            if (def == null) return "";
            var getCardId = def.GetType().GetMethod("GetCardId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return getCardId?.Invoke(def, null) as string ?? "";
        }
        catch { return ""; }
    }

    private static DraftDisplay GetDraftDisplay()
    {
        // The arena scene can briefly keep the previous reward/landing
        // DraftDisplay singleton alive while a new display is already active.
        // Prefer the display owned by the current ArenaDraftScreen, then scan
        // active displays for the one whose choice objects contain card ids.
        try
        {
            var screen = UnityEngine.Object.FindObjectOfType<ArenaDraftScreen>();
            var screenDisplay = screen?.GetDraftDisplay();
            if (IsLiveDraftDisplay(screenDisplay)) return screenDisplay;

            var all = UnityEngine.Object.FindObjectsOfType<DraftDisplay>();
            DraftDisplay fallback = null;
            foreach (var candidate in all)
            {
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                if (IsLiveDraftDisplay(candidate)) return candidate;
                fallback ??= candidate;
            }
            if (fallback != null) return fallback;
        }
        catch { return null; }
        return DraftDisplay.Get();
    }

    private static bool IsLiveDraftDisplay(DraftDisplay display)
    {
        if (display == null || !display.isActiveAndEnabled) return false;
        try
        {
            EnsureChoiceReflection(display);
            var choices = s_choicesField?.GetValue(display) as IList;
            if (choices == null || choices.Count == 0) return false;
            foreach (var item in choices)
            {
                var id = s_cardIdField?.GetValue(item) as string;
                if (!string.IsNullOrEmpty(id)) return true;
                if (s_actorField != null && !string.IsNullOrEmpty(ReadActorCardId(s_actorField.GetValue(item)))) return true;
            }
        }
        catch { }
        return false;
    }

    private static void Log(string message)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message;
        try { File.AppendAllText(EventLogPath, line + Environment.NewLine); } catch { }
        ArenaBoxBotPlugin.LogSource?.LogInfo(message);
    }
}




