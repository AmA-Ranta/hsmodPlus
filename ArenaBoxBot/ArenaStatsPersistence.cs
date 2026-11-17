using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArenaBoxBot;

/// <summary>The production statistics format, shared with persistence regressions.</summary>
internal static class ArenaStatsPersistence
{
    private static JsonSerializer CreateSerializer()
        => new JsonSerializer
        {
            TypeNameHandling = TypeNameHandling.None,
            ObjectCreationHandling = ObjectCreationHandling.Replace
        };

    internal static ArenaStatsData Read(string json, out string migrationDetail)
    {
        var root = JObject.Parse(json);
        var normalPresent = root.Property("NormalRun", StringComparison.OrdinalIgnoreCase)?.Value is JObject;
        var undergroundPresent = root.Property("UndergroundRun", StringComparison.OrdinalIgnoreCase)?.Value is JObject;
        var data = root.ToObject(typeof(ArenaStatsData), CreateSerializer()) as ArenaStatsData;
        if (data == null) throw new JsonSerializationException("Arena statistics did not contain an object");
        EnsureCollections(data);
        migrationDetail = "";
        if (data.ModeContextsInitialized && (!normalPresent || !undergroundPresent))
        {
            // An installed build was observed writing the top-level fields
            // and initialized flag but omitting nested mode contexts. Recover
            // the surviving projection without modifying lifetime history or
            // inventing the missing other mode's score.
            if (data.RunKey?.StartsWith("underground:", StringComparison.Ordinal) == true)
                data.RunUnderground = true;
            else if (data.RunKey?.StartsWith("normal:", StringComparison.Ordinal) == true)
                data.RunUnderground = false;
            var currentMissing = data.RunUnderground ? !undergroundPresent : !normalPresent;
            if (currentMissing)
            {
                data.RunOrigin = "recovered-missing-context-projection";
                ArenaStatsLedger.CaptureCurrentContext(data);
            }
            if (!normalPresent && data.RunUnderground)
                data.NormalRun.Origin = "missing-context-awaiting-native";
            if (!undergroundPresent && !data.RunUnderground)
                data.UndergroundRun.Origin = "missing-context-awaiting-native";
            migrationDetail = "missing mode contexts: normal=" + !normalPresent +
                ", underground=" + !undergroundPresent + "; " +
                (currentMissing ? "recovered current projection for " + (data.RunUnderground ? "underground" : "normal") : "retained existing current context") +
                "; lifetime history unchanged; absent other-mode context cannot be reconstructed from this file";
        }
        return data;
    }

    internal static string Write(ArenaStatsData data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        EnsureCollections(data);
        ArenaStatsLedger.CaptureCurrentContext(data);
        using (var text = new StringWriter(CultureInfo.InvariantCulture))
        using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented })
        {
            CreateSerializer().Serialize(writer, data);
            return text.ToString();
        }
    }

    private static void EnsureCollections(ArenaStatsData data)
    {
        if (data.NormalRun == null) data.NormalRun = new ArenaRunContext();
        if (data.UndergroundRun == null) data.UndergroundRun = new ArenaRunContext();
        if (data.MatchLogCursors == null) data.MatchLogCursors = new List<ArenaStatsLogCursor>();
        if (data.RecordedGameKeys == null) data.RecordedGameKeys = new List<string>();
        if (data.CountedRunKeys == null) data.CountedRunKeys = new List<string>();
        if (data.EntryReceipt == null) data.EntryReceipt = new ArenaEntryReceipt();
        if (data.NormalRun.EntryReceipt == null) data.NormalRun.EntryReceipt = new ArenaEntryReceipt();
        if (data.UndergroundRun.EntryReceipt == null) data.UndergroundRun.EntryReceipt = new ArenaEntryReceipt();
    }

    internal static bool IsEntryReceiptPersisted(ArenaStatsData saved, bool underground, ArenaEntryReceipt expected)
    {
        if (saved == null || expected == null || !saved.ModeContextsInitialized ||
            string.IsNullOrEmpty(expected.Nonce) || expected.Underground != underground) return false;
        var context = underground ? saved.UndergroundRun : saved.NormalRun;
        if (!SameEntryReceipt(context?.EntryReceipt, expected)) return false;
        return saved.RunUnderground != underground || SameEntryReceipt(saved.EntryReceipt, expected);
    }

    private static bool SameEntryReceipt(ArenaEntryReceipt actual, ArenaEntryReceipt expected)
        => actual != null && actual.Nonce == expected.Nonce && actual.Underground == expected.Underground &&
           actual.PreviousRunKey == expected.PreviousRunKey && actual.RequestedAtUnixMs == expected.RequestedAtUnixMs &&
           actual.PreviousNativeRunKey == expected.PreviousNativeRunKey &&
           actual.TicketsBefore == expected.TicketsBefore && actual.TicketCost == expected.TicketCost &&
           actual.Phase == expected.Phase && actual.ResponseRunKey == expected.ResponseRunKey &&
           actual.ResponseAtUnixMs == expected.ResponseAtUnixMs && actual.BoundRunKey == expected.BoundRunKey &&
           actual.BoundAtUnixMs == expected.BoundAtUnixMs &&
           actual.EmptyAcknowledgedAtUnixMs == expected.EmptyAcknowledgedAtUnixMs;

    internal static bool SaveAndConfirmEntryReceipt(Func<bool> save, Func<string> read, bool underground,
        ArenaEntryReceipt expected, out string failure)
    {
        failure = "";
        try
        {
            if (!save()) { failure = "statistics write failed"; return false; }
            var saved = Read(read(), out _);
            if (IsEntryReceiptPersisted(saved, underground, expected)) return true;
            failure = "entry receipt mode/nonce/phase/identity read-back mismatch";
        }
        catch (Exception ex) { failure = "entry receipt read-back failed: " + ex.GetType().Name + ": " + ex.Message; }
        return false;
    }
}
