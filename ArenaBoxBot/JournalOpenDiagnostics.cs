using System;
using System.Reflection;
using HarmonyLib;

namespace ArenaBoxBot;

/// <summary>Observes native Journal requests without changing any UI decision.</summary>
internal static class JournalOpenDiagnostics
{
    private static bool s_installAttempted;
    private static int s_requestNumber;
    private static FieldInfo s_existingWidget;
    private static FieldInfo s_metadata;
    private static FieldInfo s_popupShowing;
    private static FieldInfo s_journalShowing;

    internal static void EnsureInstalled()
    {
        if (s_installAttempted) return;
        s_installAttempted = true;
        try
        {
            var buttonType = AccessTools.TypeByName("Hearthstone.Progression.JournalButton");
            var popupType = AccessTools.TypeByName("Hearthstone.Progression.JournalPopup");
            var request = AccessTools.Method(buttonType, "ShowJournal", Type.EmptyTypes);
            var shown = AccessTools.Method(popupType, "Show", Type.EmptyTypes);
            if (request == null || shown == null)
                throw new MissingMethodException("Native Journal request/Show method unavailable");
            s_existingWidget = AccessTools.Field(buttonType, "m_journalPopUpWidget");
            s_metadata = AccessTools.Field(buttonType, "m_journalMetaDatamodel");
            s_popupShowing = AccessTools.Field(typeof(PopupDisplayManager), "s_isShowing");
            s_journalShowing = AccessTools.Field(popupType, "s_isShowing");
            var harmony = new Harmony("local.hsmod.arenaboxbot.journal-diagnostics");
            harmony.Patch(request, prefix: new HarmonyMethod(typeof(JournalOpenDiagnostics), nameof(BeforeOpenRequest)));
            harmony.Patch(shown, postfix: new HarmonyMethod(typeof(JournalOpenDiagnostics), nameof(AfterNativeShow)));
            Write("request and native Show hooks installed");
        }
        catch (Exception ex)
        {
            Write("hook unavailable; error=" + ex.GetType().Name);
        }
    }

    // A void prefix cannot suppress the original. Every diagnostic read and
    // sink is isolated so a missing service/field cannot break a user's click.
    private static void BeforeOpenRequest(object __instance)
    {
        try
        {
            var request = ++s_requestNumber;
            Write("open requested; request=" + request +
                  "; boxButtons=" + Read(() =>
                  {
                      var box = Box.Get();
                      return box == null ? "missing" : box.IsInStateWithButtons().ToString();
                  }) +
                  "; popupManagerIsShowing=" + Read(() =>
                  {
                      var manager = PopupDisplayManager.Get();
                      return manager == null ? "missing" : manager.IsShowing.ToString();
                  }) +
                  "; existingWidget=" + Read(() => ObjectPresence(s_existingWidget, __instance)) +
                  "; metadata=" + Read(() => ObjectPresence(s_metadata, __instance)) +
                  "; popupSources={flag=" + Read(() => StaticFlag(s_popupShowing)) +
                  ",dialog=" + Read(() =>
                  {
                      var manager = DialogManager.Get();
                      return manager == null ? "missing" : manager.ShowingDialog().ToString();
                  }) +
                  ",welcomeQuests=" + Read(() => (WelcomeQuests.Get() != null).ToString()) +
                  ",narrative=" + Read(() =>
                  {
                      var manager = NarrativeManager.Get();
                      return manager == null ? "missing" : manager.IsShowingBlockingDialog().ToString();
                  }) +
                  ",banner=" + Read(() =>
                  {
                      var manager = BannerManager.Get();
                      return manager == null ? "missing" : manager.IsShowing.ToString();
                  }) +
                  ",xpGains=" + Read(() =>
                  {
                      var manager = Hearthstone.Progression.RewardXpNotificationManager.Get();
                      return manager == null ? "missing" : manager.IsShowingXpGains.ToString();
                  }) +
                  ",initialDownload=" + Read(() => InitialDownloadDialog.IsVisible.ToString()) + "}");
        }
        catch (Exception ex) { Write("request diagnostic failed; error=" + ex.GetType().Name); }
    }

    private static void AfterNativeShow()
    {
        try
        {
            // Show returning proves native code ran, not that its asynchronous
            // widget animation finished or that the page is visibly rendered.
            Write("native Show returned; latestRequest=" + s_requestNumber +
                  "; journalIsShowing=" + Read(() => StaticFlag(s_journalShowing)));
        }
        catch (Exception ex) { Write("Show diagnostic failed; error=" + ex.GetType().Name); }
    }

    private static string ObjectPresence(FieldInfo field, object instance)
    {
        if (field == null) return "unknown(field missing)";
        var value = field.GetValue(instance);
        if (value == null || value is UnityEngine.Object unityObject && unityObject == null) return "null";
        return "present";
    }

    private static string StaticFlag(FieldInfo field)
        => field == null ? "unknown(field missing)" : Convert.ToString(field.GetValue(null));

    private static string Read(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) { return "unknown(" + ex.GetType().Name + ")"; }
    }

    private static void Write(string message)
    {
        // Developer-only sink: no user status, automatic UI action, account
        // data, exception message, or per-frame polling is recorded here.
        try
        {
            ArenaBoxBotPlugin.LogSource?.LogDebug("Journal diagnostic: " + message);
        }
        catch { }
    }
}
