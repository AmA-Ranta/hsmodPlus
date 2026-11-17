using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

// A direct protocol send has no dragged UI card. A rejected send must still
// reach GameState's original cleanup instead of throwing in the UI rollback.
internal static class NativeBgRejectedRollbackGuard
{
    private const string CallbackBodyHash = "AF004F13499FCFE3F426BBB2A681A235EC0F76FB7D582E62A0F95A025F40BC06";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly FieldInfo LastOptions = typeof(GameState).GetField("m_lastOptions", Fields);
    private static readonly FieldInfo LastSelection = typeof(GameState).GetField("m_lastSelectedOption", Fields);
    private static FieldInfo SelectionField(string name) => LastSelection?.FieldType.GetField(name, Fields);
    private static readonly FieldInfo Main = SelectionField("m_main");
    private static readonly FieldInfo Target = SelectionField("m_target");
    private static readonly FieldInfo Sub = SelectionField("m_sub");
    private static readonly FieldInfo Position = SelectionField("m_position");
    private static readonly ConditionalWeakTable<GameState, Sent> Observations = new ConditionalWeakTable<GameState, Sent>();
    [ThreadStatic] private static GameState s_nativeScope;
    private static Harmony s_harmony;
    private static float s_nextInstall;
    private static MethodInfo s_receiverEntity;

