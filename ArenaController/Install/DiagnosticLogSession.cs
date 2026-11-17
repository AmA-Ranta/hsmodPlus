using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ArenaController;

/// <summary>Each operation has a simple user log and a durable, incremental developer log.</summary>
internal sealed class DiagnosticLogSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private bool _completed;
    private readonly List<string> _writeErrors = new();
    private readonly Dictionary<string, DurableLogWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    public string Id { get; } = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
    public string? DeveloperPath { get; private set; }
    public string? UserPath { get; private set; }
    public string Stage { get; private set; } = "begin";
    public DiagnosticLines DeveloperLines { get; }
    public IReadOnlyList<string> WriteErrors => _writeErrors;

    public DiagnosticLogSession(string operation, string? packageDirectory = null, string? logDirectory = null)
    {
        DeveloperLines = new DiagnosticLines(value => Write(DeveloperPath, value));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
        foreach (var directory in new[] { logDirectory ?? Path.Combine(local, "hsmodPlus", "logs"), Path.Combine(Path.GetTempPath(), "hsmodPlus", "logs") }.Distinct())
        {
            try
            {
                Directory.CreateDirectory(directory);
                var prefix = Path.Combine(directory, operation + "-" + Id);
                DeveloperPath = prefix + "-developer.log";
                UserPath = prefix + "-user.log";
                File.WriteAllText(DeveloperPath, "", new UTF8Encoding(false));
                File.WriteAllText(UserPath, "", new UTF8Encoding(false));
                Prune(directory);
                break;
            }
            catch (Exception ex)
            {
                DeveloperPath = UserPath = null;
                _writeErrors.Add("log.create=" + directory + ";" + ex);
            }
        }
        DeveloperLines.Add("operation.id=" + Id + ";operation=" + operation);
        DeveloperLines.Add("packageDirectory=" + packageDirectory);
        DeveloperLines.Add("baseDirectory=" + AppContext.BaseDirectory + ";workingDirectory=" + Environment.CurrentDirectory);
        DeveloperLines.Add("processId=" + Environment.ProcessId + ";processPath=" + Environment.ProcessPath);
        DeveloperLines.Add("version=" + typeof(DiagnosticLogSession).Assembly.GetName().Version +
            ";runtime=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription +
            ";architecture=" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture +
            ";os=" + Environment.OSVersion);
        foreach (var error in _writeErrors.ToArray()) DeveloperLines.Add(error);
    }

    public void Step(string stage)
    {
        Stage = stage;
        DeveloperLines.Add("stage=" + stage);
    }

    public void User(string message)
    {
        Write(UserPath, message);
        DeveloperLines.Add("user=" + message);
    }

    public void Complete(bool success)
    {
        if (_completed) return;
        DeveloperLines.Add("outcome=" + (success ? "success" : "failure") + ";lastStage=" + Stage + ";durationMs=" + _elapsed.ElapsedMilliseconds);
        _completed = true;
    }

    private void Write(string? path, string value)
    {
        if (path == null) return;
        lock (_gate)
        {
            if (!_writers.TryGetValue(path, out var writer))
            {
                writer = new DurableLogWriter(path, Path.Combine(Path.GetTempPath(), "hsmodPlus", "logs", Path.GetFileName(path)));
                _writers[path] = writer;
            }
            var warning = writer.Write("[" + DateTimeOffset.Now.ToString("O") + "] " + value);
            if (DeveloperPath == path) DeveloperPath = writer.CurrentPath;
            if (UserPath == path) UserPath = writer.CurrentPath;
            _writers[writer.CurrentPath] = writer;
            if (warning != null)
            {
                _writeErrors.Add(warning);
                // The operation result exposes WriteErrors; also keep the
                // user-facing file informed when its developer sink moved.
                if (UserPath != null && UserPath != writer.CurrentPath)
                    Write(UserPath, warning);
            }
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            for (var i = 0; i < files.Length; i++)
                if (i >= 400 || files[i].LastWriteTimeUtc < DateTime.UtcNow.AddDays(-30)) files[i].Delete();
        }
        catch { /* Retention never blocks an installation. */ }
    }

    public void Dispose()
    {
        if (!_completed) { DeveloperLines.Add("operation ended without a completed result"); Complete(false); }
    }

    internal sealed class DiagnosticLines : Collection<string>
    {
        private readonly Action<string> _write;
        public DiagnosticLines(Action<string> write) => _write = write;
        protected override void InsertItem(int index, string item) { base.InsertItem(index, item); _write(item); }
        public void AddRange(IEnumerable<string> values) { foreach (var value in values) Add(value); }
    }
}
