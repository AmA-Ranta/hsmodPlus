using System.Text.Json;

namespace ArenaController;

/// <summary>Binds the HsMod session mode to the game process that produced it.</summary>
internal sealed class SessionModeBinding
{
    public string Mode { get; set; } = "";
    public int GameProcessId { get; set; }
    public DateTime GameStartedAtUtc { get; set; }
}

/// <summary>
/// Persists and validates the "which mode is this client really in" evidence.
/// HsMod only reads its mode at client start, so the binding (mode + process id +
/// process start time) is the only trustworthy proof that a running client was
/// launched for the configured mode.
/// </summary>
internal sealed class SessionModeBindingStore
{
    private readonly string _path;
    private readonly Action<string>? _trace;

    internal SessionModeBindingStore(string path, Action<string>? trace = null)
    {
        _path = path;
        _trace = trace;
    }

    internal SessionModeBinding? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<SessionModeBinding>(File.ReadAllText(_path));
        }
        catch (Exception exception)
        {
            _trace?.Invoke("session-binding.read=" + exception.GetType().Name);
            return null;
        }
    }

    internal void Write(string mode, int gameProcessId, DateTime gameStartedAtUtc)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new SessionModeBinding
            {
                Mode = mode,
                GameProcessId = gameProcessId,
                GameStartedAtUtc = gameStartedAtUtc
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            _trace?.Invoke("session-binding.write=" + exception.GetType().Name);
        }
    }

    /// <summary>True only when the binding names the same mode and the same game
    /// process instance that is running now.</summary>
    internal static bool Matches(SessionModeBinding? binding, string mode, int gameProcessId, DateTime gameStartedAtUtc)
        => binding != null &&
           string.Equals(binding.Mode, mode, StringComparison.Ordinal) &&
           binding.GameProcessId == gameProcessId &&
           binding.GameStartedAtUtc == gameStartedAtUtc;
}
