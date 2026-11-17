using System;
using System.Diagnostics;

namespace ArenaBoxBot;

/// <summary>Single-flight, bounded-lifetime cache for immutable configuration observations.</summary>
internal sealed class TimeCachedBoolean
{
    private readonly object _gate = new object();
    private readonly Func<bool> _read;
    private readonly Func<double> _clock;
    private readonly double _ttl;
    private bool _hasValue, _value;
    private double _sampledAt, _expires;
    private long _loads, _hits;

    internal TimeCachedBoolean(Func<bool> read, Func<double> clock = null, double ttl = 0.25)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _clock = clock ?? MonotonicSeconds;
        if (ttl <= 0 || double.IsNaN(ttl) || double.IsInfinity(ttl)) throw new ArgumentOutOfRangeException(nameof(ttl));
        _ttl = ttl;
    }

    private static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal bool Read()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_hasValue && now >= _sampledAt && now < _expires)
            {
                ++_hits;
                return _value;
            }
            // A failed refresh must never make a previous value authoritative.
            _hasValue = false;
            ++_loads;
            var value = _read();
            _sampledAt = _clock();
            _expires = _sampledAt + _ttl;
            _value = value;
            _hasValue = true;
            return value;
        }
    }

    internal void Invalidate() { lock (_gate) _hasValue = false; }
    internal string Diagnostic { get { lock (_gate) return _loads + "/" + _hits; } }
}
