using System.Globalization;

namespace ArenaController;

/// <summary>Only this request's current-process log events may complete a fallback stop.</summary>
public sealed class ArenaStopLogEvidence
{
    private readonly DateTime _notBefore;
    private readonly int _processId;
    private bool _returned;
    private bool _rewards;
    public bool Completed { get; private set; }

    public ArenaStopLogEvidence(DateTime requestedAt, DateTime sessionStartedAt, int processId)
    {
        _notBefore = requestedAt > sessionStartedAt ? requestedAt : sessionStartedAt;
        _processId = processId;
    }

    public void Observe(string line, int currentProcessId)
    {
        if (_processId <= 0 || currentProcessId != _processId) { Completed = false; return; }
        if (line.Length < 23 || !DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp) || timestamp < _notBefore)
            return;
        if (line.Contains("entered Arena gameplay", StringComparison.OrdinalIgnoreCase))
        { Completed = _returned = _rewards = false; return; }
        if (line.Contains("Arena state=REWARDS", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("arena state=IN_REWARDS", StringComparison.OrdinalIgnoreCase))
        { _rewards = true; Completed = false; return; }
        if (line.Contains("returned from Arena gameplay", StringComparison.OrdinalIgnoreCase))
        { _returned = true; Completed = false; return; }
        if (line.Contains("controller stop-after-game request completed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("controller requested Arena stop; no active gameplay remained", StringComparison.OrdinalIgnoreCase))
        { Completed = true; return; }
        if ((_returned || _rewards) && line.Contains("Arena state=NO_RUN", StringComparison.OrdinalIgnoreCase))
            Completed = true;
    }

    public bool IsCompleteFor(int currentProcessId) => _processId > 0 && currentProcessId == _processId && Completed;
}