    private sealed class Sent
    {
        internal Network.Options Packet;
        internal Network.Options.Option Option;
        internal int Index, MainId, Target, Sub, Position;
        internal long At;
        internal bool Skipped;
    }

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextInstall) return;
        s_nextInstall = Time.unscaledTime + 5;
        Harmony candidate = null;
        try
        {
            var execute = AccessTools.Method(AccessTools.TypeByName("HsMod.BgBoxAiBot"), "TryExecuteOption", new[] { typeof(GameState), typeof(Network.Options) });
            var send = AccessTools.Method(typeof(GameState), "SendOption", Type.EmptyTypes);
            var rejected = AccessTools.Method(typeof(GameState), "OnOptionRejected", new[] { typeof(int) });
            var rollback = AccessTools.Method(typeof(MoveMinionHoverTarget), "OnOptionRejected", new[] { typeof(Network.Options.Option), typeof(object) });
            var card = AccessTools.Field(typeof(MoveMinionHoverTarget), "m_lastDroppedCard");
            s_receiverEntity = AccessTools.PropertyGetter(typeof(MoveMinionHoverTarget), "m_entity");
            if (execute?.ReturnType != typeof(void) || send?.ReturnType != typeof(void) ||
                rejected?.ReturnType != typeof(void) || rollback?.ReturnType != typeof(void) ||
                card?.FieldType != typeof(Card) || s_receiverEntity?.ReturnType != typeof(Entity) ||
                LastOptions?.FieldType != typeof(Network.Options) || Main?.FieldType != typeof(int) ||
                Target?.FieldType != typeof(int) || Sub?.FieldType != typeof(int) || Position?.FieldType != typeof(int) ||
                !KnownCallback(rollback)) return;
            candidate = new Harmony("local.hsmod.arenaboxbot.bg-rejected-rollback");
            candidate.Patch(execute, prefix: Hook(nameof(BeginNative)), finalizer: Hook(nameof(EndNative)));
            candidate.Patch(send, postfix: Hook(nameof(ObserveSend)));
            candidate.Patch(rollback, prefix: Hook(nameof(BeforeRollback)));
            candidate.Patch(rejected, postfix: Hook(nameof(AfterRejected)));
            s_harmony = candidate;
            ArenaBoxBotPlugin.LogSource?.LogInfo("BG rejection rollback guard installed: exact observed native send only; no nonexistent dragged-card rollback; original rejection and cleanup retained, no ACK or native state writes");
        }
        catch (Exception ex)
        {
            try { candidate?.UnpatchSelf(); } catch { }
            ArenaBoxBotPlugin.LogSource?.LogWarning("BG rejection rollback guard unavailable: " + ex.GetType().Name);
        }
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(NativeBgRejectedRollbackGuard), name);

    internal static bool KnownCallback(MethodInfo method)
    {
        var bytes = method?.GetMethodBody()?.GetILAsByteArray();
        if (bytes == null) return false;
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "") == CallbackBodyHash;
    }

    private static void BeginNative(GameState __0, out GameState __state)
    {
        __state = s_nativeScope;
        s_nativeScope = __0;
    }

    private static Exception EndNative(Exception __exception, GameState __state)
    {
        s_nativeScope = __state;
        return __exception;
    }

    private static bool Selection(GameState state, out Network.Options packet, out object selected)
    {
        packet = null; selected = null;
        if (ReferenceEquals(state, null)) return false;
        packet = LastOptions?.GetValue(state) as Network.Options;
        selected = LastSelection?.GetValue(state);
        return packet?.List != null && selected != null;
    }

    // Observe the completed original send, never create or dispatch an action.
    private static void ObserveSend(GameState __instance)
    {
        if (ReferenceEquals(__instance, null)) return;
        lock (Observations)
        {
            Observations.Remove(__instance);
            try
            {
                // Any non-bot send invalidates an earlier bot observation.
                if (!ReferenceEquals(s_nativeScope, __instance) || !Selection(__instance, out var packet, out var selected)) return;
                var index = (int)Main.GetValue(selected);
                if (index < 0 || index >= packet.List.Count) return;
                var option = packet.List[index];
                if (option?.Main == null || option.Main.ID <= 0) return;
                Observations.Add(__instance, new Sent
                {
                    Packet = packet, Option = option, Index = index, MainId = option.Main.ID,
                    Target = (int)Target.GetValue(selected), Sub = (int)Sub.GetValue(selected),
                    Position = (int)Position.GetValue(selected), At = Stopwatch.GetTimestamp()
                });
            }
            catch { /* Unknown send contracts preserve the original path. */ }
        }
    }

    internal static bool ShouldRunRollback(GameState state, Network.Options.Option option, object droppedCard, int receiverEntityId)
    {
        if (!ReferenceEquals(droppedCard, null) || ReferenceEquals(state, null) || option?.Main == null) return true;
        lock (Observations)
        {
            if (!Observations.TryGetValue(state, out var sent) || !ReferenceEquals(sent.Option, option) ||
                receiverEntityId != sent.MainId || option.Main.ID != sent.MainId) return true;
            var age = (Stopwatch.GetTimestamp() - sent.At) / (double)Stopwatch.Frequency;
            if (age < 0 || age > 10 || !Selection(state, out var packet, out var selected) ||
                !ReferenceEquals(packet, sent.Packet) || (int)Main.GetValue(selected) != sent.Index ||
                (int)Target.GetValue(selected) != sent.Target || (int)Sub.GetValue(selected) != sent.Sub ||
                (int)Position.GetValue(selected) != sent.Position) return true;
            if (!sent.Skipped)
                ArenaBoxBotPlugin.LogSource?.LogWarning("BgAI服务器拒绝动作: packet=" + packet.ID +
                    "; option=" + sent.Index + "; main=" + sent.MainId + "; target=" + sent.Target +
                    "; no dragged UI card exists; skip only its null rollback; preserve rejection and original cleanup, no synthetic ACK");
            sent.Skipped = true;
            return false;
        }
    }

    private static bool BeforeRollback(MoveMinionHoverTarget __instance, Network.Options.Option __0, Card ___m_lastDroppedCard)
    {
        try
        {
            if (!ReferenceEquals(___m_lastDroppedCard, null) || ReferenceEquals(__instance, null)) return true;
            var entity = s_receiverEntity.Invoke(__instance, null) as Entity;
            return ReferenceEquals(entity, null) || ShouldRunRollback(GameState.Get(), __0, ___m_lastDroppedCard, entity.GetEntityId());
        }
        catch { return true; }
    }

    private static void AfterRejected(GameState __instance, int __0)
    {
        if (ReferenceEquals(__instance, null)) return;
        lock (Observations)
        {
            try
            {
                if (!Observations.TryGetValue(__instance, out var sent) || !sent.Skipped || sent.Packet.ID != __0) return;
                var cleared = LastOptions.GetValue(__instance) == null && LastSelection.GetValue(__instance) == null;
                ArenaBoxBotPlugin.LogSource?.LogInfo("BgAI拒绝动作原生清理: packet=" + __0 +
                    "; lastOptionsAndSelectionCleared=" + cleared + "; original handler returned; no replacement action or ACK");
                Observations.Remove(__instance);
            }
            catch { /* Diagnostic only, never replace the original cleanup. */ }
        }
    }
}
