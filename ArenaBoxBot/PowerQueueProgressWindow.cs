using System;

namespace ArenaBoxBot;

/// <summary>Queue growth is not processing progress; changing the active task is.</summary>
internal sealed class PowerQueueProgressWindow
{
    private string _key;
    private double _since, _lastNotice;
    internal double StalledSeconds { get; private set; }

    internal bool Observe(string progressKey, double now)
    {
        if (string.IsNullOrEmpty(progressKey) || double.IsNaN(now) || double.IsInfinity(now))
        {
            _key = null;
            StalledSeconds = 0;
            return false;
        }
        if (_key != progressKey || now < _since || now < _lastNotice)
        {
            _key = progressKey;
            _since = now;
            _lastNotice = now - 5;
            StalledSeconds = 0;
            return false;
        }
        StalledSeconds = now - _since;
        if (StalledSeconds < 3 || now - _lastNotice < 5) return false;
        _lastNotice = now;
        return true;
    }
}
