#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;

namespace ArenaBoxBot;

/// <summary>Advances native close iterators, including nested iterators, under one cancellable lease.</summary>
internal sealed class ArenaPopupCloseOperation : IEnumerator, IDisposable
{
    private readonly Stack<IEnumerator> _iterators = new();
    private readonly Func<bool> _canContinue;
    internal bool Completed { get; private set; }
    internal bool Cancelled { get; private set; }
    public object? Current { get; private set; }

    internal ArenaPopupCloseOperation(IEnumerator iterator, Func<bool> canContinue)
    {
        _iterators.Push(iterator ?? throw new ArgumentNullException(nameof(iterator)));
        _canContinue = canContinue ?? throw new ArgumentNullException(nameof(canContinue));
    }

    public bool MoveNext()
    {
        if (Completed || Cancelled) return false;
        try
        {
            while (_iterators.Count > 0)
            {
                if (!_canContinue()) { Dispose(); return false; }
                var iterator = _iterators.Peek();
                if (!iterator.MoveNext())
                {
                    _iterators.Pop();
                    (iterator as IDisposable)?.Dispose();
                    continue;
                }
                // Keep nested native work under the same cancellation gate;
                // Unity must not start an untracked child close coroutine.
                if (iterator.Current is IEnumerator nested) { _iterators.Push(nested); continue; }
                Current = iterator.Current;
                return true;
            }
            Completed = true;
            Current = null;
            return false;
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (!Completed) Cancelled = true;
        while (_iterators.Count > 0)
        {
            var iterator = _iterators.Pop();
            try { (iterator as IDisposable)?.Dispose(); } catch { }
        }
        Current = null;
    }

    public void Reset() => throw new NotSupportedException();
}
