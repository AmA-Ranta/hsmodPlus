using System;
using System.Collections.Generic;

namespace ArenaBoxBot;

internal sealed class TargetlessAbilityOption
{
    internal int OptionIndex, SubOptionIndex, MainId, AbilityId, AbilityController, TargetCount;
    internal string AbilityCardId;
    internal bool MainLegal, SubLegal;
}

internal static class TargetlessAbilityPolicy
{
    internal static bool Current(int pid, int recPid, int packet, int recPacket, double age,
        bool ready, string action, string raw, string ability, bool noTarget, bool failedMissingTarget)
        => pid > 0 && recPid == pid && packet > 0 && packet == recPacket && ready && noTarget && failedMissingTarget &&
            !double.IsNaN(age) && !double.IsInfinity(age) && age >= 0 && age <= 10 &&
            string.Equals(action, "attack", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(raw, "minion_attack", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(ability);

    internal static TargetlessAbilityOption Resolve(IList<TargetlessAbilityOption> options, int source, int owner, string ability)
    {
        if (options == null || source <= 0 || owner <= 0 || string.IsNullOrEmpty(ability)) return null;
        TargetlessAbilityOption found = null;
        foreach (var candidate in options)
        {
            if (candidate == null || candidate.MainId != source || candidate.OptionIndex < 0 || candidate.SubOptionIndex < 0 ||
                candidate.AbilityId <= 0 || candidate.AbilityId == source || candidate.AbilityController != owner ||
                !candidate.MainLegal || !candidate.SubLegal || candidate.TargetCount != 0 ||
                !string.Equals(candidate.AbilityCardId, ability, StringComparison.Ordinal)) continue;
            if (found != null) return null;
            found = candidate;
        }
        return found;
    }
}
