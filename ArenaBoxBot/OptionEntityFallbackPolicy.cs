using System;
using System.Collections.Generic;

namespace ArenaBoxBot;

internal sealed class OptionEntityCandidate
{
    internal int Id;
    internal string CardId;
    internal int Position;
    internal int Controller;
    internal string Zone;
    internal bool Legal;
}

internal static class OptionEntityFallbackPolicy
{
    internal static bool IsCurrent(int currentPid, int recPid, int packetId, int recPacket,
        double age, bool activeOwnOption)
        => activeOwnOption && currentPid > 0 && currentPid == recPid && packetId >= 0 &&
           packetId == recPacket && !double.IsNaN(age) && !double.IsInfinity(age) && age >= 0 && age <= 10;

    // Positions are authoritative here, not a guess among duplicate card IDs.
    // Repeated options for the same entity are fine; two different exact
    // matches remain ambiguous and must keep the native missing-entity result.
    internal static int Resolve(IEnumerable<OptionEntityCandidate> candidates, string cardId,
        int position, int controller, string zone)
    {
        if (candidates == null || string.IsNullOrEmpty(cardId) || position < 0 ||
            controller <= 0 || string.IsNullOrEmpty(zone)) return 0;
        var result = 0;
        foreach (var c in candidates)
        {
            if (c == null || !c.Legal || c.Id <= 0 || c.Position != position || c.Controller != controller ||
                !string.Equals(c.CardId, cardId, StringComparison.Ordinal) || c.Zone != zone) continue;
            if (result != 0 && result != c.Id) return 0;
            result = c.Id;
        }
        return result;
    }
}
