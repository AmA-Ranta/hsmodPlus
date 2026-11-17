using ArenaBoxBot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class StatsPersistenceTests
{
    internal static void Run()
    {
        // This fixture is the observed 12:24 production payload. It explicitly
        // claims initialization while omitting all custom nested fields.
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures",
            "arena-stats-missing-contexts-20261007.json"));
        var data = ArenaStatsPersistence.Read(fixture, out var migration);
        Check(!string.IsNullOrEmpty(migration) && data.UndergroundRun.RunKey == "underground:151:2770235581" &&
            data.UndergroundRun.Wins == 2 && data.UndergroundRun.Active,
            "Observed missing-context file must recover the surviving underground projection");
        Check(data.RunOrigin == "recovered-missing-context-projection" &&
            data.NormalRun.Origin == "missing-context-awaiting-native", "Missing context recovery needs explicit persistent provenance");
        CheckHistory(data);

        ExistingRunDoesNotImportHistory(fixture);
        var normalized = JObject.Parse(ArenaStatsPersistence.Write(data));
        Check(normalized["NormalRun"] is JObject && normalized["UndergroundRun"] is JObject &&
            normalized["MatchLogCursors"] is JArray, "Production serializer must emit every nested field");
        data = ArenaStatsPersistence.Read(normalized.ToString(), out migration);
        Check(migration == "", "A rewritten complete file must not repeat migration");
        ArenaStatsLedger.SelectMode(data, false);
        Check(!data.RunActive && data.CurrentRunWins == 0, "Missing opposite mode cannot inherit underground results");
        data = Restart(data);
        ArenaStatsLedger.SelectMode(data, true);
        Check(data.CurrentRunWins == 2 && data.CurrentRunLosses == 0 && data.RunActive &&
            data.RunKey == "underground:151:2770235581", "Recovered context survives mode switch and restart");
        CheckHistory(data);

        var partial = JObject.Parse(fixture);
        partial["NormalRun"] = JObject.Parse("{\"Wins\":4,\"Losses\":1,\"Active\":true,\"RewardPending\":true," +
            "\"RunKey\":\"normal:151:444\",\"PendingGameKey\":\"normal:151:444:5\",\"PendingGameWinsBefore\":4," +
            "\"PendingGameLossesBefore\":1,\"AutomaticGamesInRun\":5,\"EntryRequestedAtUnixMs\":123456}");
        partial["MatchLogCursors"] = new JArray(new[] { JObject.Parse("{\"LogPath\":\"旧记录/炉石.log\",\"Position\":9876543210}") });
        partial["RecordedGameKeys"] = new JArray(new[] { "game-1", "game-2" });
        data = ArenaStatsPersistence.Read(partial.ToString(), out _);
        data = Restart(data);
        ArenaStatsLedger.SelectMode(data, false);
        Check(data.CurrentRunWins == 4 && data.CurrentRunLosses == 1 && data.RewardPending &&
            data.PendingGameKey == "normal:151:444:5" && data.PendingGameWinsBefore == 4 &&
            data.PendingGameLossesBefore == 1 && data.AutomaticGamesInRun == 5 && data.EntryRequestedAtUnixMs == 123456,
            "Repair cannot overwrite an existing opposite context or its pending result");
        Check(data.MatchLogCursors.Count == 1 && data.MatchLogCursors[0].Position == 9876543210 &&
            data.MatchLogCursors[0].LogPath == "旧记录/炉石.log" && data.RecordedGameKeys.SequenceEqual(new[] { "game-1", "game-2" }),
            "Legacy nested cursors and result deduplication keys must round trip with production serialization");
        CheckHistory(data);

        partial["NormalRun"] = null;
        partial["UndergroundRun"] = null;
        partial["RecordedGameKeys"] = null;
        partial["MatchLogCursors"] = null;
        data = ArenaStatsPersistence.Read(partial.ToString(), out _);
        Check(data.UndergroundRun.Wins == 2 && data.NormalRun != null && data.MatchLogCursors.Count == 0 &&
            data.RecordedGameKeys.Count == 0, "Explicit null collections must follow missing-field recovery");
        CheckHistory(data);
        foreach (var invalid in new[] { "null", "[]", "{\"TotalWins\":", "{\"TotalWins\":\"invalid\"}" })
        {
            var rejected = false;
            try { ArenaStatsPersistence.Read(invalid, out _); }
            catch (JsonException) { rejected = true; }
            Check(rejected, "Unreadable history must fail visibly instead of silently becoming empty counters");
        }
        Console.WriteLine("PASS: production Newtonsoft statistics persistence, observed missing contexts, partial migration and nested round trips");
    }

    private static ArenaStatsData Restart(ArenaStatsData data)
        => ArenaStatsPersistence.Read(ArenaStatsPersistence.Write(data), out _);

    private static void ExistingRunDoesNotImportHistory(string fixture)
    {
        var data = ArenaStatsPersistence.Read(fixture, out _);
        ArenaStatsLedger.SelectMode(data, false);
        Check(!ArenaStatsLedger.HasNewRunEvidence(data, "normal:151:2770175839", 500000),
            "Missing normal context is not evidence that its old native run is newly purchased");
        ArenaStatsLedger.StartRun(data, "normal:151:2770175839", 1, 1);
        Check(data.CurrentRunWins == 1 && data.CurrentRunLosses == 1 && data.RunOrigin == "attached-existing-baseline",
            "Attach the real old normal score as a baseline with explicit provenance");
        CheckHistory(data);
        for (var i = 0; i < 3; i++)
        {
            data = Restart(data);
            ArenaStatsLedger.SelectMode(data, true);
            Check(data.CurrentRunWins == 2 && data.CurrentRunLosses == 0, "Underground historical projection survives normal attach");
            ArenaStatsLedger.StartRun(data, "underground:151:2770235581", 2, 0);
            ArenaStatsLedger.Reconcile(data, 2, 0);
            ArenaStatsLedger.SelectMode(data, false);
            ArenaStatsLedger.StartRun(data, "normal:151:2770175839", 1, 1);
            ArenaStatsLedger.Reconcile(data, 1, 1);
            Check(data.CurrentRunWins == 1 && data.CurrentRunLosses == 1, "Normal baseline survives repeated mode restoration");
            CheckHistory(data);
        }
        ArenaStatsLedger.BeginGame(data, "normal:151:2770175839:next");
        ArenaStatsLedger.Reconcile(data, 2, 1);
        ArenaStatsLedger.FinishGame(data, "normal:151:2770175839:next", true);
        Check(data.TotalWins == 39 && data.TotalLosses == 32 && data.RunCount == 26,
            "Only the new game after attachment contributes, even if server result precedes callback");
        data = Restart(data);
        ArenaStatsLedger.BeginGame(data, "normal:151:2770175839:loss");
        ArenaStatsLedger.FinishGame(data, "normal:151:2770175839:loss", false);
        ArenaStatsLedger.Reconcile(data, 2, 2);
        Check(data.TotalWins == 39 && data.TotalLosses == 33 && data.RunCount == 26,
            "New loss after restart is not swallowed by the recovered baseline");
        data.RewardPending = true;
        ArenaStatsLedger.CompleteRun(data);
        ArenaStatsLedger.ObserveEmptyRun(data, "normal:151:2770175839", 600000);
        Check(!ArenaStatsLedger.HasNewRunEvidence(data, "normal:151:2770175839", 600010),
            "Even after NO_RUN the old deck cannot be counted again");
        ArenaStatsLedger.SelectMode(data, true);
        data = Restart(data);
        ArenaStatsLedger.SelectMode(data, false);
        Check(!ArenaStatsLedger.HasNewRunEvidence(data, "normal:151:2770300000", 600010),
            "Empty-run evidence survives restart but alone cannot assign an unknown manually entered deck");
        EntryReceiptTests.ConfirmEntry(data, "normal:151:2770300000", 600010);
        ArenaStatsLedger.StartRun(data, "normal:151:2770300000", 0, 0,
            ArenaStatsLedger.HasNewRunEvidence(data, "normal:151:2770300000", 603110));
        Check(data.RunCount == 27 && data.CurrentRunWins == 0 && data.CurrentRunLosses == 0 &&
            data.RunOrigin == "observed-new-entry" && data.TotalWins == 39 && data.TotalLosses == 33,
            "A truly observed new entry counts once while retaining lifetime history");
        ArenaStatsLedger.BeginGame(data, "normal:151:2770300000:first");
        ArenaStatsLedger.FinishGame(data, "normal:151:2770300000:first", true);
        Check(data.TotalWins == 40 && data.TotalLosses == 33 && data.RunCount == 27,
            "First result of a true new run is still counted");

        var empty = new ArenaStatsData();
        ArenaStatsLedger.ObserveEmptyRun(empty, "old", 1000);
        Check(!ArenaStatsLedger.HasNewRunEvidence(empty, "new", 601001) &&
            !ArenaStatsLedger.HasNewRunEvidence(empty, "new", 999),
            "Expired or future-dated evidence cannot reclassify an unknown run as new");
        var gate = new ArenaLifecyclePolicy();
        gate.Observe("NO_RUN", 0);
        Check(!gate.CanObserveEmptyRun("NO_RUN", false, false, false, false, 10), "Loading placeholder is not native empty-run proof");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, false, false, 1), "Transient native empty state is not proof");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, true, false, false, 10), "Active server session contradicts empty state");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, false, true, 10), "Unclaimed chest contradicts empty state");
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, false, false, 10), "Native confirmation window starts when complete data arrives");
        Check(gate.CanObserveEmptyRun("NO_RUN", true, false, false, false, 13), "Stable acknowledged empty native session is proof");
        gate.Observe("DRAFTING", 14);
        Check(!gate.CanObserveEmptyRun("NO_RUN", true, false, false, false, 17), "Stale server NO_RUN cannot override an active client page");
    }

    private static void CheckHistory(ArenaStatsData data)
        => Check(data.TotalWins == 38 && data.TotalLosses == 32 && data.RunCount == 26 && data.HighestWins == 5 &&
            data.RewardsClaimed == 15 && data.TicketsPurchased == 6 && data.GoldSpent == 900 && data.MatchLogBackfillCompleted,
            "Migration and mode restoration must leave all recorded history unchanged");

    private static void Check(bool pass, string message)
    {
        if (!pass) throw new InvalidOperationException(message);
    }
}
