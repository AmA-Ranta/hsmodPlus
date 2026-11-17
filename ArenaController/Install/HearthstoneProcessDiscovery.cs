using System.Diagnostics;

namespace ArenaController;

public readonly record struct GameProcessEvidence(int Id, string? Executable, bool? HasWindow, bool Exited);

/// <summary>Separate launch presence from permission to control a process.</summary>
public static class HearthstoneProcessDiscovery
{
    public static bool IsConfigured(GameProcessEvidence evidence, string directory)
    {
        if (evidence.Exited || string.IsNullOrWhiteSpace(evidence.Executable)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(evidence.Executable),
                Path.GetFullPath(Path.Combine(directory, "Hearthstone.exe")), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool BlocksLaunch(GameProcessEvidence evidence, string directory) =>
        !evidence.Exited && (!string.IsNullOrWhiteSpace(evidence.Executable) || evidence.HasWindow != false);

    public static int SelectConfigured(IEnumerable<GameProcessEvidence> evidence, string directory)
    {
        var candidates = evidence.Where(e => BlocksLaunch(e, directory)).ToArray();
        return candidates.Length == 1 && IsConfigured(candidates[0], directory) ? candidates[0].Id : 0;
    }

    internal static GameProcessEvidence Read(Process process)
    {
        var exited = false;
        string? executable = null;
        bool? hasWindow = null;
        try { exited = process.HasExited; } catch { }
        try { executable = process.MainModule?.FileName; } catch { }
        try { hasWindow = process.MainWindowHandle != IntPtr.Zero; } catch { }
        return new(process.Id, executable, hasWindow, exited);
    }

    internal static Process? FindConfigured(string directory)
    {
        var processes = Process.GetProcessesByName("Hearthstone");
        var selected = SelectConfigured(processes.Select(Read), directory);
        Process? result = null;
        foreach (var process in processes)
            if (process.Id == selected) result = process;
            else process.Dispose();
        return result;
    }

    internal static bool HasLaunchCandidate(string directory)
    {
        var processes = Process.GetProcessesByName("Hearthstone");
        try { return processes.Select(Read).Any(e => BlocksLaunch(e, directory)); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    // Unknown metadata is never permission for installation, Box teardown,
    // or session destruction, even when that process does not block launch.
    internal static bool HasUnverifiedProcess(string directory)
    {
        var processes = Process.GetProcessesByName("Hearthstone");
        try { return processes.Select(Read).Any(e => !e.Exited && !IsConfigured(e, directory)); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
