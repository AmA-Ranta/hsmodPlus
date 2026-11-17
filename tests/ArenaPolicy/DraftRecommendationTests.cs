using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArenaBoxBot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class DraftRecommendationTests
{
    private static int checks;
    internal static void Run()
    {
        var fixture = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "arena-draft-choose-captures-20261026.json")));
        var actual = (JArray)fixture["captures"]!;
        foreach (var c in actual)
        {
            Check(ArenaDraftRecommendationParser.TryParse((string)c["line"]!, out var parsed, out var why),
                "Actual explicit choose callback: " + why);
            Check(parsed.CardIds.SequenceEqual(c["cardIds"]!.Select(x => (string)x!)),
                "Preserve original recommended order and IDs");
        }
        var sourceLine = (string)actual[0]!["line"]!;
        Check(ArenaDraftRecommendationParser.TryParse(sourceLine, out var hero, out _), "Actual hero capture");
        // A counterexample pool, not a claim of an independently captured
        // full server bucket: the preferred hero must win over an S-rated
        // non-recommended hero and an alternative listed second by Box.
        Check(ArenaDraftRecommendationParser.Choice(hero, new[] { "HERO_07", "HERO_03", "HERO_10" }, out _) == 3,
            "Explicit first hero recommendation wins, not highest generic rank");
        Check(ArenaDraftRecommendationParser.Choice(hero, new[] { "HERO_10", "HERO_07", "HERO_03" }, out _) == 1,
            "Map card identity to native order, never treat callback order as a screen position");
        Check(ArenaDraftRecommendationParser.Choice(hero, new[] { "OTHER", "HERO_03", "THIRD" }, out _) == 0,
            "Missing primary recommendation must not fall back to second alternative");
        Check(ArenaDraftRecommendationParser.Choice(hero, new[] { "HERO_10", "HERO_10", "HERO_03" }, out _) == 0,
            "Duplicate native IDs cannot authorize an ambiguous position");
        Check(!ArenaDraftRecommendationParser.TryParse(sourceLine.Insert(25, "\"pid\":16080,"), out _, out _),
            "Duplicate envelope identity rejected");
        foreach (var bad in new[] { "", sourceLine[..^1], sourceLine + " {}", sourceLine.Replace("upstream", "unknown") })
            Check(!ArenaDraftRecommendationParser.TryParse(bad, out _, out _), "Malformed or foreign envelope rejected");

        string Captured(string body, DateTime? stamp = null, int pid = 16080)
        {
            var envelope = JObject.Parse(sourceLine[24..]);
            envelope["pid"] = pid;
            envelope["script"] = "onUpdateArenaRecommend) !== 'undefined') window.onUpdateArenaRecommend(new TextDecoder('utf-8').decode(Uint8Array.from(atob('" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(body)) + "'), c => c.charCodeAt(0))))";
            return (stamp ?? DateTime.Now.AddMilliseconds(1)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                " " + envelope.ToString(Formatting.None);
        }
        const string choose = "{\"status\":2,\"error\":\"\",\"data\":[{\"actionName\":\"choose\",\"card\":{\"cardId\":\"LOW_SCORE\"}}]}";
        foreach (var bad in new[]
        {
            choose.Replace("\"status\":2", "\"status\":1"),
            choose.Replace("\"status\":2", "\"status\":\"2\""),
            choose.Replace("\"error\":\"\"", "\"error\":\"failed\""),
            choose.Replace("\"choose\"", "\"discard\""),
            choose.Replace("\"cardId\":\"LOW_SCORE\"", "\"score\":100"),
            choose.Replace("\"cardId\":\"LOW_SCORE\"", "\"cardId\":\"LOW_SCORE\",\"cardId\":\"OTHER\""),
            choose.Replace("\"cardId\":\"LOW_SCORE\"", "\"cardId\":\"LOW_SCORE\",\"score\":100"),
            "{\"status\":2,\"error\":\"\",\"data\":[]}",
            choose.Replace("\"LOW_SCORE\"", "\"bad|id\"")
        }) Check(!ArenaDraftRecommendationParser.TryParse(Captured(bad), out _, out _), "No scores/error/mixed semantics substitute for choose");

        ArenaRuntimePaths.Root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "draft-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArenaRuntimePaths.Root);
        var log = ArenaRuntimePaths.File("hsang-recommend-call.log");
        void Heartbeat(string producer = "draft-a", int? game = null, int box = 16080)
            => File.WriteAllText(ArenaRuntimePaths.File("boxai-heartbeat.json"), new JObject
            { ["pid"] = box, ["gamePid"] = game ?? Process.GetCurrentProcess().Id,
                ["producerInstanceId"] = producer, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.ToString(Formatting.None));
        var ids = new[] { "HIGH_SCORE", "LOW_SCORE", "POPULAR_CARD" };
        var key = ArenaDraftRecommendationStream.Bucket(true, 111, 0, false, 5, "DRAFT_SLOT_CARD", ids);
        int Read(string? window = null) => ArenaDraftRecommendationStream.Read(window ?? key, ids, out _);
        Heartbeat();
        File.WriteAllText(log, Captured(choose) + "\n");
        ArenaDraftRecommendationStream.BeginNativeWindow(key, "DRAFTING");
        Check(Read() == 0, "Do not scan historical callback tail for a matching card");
        File.AppendAllText(log, Captured(choose) + "\n");
        Check(Read() == 2, "Fresh explicit lower-score recommendation maps to position2");
        Check(Read("other-bucket") == 0, "Cannot cross native bucket ownership");
        Heartbeat(game: Process.GetCurrentProcess().Id + 1);
        Check(Read() == 0, "Wrong actual game heartbeat rejected");
        Heartbeat("draft-b");
        Check(Read() == 0, "Same Box PID generation change invalidates old callback");
        File.AppendAllText(log, Captured(choose) + "\n");
        Check(Read() == 2, "New producer callback recovers safely");
        key = ArenaDraftRecommendationStream.Bucket(true, 111, 0, false, 6, "DRAFT_SLOT_CARD", ids);
        ArenaDraftRecommendationStream.BeginNativeWindow(key, "DRAFTING");
        Check(Read() == 0, "Identical cards in next slot still require new recommendation");
        var fresh = Captured(choose);
        File.AppendAllText(log, fresh[..(fresh.Length / 2)]);
        Check(Read() == 0, "Partial record cannot select");
        File.AppendAllText(log, fresh[(fresh.Length / 2)..]);
        Check(Read() == 0, "No complete record delimiter cannot select");
        File.AppendAllText(log, "\n");
        Check(Read() == 2, "Complete one-shot callback accepted after native boundary");
        File.AppendAllText(log, Captured(choose.Replace("\"status\":2", "\"status\":0")) + "\n");
        Check(Read() == 0, "New upstream failure invalidates previously cached success");
        File.WriteAllText(log, "rotated\n" + Captured(choose) + "\n");
        Check(Read() == 2, "Original log rotation keeps first fresh callback");
        key = ArenaDraftRecommendationStream.Bucket(true, 111, 222, true, 0, "DRAFT_SLOT_CARD", ids);
        ArenaDraftRecommendationStream.BeginNativeWindow(key, "REDRAFTING");
        File.AppendAllText(log, Captured(choose, DateTime.Now.AddMinutes(-3)) + "\n");
        Check(Read() == 0, "Old timestamp appended after boundary rejected");
        File.AppendAllText(log, Captured(choose, DateTime.Now.AddSeconds(20)) + "\n");
        Check(Read() == 0, "Future callback rejected");
        File.AppendAllText(log, Captured(choose, pid: 9999) + "\n");
        Check(Read() == 0, "Foreign Box PID rejected");
        File.AppendAllText(log, Captured(choose) + "\n");
        Check(Read() == 2, "Current redraft recommendation accepted");
        ArenaDraftRecommendationStream.BeginNativeWindow(null, "MIDRUN");
        Check(Read() == 0, "Leaving selection phase clears ownership");
        Check(ArenaDraftRecommendationStream.Bucket(false, 111, 0, false, 5, "DRAFT_SLOT_CARD", ids) !=
            ArenaDraftRecommendationStream.Bucket(true, 111, 0, false, 5, "DRAFT_SLOT_CARD", ids), "Normal/Underground separated");
        Check(ArenaDraftRecommendationStream.Bucket(true, 0, 0, false, 0, "DRAFT_SLOT_HERO", ids) == null,
            "Unbound deck cannot open selection capture");
        Console.WriteLine("PASS: original Box recommended drafting source, no score fallback, exact bucket/producer boundaries (" + checks + " assertions)");
    }
    private static void Check(bool ok, string message)
    {
        checks++;
        if (!ok) throw new InvalidOperationException(message + "; diagnostic=" + ArenaDraftRecommendationStream.Diagnostic);
    }
}
