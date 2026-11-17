using System;

namespace ArenaBoxBot;

/// <summary>Bounds an empty recommendation gate; permits polling, never an action.</summary>
internal sealed class MissingRecommendationResponseGate
{
    internal const float WaitSeconds = 0.3f;
    private string _key = "";
    private float _since;
    private float _last;
    private bool _notified;

    internal bool Observe(string key, float now, bool active, out bool firstRelease)
    {
        firstRelease = false;
        if (!active || string.IsNullOrEmpty(key) || float.IsNaN(now) || float.IsInfinity(now))
        {
            Reset();
            return false;
        }
        if (_key != key || now < _last || now - _last > 10f)
        {
            _key = key;
            _since = now;
            _notified = false;
        }
        _last = now;
        if (now - _since < WaitSeconds) return false;
        firstRelease = !_notified;
        _notified = true;
        return true;
    }

    internal void Reset()
    {
        _key = "";
        _since = _last = 0f;
        _notified = false;
    }
}
