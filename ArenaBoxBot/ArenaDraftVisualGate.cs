using System;

namespace ArenaBoxBot;

/// <summary>
/// A time delay alone cannot prove that Unity has displayed a card bucket.
/// Require consecutive observations of the same fully loaded, settled layout,
/// in different rendered frames. Fast mode only skips thinking time.
/// </summary>
internal sealed class ArenaDraftVisualGate
{
    internal const float StableSeconds = 1.25f;
    internal const int MinimumObservations = 4;
    internal const float MaximumObservationGapSeconds = 10f;
    private string _key;
    private string _identity;
    private float[] _geometry;
    private float _stableSince;
    private float _lastObservedAt;
    private int _lastFrame = -1;
    private int _observations;

    internal bool Observe(string key, string identity, float[] geometry, bool nativeReady, float now, int frame)
    {
        if (!nativeReady || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(identity) ||
            geometry == null || geometry.Length == 0 || float.IsNaN(now) || float.IsInfinity(now))
        {
            Reset();
            return false;
        }
        foreach (var value in geometry)
            if (float.IsNaN(value) || float.IsInfinity(value)) { Reset(); return false; }

        var changed = _key != key || _identity != identity || !SameGeometry(_geometry, geometry) ||
            now < _lastObservedAt || now - _lastObservedAt > MaximumObservationGapSeconds || frame < _lastFrame;
        if (changed)
        {
            _key = key;
            _identity = identity;
            _geometry = (float[])geometry.Clone();
            _stableSince = now;
            _observations = 0;
            _lastFrame = -1;
        }
        if (frame != _lastFrame) _observations++;
        _lastFrame = frame;
        _lastObservedAt = now;
        return _observations >= MinimumObservations && now - _stableSince >= StableSeconds;
    }

    private static bool SameGeometry(float[] previous, float[] current)
    {
        if (previous == null || previous.Length != current.Length) return false;
        // Compare to the start of the stable interval, not just the previous
        // sample: a slowly drifting animation must not accumulate unnoticed.
        for (var i = 0; i < current.Length; i++)
            if (Math.Abs(previous[i] - current[i]) > 0.003f) return false;
        return true;
    }

    internal void Reset()
    {
        _key = _identity = null;
        _geometry = null;
        _stableSince = _lastObservedAt = 0f;
        _lastFrame = -1;
        _observations = 0;
    }
}

/// <summary>
/// A slow asset loader gets at least a minute of uninterrupted progress.
/// Repeatedly refreshing every two seconds destroys actors while they load.
/// Permit only three increasingly spaced server refreshes per choice slot.
/// </summary>
internal sealed class ArenaDraftWaitBudget
{
    internal const float InitialRefreshSeconds = 60f;
    internal const int MaximumRefreshes = 3;
    private string _key;
    private string _progress;
    private float _lastProgressAt;
    private float _lastRefreshAt = -1f;
    internal int Refreshes { get; private set; }

    internal void Observe(string key, string progress, float now)
    {
        if (_key != key || now < _lastProgressAt)
        {
            Reset();
            _key = key;
            _lastProgressAt = now;
        }
        if (_progress != progress)
        {
            _progress = progress;
            _lastProgressAt = now;
        }
    }

    internal bool IsRefreshDue(float now)
    {
        return _key != null && Refreshes < MaximumRefreshes &&
            now - Math.Max(_lastProgressAt, _lastRefreshAt) >= InitialRefreshSeconds * (Refreshes + 1);
    }

    internal bool IsExhausted(float now)
    {
        return _key != null && Refreshes >= MaximumRefreshes &&
            now - Math.Max(_lastProgressAt, _lastRefreshAt) >= InitialRefreshSeconds;
    }

    internal void Refreshed(float now)
    {
        Refreshes++;
        _lastRefreshAt = now;
    }

    internal void Reset()
    {
        _key = _progress = null;
        _lastProgressAt = 0f;
        _lastRefreshAt = -1f;
        Refreshes = 0;
    }
}
