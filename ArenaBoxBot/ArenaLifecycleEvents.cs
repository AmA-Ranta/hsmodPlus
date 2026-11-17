using System;
using HarmonyLib;

namespace ArenaBoxBot;

internal static class ArenaLifecycleEvents
{
    private static bool s_installed;
    private static bool s_entryInstalled;
    [ThreadStatic] private static BeginObservation s_beginObservation;
    [ThreadStatic] private static EntrySubmission s_entrySubmission;
    private sealed class EntrySubmission
    {
        internal string Nonce;
        internal bool Underground;
    }
    private sealed class BeginObservation
    {
        internal DraftManager Manager;
        internal Network.BeginDraft Response;
        internal BeginObservation Previous;
    }
    internal static bool EntryReceiptHooksReady => s_entryInstalled;
    internal static void EnsureInstalled()
    {
        if (!s_installed) try
        {
            var original = AccessTools.Method(typeof(GameState), "OnGameOver", new[] { typeof(TAG_PLAYSTATE) });
            if (original == null) throw new MissingMethodException("GameState.OnGameOver");
            new Harmony("local.hsmod.arenaboxbot.results").Patch(original,
                prefix: new HarmonyMethod(typeof(ArenaLifecycleEvents), nameof(BeforeGameOver)));
            s_installed = true;
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena result hook unavailable: " + ex.Message);
        }
        if (!s_entryInstalled) try
        {
            var begin = AccessTools.Method(typeof(DraftManager), "OnBegin", Type.EmptyTypes);
            var response = AccessTools.Method(typeof(Network), "GetBeginDraft", Type.EmptyTypes);
            var submit = AccessTools.Method(typeof(Network), "DraftBegin", new[] { typeof(bool) });
            var sessionResponse = AccessTools.Method(typeof(DraftManager), "OnArenaSessionResponsePacket",
                new[] { typeof(PegasusUtil.ArenaSessionResponse), typeof(bool) });
            if (begin == null || response == null || response.ReturnType != typeof(Network.BeginDraft) ||
                submit == null || sessionResponse == null)
                throw new MissingMethodException("DraftManager.OnBegin / Network.GetBeginDraft response contract");
            var harmony = new Harmony("local.hsmod.arenaboxbot.entry-receipts");
            harmony.Patch(response, postfix: new HarmonyMethod(typeof(ArenaLifecycleEvents), nameof(CaptureBeginResponse)));
            harmony.Patch(begin, prefix: new HarmonyMethod(typeof(ArenaLifecycleEvents), nameof(BeforeBegin)),
                postfix: new HarmonyMethod(typeof(ArenaLifecycleEvents), nameof(AfterBegin)),
                finalizer: new HarmonyMethod(typeof(ArenaLifecycleEvents), nameof(FinalizeBegin)));
            harmony.Patch(submit, postfix: new HarmonyMethod(typeof(ArenaLifecycleEvents), nameof(AfterSubmission)));
            harmony.Patch(sessionResponse, postfix: new HarmonyMethod(typeof(ArenaLifecycleEvents), nameof(AfterSessionResponse)));
            s_entryInstalled = true;
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena entry response hook unavailable; new ticket entries blocked: " + ex.Message);
        }
    }

    private static void BeforeGameOver(TAG_PLAYSTATE __0)
    {
        try { ArenaController.RecordNativeGameOver(__0); }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("Arena result capture failed: " + ex.Message); }
    }

    private static void BeforeBegin(DraftManager __instance, out BeginObservation __state)
    {
        __state = new BeginObservation { Manager = __instance != null && !__instance.IsRedrafting() ? __instance : null,
            Previous = s_beginObservation };
        s_beginObservation = __state;
    }

    private static void CaptureBeginResponse(Network.BeginDraft __result)
    {
        if (s_beginObservation?.Manager != null) s_beginObservation.Response = __result;
    }

    private static void AfterBegin(BeginObservation __state)
    {
        try
        {
            if (__state?.Manager != null && __state.Response != null)
                ArenaController.RecordNativeArenaBegin(__state.Manager, __state.Response);
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena entry response capture failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally { RestoreBeginObservation(__state); }
    }

    private static Exception FinalizeBegin(Exception __exception, BeginObservation __state)
    {
        RestoreBeginObservation(__state);
        return __exception;
    }

    private static void RestoreBeginObservation(BeginObservation observation)
    {
        if (ReferenceEquals(s_beginObservation, observation)) s_beginObservation = observation?.Previous;
    }

    internal static void BeginEntrySubmission(string nonce, bool underground)
    {
        if (s_entrySubmission != null) throw new InvalidOperationException("Nested automatic ticket submission is prohibited");
        s_entrySubmission = new EntrySubmission { Nonce = nonce, Underground = underground };
    }

    internal static void EndEntrySubmission() => s_entrySubmission = null;

    private static void AfterSubmission(bool __0)
    {
        if (s_entrySubmission == null || s_entrySubmission.Underground != __0) return;
        try { ArenaController.RecordNativeArenaSubmission(s_entrySubmission.Nonce, __0); }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena native submission receipt failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void AfterSessionResponse(PegasusUtil.ArenaSessionResponse __0, bool __1)
    {
        try { ArenaController.RecordNativeArenaSessionResponse(__0, __1); }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena native session acknowledgement failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
