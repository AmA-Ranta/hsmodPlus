using System;
using System.Collections.Generic;
using System.Linq;

namespace ArenaController;

public sealed record ConstructedDeckOption(string Value, string Label);

/// <summary>
/// Picks the configured deck from the mode-specific deck list HsMod publishes.
/// HsMod decorates collection names with their format ("快攻  (标准)") and, for
/// duplicates, a stable #ID, so a bare name is not enough for every deck.
/// </summary>
public static class ConstructedDeckSelection
{
    public static ConstructedDeckOption Resolve(IReadOnlyList<ConstructedDeckOption> options, string wanted, string mode,
        bool allowSingleFallback = false)
    {
        if (options == null || options.Count == 0)
            throw new InvalidOperationException("当前" + mode + "模式没有可用卡组；请先在游戏内建立一套该模式的卡组。");

        var matches = options
            .Where(x => string.Equals(x.Label.Trim(), wanted, StringComparison.OrdinalIgnoreCase) ||
                        x.Label.TrimStart().StartsWith(wanted + "  (", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1)
            throw new InvalidOperationException("当前" + mode + "模式有多个同名卡组“" + wanted +
                "”，请填写含格式和 #ID 的完整卡组名称：" + string.Join("、", matches.Select(x => x.Label.Trim())));

        // A mode with exactly one deck has no real choice to make; picking it is
        // better than leaving the bot stuck on "please select a deck".
        if (allowSingleFallback && options.Count == 1) return options[0];

        var available = string.Join("、", options.Take(12).Select(x => x.Label.Trim()));
        throw new InvalidOperationException("当前" + mode + "模式找不到卡组“" + wanted +
            "”。可用卡组：" + (available.Length == 0 ? "无" : available));
    }

    /// <summary>True when the configured name already selects exactly one deck.</summary>
    public static bool CanResolve(IReadOnlyList<ConstructedDeckOption> options, string wanted)
    {
        try
        {
            Resolve(options, wanted, "");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}