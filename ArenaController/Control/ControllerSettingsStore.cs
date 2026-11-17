using System.Text.Json;

namespace ArenaController;

/// <summary>
/// Reads and writes <see cref="ControllerSettings"/>.  Extracted from the window
/// so persistence (path, legacy migration, corrupt-file fallback) can be tested
/// without a desktop session.
/// </summary>
internal sealed class ControllerSettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly string _legacyPath;
    private readonly Action<string>? _trace;

    internal ControllerSettingsStore(string path, string legacyPath, Action<string>? trace = null)
    {
        _path = path;
        _legacyPath = legacyPath;
        _trace = trace;
    }

    internal string Path => _path;

    /// <summary>Copies a settings file that lived beside the executable into the
    /// per-user data directory once.  Returns true when a migration happened.</summary>
    internal bool MigrateLegacyIfNeeded()
    {
        try
        {
            if (File.Exists(_path) || !File.Exists(_legacyPath)) return false;
            File.Copy(_legacyPath, _path, false);
            return true;
        }
        catch (Exception exception)
        {
            // The normal load falls back to defaults; logging happens later.
            _trace?.Invoke("settings-migrate=" + exception.GetType().Name);
            return false;
        }
    }

    internal ControllerSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new ControllerSettings();
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ControllerSettings>(json) ?? new ControllerSettings();
        }
        catch (Exception exception)
        {
            _trace?.Invoke("settings-load=" + exception.GetType().Name);
            return new ControllerSettings();
        }
    }

    internal void Save(ControllerSettings settings)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception exception)
        {
            _trace?.Invoke("settings-save=" + exception.GetType().Name);
        }
    }

    /// <summary>Deep copy used for staged edits, so the applied snapshot cannot be
    /// mutated by the UI before 应用设置.</summary>
    internal static ControllerSettings Clone(ControllerSettings source)
        => JsonSerializer.Deserialize<ControllerSettings>(
               JsonSerializer.Serialize(source, JsonOptions), JsonOptions)
           ?? new ControllerSettings();
}
