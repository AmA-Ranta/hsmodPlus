using System;

namespace ArenaBoxBot;

/// <summary>Bounded observational counters; no allocations or screen capture per frame.</summary>
internal sealed class FrameTimingWindow
{
    private double _previous = double.NaN, _opened;
    private int _frames, _slow, _severe;
    private double _maximum;
    internal int Frames { get; private set; }
    internal int SlowFrames { get; private set; }
    internal int SevereFrames { get; private set; }
    internal double MaximumMs { get; private set; }
    internal double Seconds { get; private set; }

    internal bool Observe(double now)
    {
        if (double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(_previous) || now < _previous)
        {
            _opened = _previous = now;
            _frames = _slow = _severe = 0;
            _maximum = 0;
            return false;
        }
        var ms = (now - _previous) * 1000d;
        _previous = now;
        ++_frames;
        if (ms >= 250) ++_slow;
        if (ms >= 1000) ++_severe;
        _maximum = Math.Max(_maximum, ms);
        if (now - _opened < 30) return false;
        Frames = _frames; SlowFrames = _slow; SevereFrames = _severe;
        MaximumMs = _maximum; Seconds = now - _opened;
        _opened = now;
        _frames = _slow = _severe = 0;
        _maximum = 0;
        return true;
    }
}
