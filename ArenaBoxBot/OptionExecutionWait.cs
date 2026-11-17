using System;

namespace ArenaBoxBot;

/// <summary>Observational timing only; it never releases an execution guard.</summary>
internal sealed class OptionExecutionWait
{
    private string _key = "";
    private float _since;
    private float _nextNotice;

    internal bool Observe(string key, float now, bool succeeded, bool active)
    {
        if (!active || succeeded || string.IsNullOrEmpty(key))
        {
            _key = "";
            return false;
        }
        if (_key != key || now < _since)
        {
            _key = key;
            _since = now;
            _nextNotice = now + 3f;
            return false;
        }
        if (now < _nextNotice) return false;
        _nextNotice = now + 5f;
        return true;
    }

    internal float Elapsed(float now) => Math.Max(0f, now - _since);
}
