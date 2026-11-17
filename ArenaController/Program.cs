using System;
using System.IO;
using System.Windows.Forms;

namespace ArenaController;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            var startOnShown = Environment.GetCommandLineArgs()
                .Skip(1)
                .Any(arg => string.Equals(arg, "--start", StringComparison.OrdinalIgnoreCase));
            // The target machines commonly use 150%–200% scaling.  Opt into
            // per-monitor coordinates before creating any WinForms control so
            // the 1440x900 dashboard is centered in the real work area instead
            // of being DPI-virtualized off the right side of the screen.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(startOnShown));
        }
        catch (Exception ex)
        {
            // Keep startup failures diagnosable even when this WinExe has no
            // console attached.  Keep diagnostics in the user's application
            // data directory so a portable package never writes loose files
            // beside the executable (or into a drive root).
            var dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.GetTempPath();
            var directory = Path.Combine(dataRoot, "ArenaBoxController");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "controller-startup-error.log");
            File.WriteAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}", System.Text.Encoding.UTF8);
            try { MessageBox.Show($"中控启动失败，详细信息已写入：{path}\n\n{ex.Message}", "炉石脚本中控", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { /* preserve the diagnostic file if UI creation also fails */ }
        }
    }
}
