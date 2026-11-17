using System.Reflection;
using System.Windows.Forms;

internal static class ArenaRunStopUiTests
{
    internal static void Run()
    {
        var type = typeof(ArenaController.ArenaStopBoundary).Assembly.GetType("ArenaController.MainForm")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var root = Path.Combine(Path.GetTempPath(), "arena-run-stop-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var form = (Form)Activator.CreateInstance(type, new object[] { false })!;
        object Field(string name) => type.GetField(name, flags)!.GetValue(form)!;
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(form, value);
        void Call(string name) => type.GetMethod(name, flags)!.Invoke(form, null);
        ((System.Windows.Forms.Timer)Field("_timer")).Stop();
        var settings = Field("_settings");
        settings.GetType().GetProperty("HsModDirectory")!.SetValue(settings, root);
        var mode = (ComboBox)Field("_mode");
        var button = (Button)Field("_stopAfterRun");
        for (var cycle = 0; cycle < 3; cycle++)
        foreach (var name in new[] { "标准", "狂野", "休闲", "酒馆战棋", "竞技场" })
        {
            mode.SelectedItem = name;
            Set("_running", true); Set("_starting", false); Set("_stopRequested", false);
            Call("CancelArenaRunStop"); Call("UpdateModeUi");
            Check(button.Enabled == (name == "竞技场"), "Run-stop activation must be Arena-only: " + name);
            foreach (var size in new[] { new System.Drawing.Size(1180, 760), new System.Drawing.Size(1440, 900) })
            {
                form.Size = size;
                form.PerformLayout();
                button.Parent!.Parent!.Parent!.PerformLayout();
                button.Parent.Parent.PerformLayout();
                button.Parent.PerformLayout();
                var activeButton = name == "竞技场" ? button : (Button)Field("_stop");

                Check(activeButton.Height >= TextRenderer.MeasureText(activeButton.Text, activeButton.Font).Height + 4,
                    "Stop text must fit at minimum/default window size: " + name + " / " + size);
            }
            Set("_starting", true); Call("UpdateArenaRunStopUi");
            Check(!button.Enabled, "Cannot request while startup identity is unknown");
            Set("_starting", false); Set("_running", false); Call("UpdateArenaRunStopUi");
            Check(!button.Enabled, "Idle mode cannot publish run-stop");
            if (name != "竞技场") continue;
            foreach (var arenaType in new[] { "普通竞技场", "地下竞技场" })
            {
                ((ComboBox)Field("_arenaMode")).SelectedItem = arenaType;
                Set("_running", true); Call("RequestArenaRunStop");
                Check((bool)Field("_arenaRunStopPending"), "User intent latched: " + arenaType);
                Check(!(bool)Field("_stopRequested"), "Full-round request must not invoke match-stop");
                Check(button.Text.Contains("已请求") && !button.Enabled, "Pending request has clear UI and no duplicate click");
                Call("RequestArenaRunStop");
                Check((bool)Field("_arenaRunStopPending"), "Repeated request is idempotent");
                Call("CancelArenaRunStop");
                Check(!(bool)Field("_arenaRunStopPending"), "Explicit shorter stop can cancel round intent");
            }
        }
        Console.WriteLine($"PASS: Arena run-stop controller UI state and separation, {checks} assertions / 3 cycles per mode; no live game or actions");
    }
}
