using System;

namespace ArenaBoxBot;

/// <summary>Defer periodic resource eviction until the native client has been idle in a menu.</summary>
internal sealed class MemoryCleanupPolicy
{
    private string _scene = "";
    private double _idleSince = double.NaN;
    private double _lastSample = double.NaN;

    internal bool Observe(string scene, bool transitioning, bool findingGame, double now)
    {
        var knownMenu = scene == "HUB" || scene == "TOURNAMENT";
        if (!knownMenu || transitioning || findingGame || double.IsNaN(now) || double.IsInfinity(now))
        {
            _scene = "";
            _idleSince = _lastSample = double.NaN;
            return false;
        }
        // A missing sample or backwards clock must establish a fresh quiet window.
        if (_scene != scene || double.IsNaN(_idleSince) || now < _lastSample || now - _lastSample > 1d)
        {
            _scene = scene;
            _idleSince = now;
        }
        _lastSample = now;
        return now - _idleSince >= 2d;
    }
}
