using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

/// <summary>
/// Retains the native result-driven rotation request across collection loading.
/// Native HsMod remains responsible for legality, random selection, saving and UI resync.
/// </summary>
internal static class HsModNativeDecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly HsModNativeDeckRotationPolicy Policy = new HsModNativeDeckRotationPolicy();
    private static Harmony s_harmony;
    private static Type s_bot;
    private static Type s_config;
    private static MethodInfo s_switch, s_list, s_mode, s_getDeck, s_exclude, s_status, s_action;
    private static FieldInfo s_resolvedId, s_stopAfterGame, s_rotation;
    private static bool s_pumpCreated, s_installFailed, s_faulted, s_observedDisabled;
    private static float s_nextInstall, s_nextRetry;
    private static float s_nextFailureStop, s_nextErrorLog;
    private static string s_failure = "";
    private static string s_requestReason = "";
    private static int s_requestMode = -1;
    [ThreadStatic] private static bool s_replaying;
    [ThreadStatic] private static long s_replaySetDeckId;
    [ThreadStatic] private static bool s_replaySetBound, s_replaySaved;

    internal static void EnsureInstalled()
    {
        if (!s_pumpCreated)
        {
            s_pumpCreated = true;
            var go = new GameObject("HsModNativeDeckRotation");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<HsModNativeDeckRotationPump>();
        }
        if (s_harmony != null || s_installFailed || Time.realtimeSinceStartup < s_nextInstall) return;
        s_nextInstall = Time.realtimeSinceStartup + 5f;
        try
        {
            s_bot = Type.GetType("HsMod.WildLadderBot, HsMod");
            s_config = Type.GetType("HsMod.PluginConfig, HsMod");
            if (s_bot == null || s_config == null) return; // BepInEx can load HsMod later.
            var tick = RequiredMethod(s_bot, "Tick", Type.EmptyTypes, typeof(void));
            s_switch = RequiredMethod(s_bot, "SwitchToRandomDifferentDeck", new[] { typeof(string) }, typeof(void));
            s_list = RequiredMethod(s_bot, "ListSelectableDecks", Type.EmptyTypes, typeof(List<CollectionDeck>));
            s_mode = s_bot.GetMethod("SelectedPlayMode", Static, null, Type.EmptyTypes, null);
            if (s_mode == null || s_mode.ReturnType.FullName != "HsMod.LadderPlayModeChoice")
                throw new MissingMethodException("SelectedPlayMode native contract changed");
            s_getDeck = RequiredMethod(s_config, "GetScriptDeck", new[] { typeof(long).MakeByRefType(), typeof(string).MakeByRefType() }, typeof(void));
            var setDeck = RequiredMethod(s_config, "SetScriptDeck", new[] { typeof(long), typeof(string) }, typeof(void));
            s_exclude = RequiredMethod(s_bot, "RotationExcludeDeckId", new[] { typeof(long), typeof(long) }, typeof(long));
            s_status = RequiredMethod(s_bot, "SetStatusText", new[] { typeof(string) }, typeof(void));
            s_action = RequiredMethod(s_bot, "SetActionText", new[] { typeof(string) }, typeof(void));
            s_resolvedId = RequiredField(s_bot, "s_resolvedDeckId", typeof(long));
            s_stopAfterGame = RequiredField(s_bot, "s_stopAfterCurrentGame", typeof(bool));
            s_rotation = s_config.GetField("wildLadderAutoSwitchDeck", Static);
            if (s_rotation == null || !s_rotation.FieldType.IsGenericType ||
                s_rotation.FieldType.GetGenericTypeDefinition().FullName != "BepInEx.Configuration.ConfigEntry`1" ||
                s_rotation.FieldType.GetGenericArguments()[0] != typeof(bool))
                throw new MissingFieldException("wildLadderAutoSwitchDeck native contract changed");
            var harmony = new Harmony("local.hsmod.arenaboxbot.native-deck-rotation");
            var queueInstalled = false;
            try
            {
                // Patch the queue first: a rotation request must never be accepted without its queue gate.
                harmony.Patch(tick, prefix: Patch(nameof(TickPrefix)), transpiler: Patch(nameof(QueueTranspiler)));
                queueInstalled = true;
                harmony.Patch(s_switch, prefix: Patch(nameof(SwitchPrefix)));
                harmony.Patch(setDeck, prefix: Patch(nameof(NativeSetPrefix)), postfix: Patch(nameof(NativeSetPostfix)));
                s_harmony = harmony;
            }
            catch
            {
                // If the second patch fails, keep the working precise queue gate fail-closed.
                // A contract failure must not restore the old deck's automated queue.
                if (queueInstalled) s_harmony = harmony;
                else harmony.UnpatchSelf();
                throw;
            }
            ArenaBoxBotPlugin.LogSource?.LogInfo("HsMod 卡组自动轮换等待与排队保护已安装；使用原生筛选、保存及 UI 选牌");
        }
        catch (Exception ex)
        {
            s_installFailed = true;
            s_failure = "卡组自动轮换保护安装失败，自动脚本将安全停止；请修复插件后重启游戏。";
            LogFailureDetails(s_failure, ex);
            TryStopFailure(s_failure);
        }
    }

    internal static void Pump()
    {
        EnsureInstalled();
        if (s_installFailed)
        {
            TryStopFailure(s_failure);
            return;
        }
        if (Policy.Pending || s_faulted) ServicePending();
    }

    private static HarmonyMethod Patch(string name) => new HarmonyMethod(typeof(HsModNativeDecks).GetMethod(name, Static));

    private static MethodInfo RequiredMethod(Type type, string name, Type[] parameters, Type returns)
    {
        var method = type.GetMethod(name, Static, null, parameters, null);
        if (method == null || method.ReturnType != returns) throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static FieldInfo RequiredField(Type type, string name, Type fieldType)
    {
        var field = type.GetField(name, Static);
        if (field == null || field.FieldType != fieldType) throw new MissingFieldException(type.FullName, name);
        return field;
    }

    private static object ConfigValue(string name)
    {
        var entry = s_config?.GetField(name, Static)?.GetValue(null);
        return entry?.GetType().GetProperty("Value")?.GetValue(entry, null);
    }

    // Safety scope does not invoke SelectedPlayMode or GetScriptDeck. A deck getter failure
    // cannot prevent stopping a verified constructed menu, and unknown modes never stop Arena/BG.
    private static NativeDeckRotationContext ReadSafeContext()
    {
        var context = new NativeDeckRotationContext();
        context.Mode = -1;
        if (s_config == null) return context;
        context.RuntimeEnabled = HsModNativeArena.IsRuntimeEnabled();
        var nativeMode = ConfigValue("wildLadderPlayMode");
        if (nativeMode != null && nativeMode.GetType().FullName == "HsMod.LadderPlayModeChoice")
            context.Mode = Convert.ToInt32(nativeMode);
        context.RotationEnabled = ConfigValue("wildLadderAutoSwitchDeck") is bool enabled && enabled;
        context.StopAfterGame = (s_stopAfterGame ?? s_bot?.GetField("s_stopAfterCurrentGame", Static))?.GetValue(null) is bool stop && stop;
        var scene = SceneMgr.Get();
        var mode = scene?.GetMode();
        context.Gameplay = mode == SceneMgr.Mode.GAMEPLAY;
        context.MenuReady = scene != null && !scene.IsTransitioning() &&
            (mode == SceneMgr.Mode.HUB || mode == SceneMgr.Mode.TOURNAMENT);
        context.Constructed = context.Mode >= 0 && context.Mode <= 2 && !HsModNativeArena.IsNativeArenaModeSelected() &&
            mode != SceneMgr.Mode.DRAFT && mode?.ToString().IndexOf("BACON", StringComparison.Ordinal) != 0;
        if (context.Gameplay)
        {
            var game = GameMgr.Get();
            context.Constructed &= game != null && !game.IsArena() && !game.IsUndergroundArena() && !game.IsBattlegrounds();
        }
        return context;
    }

    private static NativeDeckRotationContext ReadContext(NativeDeckRotationContext context = null)
    {
        context = context ?? ReadSafeContext();
        if (!context.Constructed) return context;
        var getter = s_getDeck ?? s_config.GetMethod("GetScriptDeck", Static);
        if (getter == null) throw new MissingMethodException("GetScriptDeck");
        var args = new object[] { 0L, "" };
        getter.Invoke(null, args);
        context.DeckId = Convert.ToInt64(args[0]);
        context.DeckName = Convert.ToString(args[1]) ?? "";
        return context;
    }

    private static NativeDeckRotationContext ReadStoredDeckContext()
    {
        var context = ReadSafeContext();
        var id = ConfigValue("wildLadderDeckId");
        var name = ConfigValue("wildLadderDeckName");
        if (id == null || name == null) throw new InvalidOperationException("Native deck memory entries are unavailable");
        context.DeckId = Convert.ToInt64(id);
        context.DeckName = Convert.ToString(name) ?? "";
        return context;
    }

    private static void RetainFailedReplayIdentity()
    {
        try { Policy.RetainFailedReplayIdentity(ReadStoredDeckContext()); }
        catch (Exception ex) { LogFailureDetails("卡组轮换失败后的内存身份读取失败", ex); }
    }

    private static bool SwitchPrefix(string reason)
    {
        if (s_replaying) return true;
        try
        {
            var safe = ReadSafeContext();
            if (!safe.Constructed || !safe.RuntimeEnabled || !safe.RotationEnabled || safe.StopAfterGame) return true;
            s_requestReason = reason;
            s_requestMode = safe.Mode;
            var context = ReadContext(safe);
            if (!Policy.Pending)
            {
                var resolved = (long)s_resolvedId.GetValue(null);
                var excluded = (long)s_exclude.Invoke(null, new object[] { resolved, context.DeckId });
                Policy.Begin(context, excluded, reason, Time.realtimeSinceStartup);
                s_nextRetry = 0;
                s_faulted = false;
                ArenaBoxBotPlugin.LogSource?.LogInfo("卡组轮换等待：" + reason + "；当前卡组 ID=" + excluded + "；等待收藏列表后调用原生轮换，禁止旧牌自动排队");
            }
            return false;
        }
        catch (Exception ex)
        {
            Fail("卡组轮换读取失败，暂停自动排队；恢复后重新开启脚本可重试。", ex);
            return false;
        }
    }

    // Never suppress Tick: HUB navigation, initial Tournament loading and GAMEPLAY must continue.
    private static void TickPrefix() { if (Policy.Pending || s_faulted) ServicePending(); }

    private static void ServicePending()
    {
        var replayAttempted = false;
        try
        {
            var safe = ReadSafeContext();
            if (!s_faulted && (!safe.RuntimeEnabled || !safe.Constructed || !safe.RotationEnabled || safe.StopAfterGame ||
                (Policy.Pending && safe.Mode != Policy.Mode)))
            { Cancel("脚本停止，或模式、轮换开关已改变"); return; }
            if (s_faulted)
            {
                if (!safe.Constructed || !safe.RotationEnabled || safe.StopAfterGame ||
                    (Policy.Pending ? safe.Mode != Policy.Mode : safe.Mode != s_requestMode))
                { Cancel("模式或轮换开关已改变"); return; }
                if (!safe.RuntimeEnabled) { s_observedDisabled = true; return; }
                if (!s_observedDisabled) { if (TryStopFailure(s_failure)) s_observedDisabled = true; return; }
            }
            var context = ReadContext(safe);
            if (s_faulted)
            {
                if (Policy.Pending && !Policy.Matches(context)) { Cancel("手动卡组已改变"); return; }
                if (!Policy.Pending)
                {
                    // Recover a threshold request whose first identity read failed before Begin.
                    var excluded = (long)s_exclude.Invoke(null, new object[] { (long)s_resolvedId.GetValue(null), context.DeckId });
                    Policy.Begin(context, excluded, s_requestReason, Time.realtimeSinceStartup);
                }
                // A fresh off -> on is a new user start; retry the retained request before any queue.
                s_faulted = false;
                s_observedDisabled = false;
                Policy.Restart(Time.realtimeSinceStartup);
                s_nextRetry = 0;
                ArenaBoxBotPlugin.LogSource?.LogInfo("卡组轮换：脚本重新开启，重新等待原生合法卡组（最多 60 秒）");
            }
            bool ready = false;
            int alternatives = 0;
            var now = Time.realtimeSinceStartup;
            if (Policy.Matches(context) && context.RuntimeEnabled && context.MenuReady && !context.Gameplay && now >= s_nextRetry)
            {
                s_nextRetry = now + 1f;
                var decks = s_list.Invoke(null, null) as IList;
                ready = decks != null;
                if (decks != null)
                    foreach (CollectionDeck deck in decks)
                        if (deck != null && deck.ID != Policy.ExcludeDeckId) alternatives++;
            }
            var action = Policy.Observe(context, ready, alternatives, now);
            if (action == NativeDeckRotationAction.Cancel) { Cancel("脚本停止，或模式、手动卡组、轮换开关已改变"); return; }
            if (action == NativeDeckRotationAction.Stop)
            {
                Fail("卡组自动轮换等待超过 60 秒：原生收藏列表未就绪或没有其他合法卡组；脚本已停止，未用旧卡组排队。恢复后重新开启脚本可重试。");
                return;
            }
            if (action != NativeDeckRotationAction.Retry) return;
            try
            {
                replayAttempted = true;
                s_replaySetDeckId = 0;
                s_replaySetBound = s_replaySaved = false;
                s_replaying = true;
                s_switch.Invoke(null, new object[] { Policy.Reason });
            }
            catch (Exception ex)
            {
                // Use direct memory entries, independent of the native getter that may itself fail.
                RetainFailedReplayIdentity();
                Fail("卡组轮换或配置保存失败，自动排队已暂停；恢复后重新开启脚本可重试。", ex);
                return;
            }
            finally { s_replaying = false; }
            var selected = ReadStoredDeckContext();
            if (HsModNativeDeckRotationPolicy.CanConfirmNativeSet(selected.DeckId, Policy.ExcludeDeckId,
                s_replaySetDeckId, s_replaySetBound) && Policy.Complete(selected.DeckId))
                ArenaBoxBotPlugin.LogSource?.LogInfo("卡组轮换已由原生方法完成并重置选牌缓存；新 ID=" + selected.DeckId +
                    "；原生保存=" + (s_replaySaved ? "已完成" : "自动保存关闭，保留原生设置") + "；等待原生 UI 选中一致及稳定后排队");
            else if (selected.DeckId != Policy.DeckId || !string.Equals(selected.DeckName, Policy.DeckName, StringComparison.Ordinal))
            {
                Policy.RetainFailedReplayIdentity(selected);
                Fail("原生轮换未确认选择其他卡组，自动排队已暂停；恢复后重新开启脚本可重试。");
            }
        }
        catch (Exception ex)
        {
            if (replayAttempted) RetainFailedReplayIdentity();
            Fail("卡组轮换读取失败，暂停自动排队；恢复后重新开启脚本可重试。", ex);
        }
    }

    private static void Cancel(string reason)
    {
        Policy.Cancel();
        s_faulted = false;
        s_observedDisabled = false;
        s_requestReason = "";
        s_requestMode = -1;
        ArenaBoxBotPlugin.LogSource?.LogInfo("取消等待卡组轮换：" + reason);
    }

    private static void Fail(string message, Exception exception = null)
    {
        LogFailureDetails(message, exception);
        s_failure = message;
        s_faulted = true;
        if (TryStopFailure(message)) s_observedDisabled = true;
    }

    private static bool TryStopFailure(string message)
    {
        if (Time.realtimeSinceStartup < s_nextFailureStop) return false;
        s_nextFailureStop = Time.realtimeSinceStartup + 5f;
        try { return StopIfSafe(ReadSafeContext(), message); }
        catch (Exception ex)
        {
            LogFailureDetails("卡组轮换停止状态读取失败；自动排队保护保持阻断", ex);
            return false;
        }
    }

    private static bool StopIfSafe(NativeDeckRotationContext context, string message)
    {
        if (!HsModNativeDeckRotationPolicy.CanSafelyStop(context)) return false;
        if (!HsModNativeArena.SetRuntimeEnabled(false))
        {
            LogFailureDetails("卡组轮换无法关闭原生脚本；自动排队保护保持阻断：" + message, null);
            return false;
        }
        s_status?.Invoke(null, new object[] { message });
        s_action?.Invoke(null, new object[] { "已停止；恢复后重新开启脚本可重试卡组轮换" });
        return true;
    }

    private static string Describe(Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
        string message;
        try { message = ex.Message; }
        catch { message = "exception message unavailable"; }
        return ex.GetType().Name + ": " + message;
    }

    private static void NativeSetPrefix(out NativeSetReceipt __state)
    {
        __state = null;
        if (!s_replaying) return;
        var name = s_config.GetField("wildLadderDeckName", Static)?.GetValue(null) as ConfigEntry<string>;
        var id = s_config.GetField("wildLadderDeckId", Static)?.GetValue(null) as ConfigEntry<long>;
        if (name != null && id != null && name.ConfigFile != null)
            __state = new NativeSetReceipt { SaveRequested = name.ConfigFile.SaveOnConfigSet };
    }

    private static void NativeSetPostfix(long __0, NativeSetReceipt __state)
    {
        // A native early return from Switch never calls Set. Only a normal
        // Set return with bound entries confirms this replay; a Save exception
        // never reaches this postfix. Preserve the native saving preference.
        if (!s_replaying || __state == null) return;
        s_replaySetDeckId = __0;
        s_replaySetBound = true;
        s_replaySaved = __state.SaveRequested;
    }

    private sealed class NativeSetReceipt { internal bool SaveRequested; }

    private static void LogFailureDetails(string message, Exception exception)
    {
        if (Time.realtimeSinceStartup < s_nextErrorLog) return;
        s_nextErrorLog = Time.realtimeSinceStartup + 5f;
        ArenaBoxBotPlugin.LogSource?.LogError(message);
        if (exception != null)
        {
            // Some Unity exceptions cannot format ToString in an offline CLR host.
            // Error reporting must never replace the original failure with a new exception.
            for (var depth = 0; exception != null && depth < 8; depth++, exception = exception.InnerException)
            {
                string stack;
                try { stack = exception.StackTrace ?? ""; }
                catch { stack = "stack unavailable"; }
                ArenaBoxBotPlugin.LogSource?.LogError("卡组轮换开发诊断：" + Describe(exception) + "\n" + stack);
            }
        }
    }

    private static bool CanSubmitConstructedMatch()
    {
        try
        {
            var context = ReadSafeContext();
            if (!context.Constructed) return true;
            if (!context.RotationEnabled)
            {
                if (Policy.Pending || s_faulted) Cancel("自动轮换已关闭");
                return context.RuntimeEnabled;
            }
            var pendingOnEntry = Policy.Pending || s_faulted;
            if (pendingOnEntry || s_faulted) ServicePending();
            if (!HsModNativeDeckRotationPolicy.CanSubmitAfterService(pendingOnEntry, context.RuntimeEnabled,
                Policy.Pending, s_faulted || s_installFailed))
            {
                var message = s_faulted || s_installFailed ? s_failure :
                    Policy.Pending ? "等待卡组自动轮换：收藏列表加载或合法候选未就绪，暂停自动排队" :
                    "卡组轮换已完成，等待下一次原生选牌检查后自动排队";
                s_status?.Invoke(null, new object[] { message });
                return false;
            }
            return true;
        }
        catch (Exception ex) { Fail("卡组自动排队保护读取失败，暂停自动排队。", ex); return false; }
    }

    // This targets only WildLadderBot.Tick's automated queue, never the player's PlayGame method.
    private static IEnumerable<CodeInstruction> QueueTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod)
    {
        var code = instructions.ToList();
        var begin = RequiredMethod(__originalMethod.DeclaringType, "BeginMatchSubmission", new[] { typeof(float) }, typeof(void));
        var matches = code.Select((instruction, index) => new { instruction, index }).Where(x => x.instruction.Calls(begin)).ToList();
        if (matches.Count != 1) throw new InvalidOperationException("Native Tick must have exactly one BeginMatchSubmission call");
        var index = matches[0].index - 1;
        if (index < 1 || !(code[index].operand is MethodInfo time) || code[index].opcode != OpCodes.Call ||
            time.DeclaringType.FullName != "UnityEngine.Time" || time.Name != "get_realtimeSinceStartup" ||
            code[index - 1].opcode != OpCodes.Stfld || code[index].blocks.Count != 0)
            throw new InvalidOperationException("Native Tick queue no longer has the reviewed empty stack Time/BeginMatchSubmission boundary");
        // Native F905's submission try begins at the following tray load, after BeginMatchSubmission.
        // Reject a future layout that would put our ret inside any try/handler region.
        var depth = 0;
        for (var i = 0; i < index; i++)
            foreach (var block in code[i].blocks)
            {
                if (block.blockType == ExceptionBlockType.BeginExceptionBlock) depth++;
                if (block.blockType == ExceptionBlockType.EndExceptionBlock) depth--;
            }
        if (depth != 0) throw new InvalidOperationException("Native Tick queue guard boundary is inside an exception region");
        var continueLabel = generator.DefineLabel();
        var gate = new CodeInstruction(OpCodes.Call, typeof(HsModNativeDecks).GetMethod(nameof(CanSubmitConstructedMatch), Static));
        gate.labels.AddRange(code[index].labels);
        code[index].labels.Clear();
        code[index].labels.Add(continueLabel);
        code.InsertRange(index, new[] { gate, new CodeInstruction(OpCodes.Brtrue, continueLabel), new CodeInstruction(OpCodes.Ret) });
        return code;
    }
}

internal sealed class HsModNativeDeckRotationPump : MonoBehaviour
{
    private void Update() { HsModNativeDecks.Pump(); }
}
