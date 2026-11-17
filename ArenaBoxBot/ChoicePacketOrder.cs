using System;
using System.Collections.Generic;

namespace ArenaBoxBot;

/// <summary>Keeps a received choice's result behind its deferred native registration.</summary>
internal sealed class ChoicePacketOrder
{
    private string _game;
    private readonly Dictionary<string, bool> _pending = new Dictionary<string, bool>();

    internal void Receive(string game, int player, int choice)
    {
        if (!SelectGame(game) || player <= 0 || choice <= 0) return;
        var key = player + ":" + choice;
        if (!_pending.ContainsKey(key)) _pending.Add(key, false);
    }

    internal bool AllowChosen(string game, int player, int choice, int? registeredChoice,
        bool nativeAllowed, out bool firstDeferred, bool nativeDiscardsChoices = false)
    {
        firstDeferred = false;
        if (!SelectGame(game) || !nativeAllowed) return nativeAllowed;
        var key = player + ":" + choice;
        // The client deliberately skips choice registration once an early
        // concede task exists. Waiting for that registration would strand
        // the original queued result during native match teardown.
        if (nativeDiscardsChoices)
        {
            _pending.Remove(key);
            return nativeAllowed;
        }
        if (!_pending.TryGetValue(key, out var noticed)) return nativeAllowed;
        if (registeredChoice == choice)
        {
            _pending.Remove(key);
            return nativeAllowed;
        }
        firstDeferred = !noticed;
        _pending[key] = true;
        return false;
    }

    private bool SelectGame(string game)
    {
        if (string.IsNullOrEmpty(game)) return false;
        if (!string.Equals(game, _game, StringComparison.Ordinal))
        {
            _pending.Clear();
            _game = game;
        }
        return true;
    }
}
