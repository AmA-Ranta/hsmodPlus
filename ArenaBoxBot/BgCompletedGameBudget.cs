using System;
using System.Collections.Generic;

namespace ArenaBoxBot;

// This count records authoritative terminal callbacks, not rating/CSV writes.
internal sealed class BgCompletedGameBudget
{
    private readonly HashSet<string> _completed = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _evidence = new Dictionary<string, int>(StringComparer.Ordinal);
    internal int Completed => _completed.Count;
    internal bool Record(string game, bool eligible, bool terminal, bool serverComplete)
    {
        if (!eligible || string.IsNullOrEmpty(game)) return false;
        _evidence.TryGetValue(game, out var flags);
        flags |= (terminal ? 1 : 0) | (serverComplete ? 2 : 0);
        _evidence[game] = flags;
        return flags == 3 && _completed.Add(game);
    }
    internal bool Reached(int limit) => limit > 0 && Completed >= limit;
    internal void Reset() { _completed.Clear(); _evidence.Clear(); }
}
