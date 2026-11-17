using System;
using System.Collections.Generic;
using System.Linq;

namespace ArenaBoxBot;

internal sealed class ArenaDeckEditCard
{
    public string CardId = "";
    public int Premium;
    public int Count;
    public string Key => CardId + "|" + Premium;
}

internal sealed class ArenaDeckEditPlan
{
    public int Version = 1;
    public bool Underground;
    public int SeasonId;
    public long DeckId;
    public long RedraftDeckId;
    public int TargetCount;
    public string ContextKey = "";
    public string RecommendationEvidence = "";
    public List<ArenaDeckEditCard> Original = new();
    public List<ArenaDeckEditCard> Target = new();
    public string Stage = "Removing";
    public long SaveRequestedAtUnixMs;
    public long ConfirmedAtUnixMs;
    public string ConfirmationSource = "";
}

internal static class ArenaDeckEditPolicy
{
    internal static string Fingerprint(IEnumerable<ArenaDeckEditCard> cards) => string.Join(",",
        cards.OrderBy(card => card.Key, StringComparer.Ordinal).Select(card => card.Key + ":" + card.Count));

    internal static bool IsValidCards(IList<ArenaDeckEditCard> cards) => cards != null && cards.Count > 0 &&
        cards.All(card => card != null && !string.IsNullOrWhiteSpace(card.CardId) && !card.CardId.Contains("|") &&
            card.Premium >= 0 && card.Count > 0) && cards.Select(card => card.Key).Distinct(StringComparer.Ordinal).Count() == cards.Count;

    internal static bool TryPlan(IList<ArenaDeckEditCard> current, int targetCount,
        IDictionary<string, int> recommendedRemovals, out List<ArenaDeckEditCard> target, out string reason)
    {
        target = null;
        reason = "";
        if (!IsValidCards(current) || targetCount <= 0 || targetCount > 100)
        { reason = "编辑卡牌或原生目标数量无效"; return false; }
        var excess = current.Sum(card => card.Count) - targetCount;
        if (excess < 0) { reason = "卡牌不足，不能自动完成编辑"; return false; }
        if (recommendedRemovals == null || recommendedRemovals.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) ||
            recommendedRemovals.Sum(pair => pair.Value) != excess)
        { reason = "盒子明确推荐弃牌的份数与需要弃牌的份数不一致"; return false; }
        var remaining = current.ToDictionary(card => card.Key, card => card.Count, StringComparer.Ordinal);
        foreach (var pair in recommendedRemovals)
        {
            var variants = current.Where(card => card.CardId == pair.Key).OrderBy(card => card.Premium).ToList();
            if (variants.Sum(card => card.Count) < pair.Value)
            { reason = "推荐弃牌不属于当前完整编辑套牌或份数不足：" + pair.Key; return false; }
            var remove = pair.Value;
            // Premium affects appearance only. Preserve the exact recommended
            // card and quantity; assign its physical copies deterministically.
            foreach (var variant in variants)
            {
                var take = Math.Min(remove, remaining[variant.Key]);
                remaining[variant.Key] -= take;
                remove -= take;
            }
        }
        target = current.Where(card => remaining[card.Key] > 0).Select(card => new ArenaDeckEditCard
            { CardId = card.CardId, Premium = card.Premium, Count = remaining[card.Key] }).ToList();
        return target.Sum(card => card.Count) == targetCount;
    }

    internal static bool TryNextRemoval(ArenaDeckEditPlan plan, IList<ArenaDeckEditCard> current,
        out ArenaDeckEditCard removal, out string reason)
    {
        removal = null;
        reason = "";
        if (!IsValidPlan(plan) || !IsValidCards(current))
        { reason = "编辑计划或当前套牌无效"; return false; }
        var original = plan.Original.ToDictionary(card => card.Key, card => card.Count, StringComparer.Ordinal);
        var target = plan.Target.ToDictionary(card => card.Key, card => card.Count, StringComparer.Ordinal);
        var actual = current.ToDictionary(card => card.Key, card => card.Count, StringComparer.Ordinal);
        foreach (var card in current)
            if (!original.TryGetValue(card.Key, out var max) || card.Count > max)
            { reason = "编辑套牌出现计划之外的卡牌或份数：" + card.Key; return false; }
        foreach (var card in plan.Target)
            if (!actual.TryGetValue(card.Key, out var count) || count < card.Count)
            { reason = "编辑套牌已缺少目标卡牌，不能继续删除：" + card.Key; return false; }
        // Recompute from the actual multiset every tick. A crash after removal
        // but before the next tick can never remove the same copy twice.
        foreach (var card in current.OrderBy(card => card.Key, StringComparer.Ordinal))
            if (card.Count > (target.TryGetValue(card.Key, out var keep) ? keep : 0))
            {
                removal = new ArenaDeckEditCard { CardId = card.CardId, Premium = card.Premium, Count = 1 };
                return true;
            }
        return true;
    }

    internal static bool IsValidPlan(ArenaDeckEditPlan plan) => plan != null && plan.Version == 1 &&
        plan.DeckId > 0 && plan.SeasonId > 0 && plan.TargetCount > 0 && plan.TargetCount <= 100 && !string.IsNullOrEmpty(plan.ContextKey) &&
        IsValidCards(plan.Original) && IsValidCards(plan.Target) && plan.Target.Sum(card => card.Count) == plan.TargetCount &&
        plan.Original.Sum(card => card.Count) <= 100 &&
        plan.Target.All(card => plan.Original.Any(source => source.Key == card.Key && source.Count >= card.Count)) &&
        (plan.Stage == "Removing" || plan.Stage == "Saving" || plan.Stage == "Confirmed");

    internal static bool ServerConfirms(ArenaDeckEditPlan plan, bool success, long deckId,
        IList<ArenaDeckEditCard> serverCards) => IsValidPlan(plan) && plan.Stage == "Saving" && success &&
        deckId == plan.DeckId && IsValidCards(serverCards) &&
        Fingerprint(serverCards) == Fingerprint(plan.Target);

    internal static bool RemovedExactly(IList<ArenaDeckEditCard> before, IList<ArenaDeckEditCard> after, ArenaDeckEditCard removal)
    {
        if (!IsValidCards(before) || !IsValidCards(after) || removal == null || removal.Count != 1) return false;
        var expected = before.Select(card => new ArenaDeckEditCard { CardId = card.CardId, Premium = card.Premium, Count = card.Count }).ToList();
        var slot = expected.FirstOrDefault(card => card.Key == removal.Key);
        if (slot == null) return false;
        if (--slot.Count == 0) expected.Remove(slot);
        return Fingerprint(expected) == Fingerprint(after);
    }

    internal static bool CanMatch(string arenaState, int actualCount, int targetCount, bool savePending) =>
        arenaState == "MIDRUN" && targetCount > 0 && actualCount == targetCount && !savePending;

    internal static bool CanStartPlan(bool redraftEditing, IList<ArenaDeckEditCard> current,
        IList<ArenaDeckEditCard> authoritativeMerged) => IsValidCards(current) &&
        (!redraftEditing || (IsValidCards(authoritativeMerged) && Fingerprint(current) == Fingerprint(authoritativeMerged)));
}
