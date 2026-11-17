using System.Text;

namespace ArenaController;

/// <summary>A failed primary write must leave both a visible warning and a durable fallback.</summary>
public sealed class DurableLogWriter
{
    // A monitor that mirrors the native log can emit tens of thousands of
    // lines per hour.  Keep one previous segment so a long-running controller
    // can never fill the disk with a multi-hundred-megabyte developer log.
    private const long MaxFileBytes = 20L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _fallbackPath;
    private readonly Action<string, string> _append;
    private string? _lastFailure;
    public string CurrentPath { get; private set; }

    public DurableLogWriter(string path, string fallbackPath, Action<string, string>? append = null)
    {
        CurrentPath = path;
        _fallbackPath = fallbackPath;
        _append = append ?? ((target, value) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                var info = new FileInfo(target);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    var rotated = target + ".1";
                    File.Move(target, rotated, true);
                }
            }
            catch { /* a failed rotation must never drop the new line */ }
            using var stream = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(value);
        });
    }

    public string? Write(string line)
    {
        lock (_gate)
        {
            try { _append(CurrentPath, line); return null; }
            catch (Exception primary)
            {
                var failedPath = CurrentPath;
                var detail = primary.GetType().Name + ": " + primary.Message;
                var warning = "日志写入失败：" + failedPath + "（" + detail + "）";
                try
                {
                    if (string.Equals(failedPath, _fallbackPath, StringComparison.OrdinalIgnoreCase)) throw;
                    _append(_fallbackPath, "[" + DateTimeOffset.Now.ToString("O") + "] " + warning);
                    _append(_fallbackPath, line);
                    CurrentPath = _fallbackPath;
                    warning += "；后续记录已改存：" + CurrentPath;
                }
                catch (Exception fallback) { warning += "；备用记录也失败：" + fallback.Message + "。请保留界面日志。"; }
                if (string.Equals(_lastFailure, warning, StringComparison.Ordinal)) return null;
                _lastFailure = warning;
                return warning;
            }
        }
    }
}
