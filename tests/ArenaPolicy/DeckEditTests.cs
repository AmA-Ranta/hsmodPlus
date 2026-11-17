using ArenaBoxBot;
using Newtonsoft.Json;

internal static class DeckEditTests
{
    private static int checks;
    internal static void Run()
    {
        var original = new List<ArenaDeckEditCard>
        {
            new() { CardId = "A", Premium = 0, Count = 3 },
            new() { CardId = "A", Premium = 1, Count = 2 },
            new() { CardId = "B", Premium = 0, Count = 10 },
            new() { CardId = "C", Premium = 0, Count = 10 },
            new() { CardId = "D", Premium = 0, Count = 10 }
        };
        Check(ArenaDeckEditPolicy.TryPlan(original, 30, new Dictionary<string, int> { ["A"] = 4, ["B"] = 1 },
            out var target, out _), "Box duplicate-copy instruction must retain exact quantity across premium variants");
        Check(target.Sum(card => card.Count) == 30 && target.Single(card => card.CardId == "A").Count == 1,
            "four copies, not one distinct card, must be discarded");
        Check(!ArenaDeckEditPolicy.TryPlan(original, 30, new Dictionary<string, int> { ["A"] = 1, ["B"] = 1 }, out _, out _),
            "a deduplicated two-card recommendation cannot stand in for five copies");
        Check(!ArenaDeckEditPolicy.TryPlan(original, 30, new Dictionary<string, int> { ["unknown"] = 5 }, out _, out _),
            "foreign cards cannot be substituted into the plan");
        Check(!ArenaDeckEditPolicy.TryPlan(original, 30, new Dictionary<string, int> { ["A"] = 6 }, out _, out _),
            "over-removal cannot be clamped silently");
        Check(!ArenaDeckEditPolicy.TryPlan(original, 40, new Dictionary<string, int>(), out _, out _),
            "insufficient decks cannot be completed by save");
        Check(!ArenaDeckEditPolicy.TryPlan(original, 30, null!, out _, out _), "missing recommendation is not a fallback plan");
        var plan = new ArenaDeckEditPlan { Underground = true, SeasonId = 151, DeckId = 2770235581,
            RedraftDeckId = 2770235582, TargetCount = 30, ContextKey = "observed full edit context",
            Original = original, Target = target, RecommendationEvidence = "real Box callback" };
        var current = Copy(original);
        for (var i = 0; i < 5; i++)
        {
            Check(ArenaDeckEditPolicy.TryNextRemoval(plan, current, out var removal, out _) && removal != null,
                "each remaining physical copy must be found");
            var slot = current.Single(card => card.Key == removal.Key);
            var before = Copy(current);
            slot.Count--;
            if (slot.Count == 0) current.Remove(slot);
            Check(ArenaDeckEditPolicy.RemovedExactly(before, current, removal), "native result must remove precisely the selected copy");
            // Simulate a restart after the native removal but before any
            // persisted per-click counter. Resume from actual quantities.
            plan = JsonConvert.DeserializeObject<ArenaDeckEditPlan>(JsonConvert.SerializeObject(plan))!;
            Check(ArenaDeckEditPolicy.IsValidPlan(plan), "serialized pending edit must retain all card identities");
        }
        Check(ArenaDeckEditPolicy.TryNextRemoval(plan, current, out var done, out _) && done == null,
            "already removed five copies must not be removed again after reload");
        Check(ArenaDeckEditPolicy.Fingerprint(current) == ArenaDeckEditPolicy.Fingerprint(target), "target multiset must be exact");
        var wrongDelta = Copy(original); wrongDelta.Single(card => card.CardId == "B").Count--;
        Check(!ArenaDeckEditPolicy.RemovedExactly(original, wrongDelta, new ArenaDeckEditCard { CardId = "A", Premium = 0, Count = 1 }),
            "same total count with a different removed card must fail");
        var altered = Copy(current); altered[0].Count--;
        Check(!ArenaDeckEditPolicy.TryNextRemoval(plan, altered, out _, out _), "external removal of a kept card must stop the plan");
        altered = Copy(current); altered.Add(new ArenaDeckEditCard { CardId = "foreign", Count = 1 });
        Check(!ArenaDeckEditPolicy.TryNextRemoval(plan, altered, out _, out _), "external deck replacement must not trigger stale removals");
        Check(!ArenaDeckEditPolicy.ServerConfirms(plan, true, plan.DeckId, current), "local exact count before save cannot prove confirmation");
        plan.Stage = "Saving";
        Check(ArenaDeckEditPolicy.ServerConfirms(plan, true, plan.DeckId, current), "exact successful main-deck receipt must confirm");
        Check(!ArenaDeckEditPolicy.ServerConfirms(plan, false, plan.DeckId, current), "failed response cannot confirm");
        Check(!ArenaDeckEditPolicy.ServerConfirms(plan, true, plan.RedraftDeckId, current), "temporary redraft deck ID cannot confirm the main deck");
        Check(!ArenaDeckEditPolicy.ServerConfirms(plan, true, plan.DeckId, original), "35-card response cannot stand in for intended saved 30");
        altered = Copy(current); altered[0].CardId = "other";
        Check(!ArenaDeckEditPolicy.ServerConfirms(plan, true, plan.DeckId, altered), "same count with wrong cards cannot confirm");
        altered = Copy(current); altered[0].Premium++;
        Check(!ArenaDeckEditPolicy.ServerConfirms(plan, true, plan.DeckId, altered), "premium-normalized different contents must be investigated");
        Check(!ArenaDeckEditPolicy.CanMatch("EDITING_DECK", 30, 30, false), "unsubmitted 30-card edit must not start matchmaking");
        Check(!ArenaDeckEditPolicy.CanMatch("EDITING_DECK", 35, 30, false), "observed 35-card redraft cannot start matchmaking");
        Check(!ArenaDeckEditPolicy.CanMatch("MIDRUN", 30, 30, true), "local MIDRUN is set before server acknowledgement and must remain blocked");
        Check(!ArenaDeckEditPolicy.CanMatch("MIDRUN", 35, 30, false), "oversized local deck cannot match");
        Check(ArenaDeckEditPolicy.CanMatch("MIDRUN", 30, 30, false), "confirmed exact-size playable deck may match");
        Check(ArenaDeckEditPolicy.CanStartPlan(false, current, null!), "initial native 30-card draft may complete without redraft recommendations");
        Check(!ArenaDeckEditPolicy.CanStartPlan(true, current, original), "old 30-card base must not masquerade as already-edited 35-card redraft");
        Check(ArenaDeckEditPolicy.CanStartPlan(true, original, original), "fully merged server main plus acknowledged replacement deck may accept Box instruction");
        Check(!ArenaDeckEditPolicy.CanStartPlan(true, original, null!), "missing authoritative main deck cannot authorize redraft editing");
        Console.WriteLine("PASS: Arena edit multiset, restart and save receipt policy (" + checks + " assertions)");
    }
    private static List<ArenaDeckEditCard> Copy(IEnumerable<ArenaDeckEditCard> cards) => cards.Select(card => new ArenaDeckEditCard
        { CardId = card.CardId, Premium = card.Premium, Count = card.Count }).ToList();
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidOperationException(message); }
}
