using System.Collections;
using System.Reflection;
using System.Windows.Forms;

internal static class ShellLayoutTests
{
    internal static void Run()
    {
        var type = typeof(ArenaController.ArenaStopBoundary).Assembly.GetType("ArenaController.MainForm")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var form = (Form)Activator.CreateInstance(type, new object[] { false })!;
        object Field(string name) => type.GetField(name, flags)!.GetValue(form)!;

        ((System.Windows.Forms.Timer)Field("_timer")).Stop();
        Check(form.MinimumSize.Width >= 1120 && form.MinimumSize.Height >= 720,
            "Shell must keep the documented minimum window size (1120x720)");

        var pages = (IDictionary)Field("_shellPages");
        Check(pages.Count == 6, "Shell must expose exactly six workspace pages");
        foreach (var expected in new[] { "dashboard", "instances", "plan", "arena", "notify", "support" })
            Check(pages.Contains(expected), "Shell page missing: " + expected);
        foreach (DictionaryEntry entry in pages)
            Check(((Control)entry.Value!).Dock == DockStyle.Fill, "Shell page must fill the workspace: " + entry.Key);

        var nav = (IList)Field("_navButtons");
        Check(nav.Count == 6, "Sidebar must expose one navigation item per page");
        var selected = 0;
        foreach (var item in nav)
        {
            var selectedField = item.GetType().GetProperty("Selected", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            if ((bool)selectedField.GetValue(item)!) selected++;
        }
        Check(selected == 1, "Exactly one navigation item may be selected");

        var dashboard = (Control)pages["dashboard"]!;
        var shellHost = dashboard.Parent!;
        Check(shellHost != null, "Dashboard must be hosted by the workspace container");

        var apply = (Button)Field("_applySettings");
        Check(apply.Height >= TextRenderer.MeasureText(apply.Text, apply.Font).Height + 4,
            "Apply button text must fit its action-bar cell at the minimum window size");

        foreach (var name in new[] { "_start", "_stop" })
        {
            var button = (Button)Field(name);
            Check(button.Height >= TextRenderer.MeasureText(button.Text, button.Font).Height + 4,
                name + " text must fit its card cell at the default window size");
        }

        var cards = 0;
        void CountCards(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child.GetType().Name == "CardPanel") cards++;
                CountCards(child);
            }
        }
        CountCards(form);
        Check(cards >= 5, "Shell must compose its workspace from cards, found: " + cards);

        Check(form.BackColor.R > 200 && form.BackColor.R < 255 && form.BackColor.G > 200, "Shell must use the light canvas token");
        Check(Field("_kpiInstances") != null && Field("_kpiSchedule") != null && Field("_kpiArena") != null && Field("_kpiNotify") != null,
            "Dashboard must compose four KPI cards");
        Check(Field("_banner") != null, "Dashboard must show the pending-items banner");
        Check(Field("_weekPlan") != null, "Dashboard must embed the weekly plan strip");
        Check(((Array)Field("_activityText")).Length == 5, "Activity feed must expose five recent rows");
        Console.WriteLine($"PASS: desktop shell template, {checks} assertions / 6 pages / KPI cards / weekly plan / activity feed");
    }
}
