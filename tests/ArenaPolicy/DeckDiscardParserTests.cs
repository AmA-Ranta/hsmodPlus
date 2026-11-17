using System.Text;
using ArenaBoxBot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class DeckDiscardParserTests
{
    private static int _checks;

    internal static void Run()
    {
        var fixture = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "arena-discard-capture-20261007.json")));
        var line = (string)fixture["line"]!;
        Check(ArenaDeckDiscardParser.TryParse(line, out var parsed, out var reason), "Captured event must parse: " + reason);
        Check(parsed.BoxPid == 23936 && parsed.Source == "upstream", "Native producer fields must survive parsing");
        Check(parsed.LogTimeLocal == new DateTime(2026, 10, 7, 16, 41, 2, 491) &&
            parsed.LogTimeLocal.Kind == DateTimeKind.Unspecified, "Log timestamp is local wall time without an invented timezone");
        Check(parsed.ScriptHash == (string)fixture["expectedScriptHash"]!, "Evidence hash must cover the original exact script");
        Check(parsed.TotalCopies == 5 && parsed.RemoveCounts.Count == 4 && parsed.RemoveCounts["MEND_303"] == 2,
            "Actual duplicate 雷象 entries represent two copies, not one distinct ID");
        foreach (var property in ((JObject)fixture["expectedRemoveCounts"]!).Properties())
            Check(parsed.RemoveCounts[property.Name] == (int)property.Value, "Actual discard copy count: " + property.Name);

        Reject(null!, "null line");
        Reject("", "empty line");
        Reject(line.Substring(24), "missing timestamp");
        Reject("2026-02-30T16:41:02.491" + line.Substring(23), "invalid calendar timestamp");
        Reject(line.Substring(0, line.Length - 1), "truncated envelope");
        Reject(line + " {}", "second JSON document");
        Reject(line.Insert(25, "\"pid\":23936,"), "duplicate envelope identity property");
        Reject(ChangeEnvelope(line, j => j["pid"] = 0), "invalid PID");
        Reject(ChangeEnvelope(line, j => j["pid"] = "23936"), "string PID");
        Reject(ChangeEnvelope(line, j => j["pid"] = long.MaxValue), "PID overflow");
        Reject(ChangeEnvelope(line, j => j["source"] = "unknown"), "unknown producer source");
        Reject(ChangeEnvelope(line, j => j["type"] = "recommend_call"), "wrong event type");
        Reject(ChangeEnvelope(line, j => j["name"] = "onUpdateLadderActionRecommend"), "gameplay recommendation");
        Reject(ChangeEnvelope(line, j => j["extra"] = 1), "unknown envelope semantics");
        Reject(ChangeEnvelope(line, j => j.Remove("script")), "missing script");
        Reject(ChangeEnvelope(line, j => j["script"] = 123), "nonstring script");
        Reject(ChangeEnvelope(line, j => j["script"] = ((string)j["script"]!)[..^1]), "truncated JS wrapper");
        Reject(ChangeEnvelope(line, j => j["script"] = (string)j["script"]! + ";otherCall()"), "appended executable text");
        Reject(ChangeEnvelope(line, j => j["script"] = "otherCall();" + (string)j["script"]!), "prepended executable text");
        Reject(ChangeEnvelope(line, j => j["script"] = ((string)j["script"]!).Replace("atob('", "atob('?")), "invalid base64 alphabet");
        Reject(ChangeEnvelope(line, j => j["script"] = new string('x', ArenaDeckDiscardParser.ScriptCaptureLimit)), "collector cap boundary");
        Reject(ChangeEnvelope(line, j => j["script"] = new string('中', ArenaDeckDiscardParser.ScriptCaptureLimit / 3 + 1)),
            "collector cap is bytes, not UTF16 character count");

        Reject(ChangePayload(line, p => p["status"] = 1), "unsuccessful status");
        Reject(ChangePayload(line, p => p["status"] = "2"), "string status");
        Reject(ChangePayload(line, p => p["status"] = true), "boolean status");
        Reject(ChangePayload(line, p => p["error"] = "not ready"), "nonempty upstream error");
        Reject(ChangePayload(line, p => p["error"] = null), "null upstream error");
        Reject(ChangePayload(line, p => p["deckId"] = 123), "unrecognized payload contract requires review");
        Reject(ChangePayload(line, p => p["data"] = new JArray()), "missing action");
        Reject(ChangePayload(line, p => ((JArray)p["data"]!).Add(p["data"]![0]!.DeepClone())), "multiple discard alternatives");
        Reject(ChangePayload(line, p => ((JArray)p["data"]!).Add(new JObject { ["actionName"] = "choose" })), "mixed choose/discard actions");
        Reject(ChangePayload(line, p => p["data"]![0]!["actionName"] = "choose"), "choice is not discard");
        Reject(ChangePayload(line, p => p["data"]![0]!["type"] = "other"), "unknown discard type");
        Reject(ChangePayload(line, p => ((JObject)p["data"]![0]!).Remove("type")), "missing reference type");
        Reject(ChangePayload(line, p => p["data"]![0]!["count"] = 5), "unsupported action count semantics");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"] = new JArray()), "empty removal array");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"] = new JObject { ["cardId"] = "MEND_303" }), "single object copy semantics not observed");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"]![0] = "MEND_303"), "string card not observed object");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"]![0]!["count"] = 2), "unsupported card count field");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"]![0]!["cardId"] = ""), "blank card ID");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"]![0]!["cardId"] = "MEND_303|0"), "card ID separator injection");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"]![0]!["cardId"] = 123), "numeric card ID");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"]![0]!["cardId"] = "迁徙的雷象"), "card name cannot replace ID");
        Reject(ChangePayload(line, p => p["data"]![0]!["card"]![0]!["cardId"] = new string('A', 129)), "oversized card ID");
        Reject(ChangeRawPayload(line, _ => "{\"status\":0,\"status\":2,\"error\":\"\",\"data\":[]}"), "duplicate payload status");
        Reject(ChangeRawPayload(line, raw => raw.Replace("\"MEND_303\"", "'MEND_303'")), "permissive JS string quoting");
        Reject(ChangeRawPayload(line, raw => "/*comment*/" + raw), "comments are not wire JSON");
        Reject(ChangeRawPayload(line, raw => raw.Replace("\"status\":2", "status:2")), "unquoted property names");
        Reject(ChangeRawPayload(line, raw => raw.Replace("\"status\":2", "\"status\":+2")), "non-JSON positive number sign");
        Reject(ChangeRawPayload(line, raw => raw[..^1] + ",}"), "trailing object comma");
        Reject(ChangeRawPayload(line, raw => raw + " {}"), "multiple embedded JSON documents");
        Reject(ChangeRawPayload(line, raw => raw[..^1]), "truncated embedded JSON");
        Reject(WithPayloadBytes(line, new byte[] { 0xff, 0xfe }), "invalid UTF8 must not silently replace bytes");

        var execute = ChangeEnvelope(line, j => j["source"] = "execute");
        Check(ArenaDeckDiscardParser.TryParse(execute, out var copy, out _) && copy.ScriptHash == parsed.ScriptHash,
            "The same exact recommendation from another collector hook retains its identity");
        var lowerToken = ChangePayload(line, p => p["data"]![0]!["card"]![0]!["cardId"] = "TIME_001t2");
        Check(ArenaDeckDiscardParser.TryParse(lowerToken, out _, out _), "Real card IDs can have lowercase token suffixes");
        var abFixture = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "arena-discard-ab-capture-20261007.json")));
        var abLine = (string)abFixture["line"]!;
        Check(ArenaDeckDiscardParser.TryParse(abLine, out var ab, out reason), "Actual A/B response must parse: " + reason);
        Check(ab.TotalCopies == 5 && ab.RemoveCounts.Count == 4 && ab.RemoveCounts["MEND_303"] == 2 && ab.RemoveCounts["EDR_530"] == 1,
            "Reference A retains its exact five copies; four-copy B must not replace or mix with it");
        foreach (var property in ((JObject)abFixture["expectedReferenceCounts"]!).Properties())
            Check(ab.RemoveCounts[property.Name] == (int)property.Value, "Reference-only A/B count: " + property.Name);
        var reversed = ChangePayload(abLine, p =>
        {
            var actions = (JArray)p["data"]!;
            p["data"] = new JArray(actions[1]!.DeepClone(), actions[0]!.DeepClone());
        });
        Check(ArenaDeckDiscardParser.TryParse(reversed, out var reversedResult, out _) && reversedResult.TotalCopies == 5 &&
            reversedResult.RemoveCounts.ContainsKey("EDR_530"), "Select A by explicit reference type, never array position");
        Reject(ChangePayload(abLine, p => ((JArray)p["data"]!).RemoveAt(0)), "custom-only B cannot replace missing A");
        Reject(ChangePayload(abLine, p => p["data"]![1]!["type"] = "reference"), "duplicate reference A alternatives");
        Reject(ChangePayload(abLine, p => p["data"]![1]!["type"] = "unknown"), "unknown alternative type");
        Reject(ChangePayload(abLine, p => p["data"]![1]!["actionName"] = "choose"), "mixed action in alternative");
        Reject(ChangePayload(abLine, p => p["data"]![1]!["card"]![0]!["count"] = 2), "unknown quantity semantics in B");
        var shortA = ChangePayload(abLine, p => ((JArray)p["data"]![0]!["card"]!).RemoveAt(1));
        Check(ArenaDeckDiscardParser.TryParse(shortA, out var incomplete, out _) && incomplete.TotalCopies == 4 &&
            !incomplete.RemoveCounts.ContainsKey("EDR_530"), "Parser must not complete a short A from B; outer native target-count guard rejects it");
        Console.WriteLine("PASS: exact Box Arena discard parser, real duplicate-copy fixture and " + _checks + " checks");
    }

    private static string ChangeEnvelope(string line, Action<JObject> change)
    {
        var envelope = JObject.Parse(line.Substring(24));
        change(envelope);
        return line.Substring(0, 24) + envelope.ToString(Formatting.None);
    }

    private static string ChangePayload(string line, Action<JObject> change)
        => ChangeRawPayload(line, raw => { var data = JObject.Parse(raw); change(data); return data.ToString(Formatting.None); });

    private static string ChangeRawPayload(string line, Func<string, string> change)
    {
        var script = (string)JObject.Parse(line.Substring(24))["script"]!;
        var start = script.IndexOf("atob('", StringComparison.Ordinal) + 6;
        var end = script.IndexOf("')", start, StringComparison.Ordinal);
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(script[start..end]));
        return WithPayloadBytes(line, Encoding.UTF8.GetBytes(change(raw)));
    }

    private static string WithPayloadBytes(string line, byte[] payload)
        => ChangeEnvelope(line, envelope =>
        {
            var script = (string)envelope["script"]!;
            var start = script.IndexOf("atob('", StringComparison.Ordinal) + 6;
            var end = script.IndexOf("')", start, StringComparison.Ordinal);
            envelope["script"] = script[..start] + Convert.ToBase64String(payload) + script[end..];
        });

    private static void Reject(string line, string scenario)
    {
        Check(!ArenaDeckDiscardParser.TryParse(line, out var result, out var reason) && result == null && !string.IsNullOrEmpty(reason),
            "Must reject " + scenario);
    }

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
