namespace HsmodPlus;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceName = "hsmodPlus-controller-single-instance";
    private System.Threading.Mutex? _singleInstance;

    /// <summary>
    /// The controller once disappeared mid-run and Windows relaunched a fresh
    /// process, but nothing recorded why. Persist every unhandled exception so a
    /// silent exit becomes diagnosable instead of guesswork.
    /// </summary>
    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            var directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ArenaBoxController");
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "controller-crash.log");
            var text = DateTime.Now.ToString("O") + Environment.NewLine +
                       (exception == null ? "unknown exception" : exception.ToString()) +
                       Environment.NewLine + new string('-', 80) + Environment.NewLine;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.IO.File.AppendAllText(path, text, System.Text.Encoding.UTF8);
                    return;
                }
                catch (System.IO.IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
        catch
        {
            // Never let diagnostics become the failure.
        }
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.Handled = false;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        // One controller per machine: two copies would each drive the same game
        // session (and the second copy's window can shadow the first one for UI
        // automation). A named mutex makes the duplicate exit immediately.
        try
        {
            _singleInstance = new System.Threading.Mutex(true, SingleInstanceName, out var createdNew);
            if (!createdNew)
            {
                // A modal dialog here would block before Shutdown(), leaving a
                // second controller process alive behind an invisible message box
                // (observed: three instances running at once). Exit immediately.
                Shutdown();
                return;
            }
        }
        catch
        {
            // Mutex unavailable: keep the normal single-process behaviour.
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hsmodPlus-wpf-crash.log"),
                    DateTime.Now.ToString("O") + Environment.NewLine + ex);
            }
            catch { }
            throw;
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
