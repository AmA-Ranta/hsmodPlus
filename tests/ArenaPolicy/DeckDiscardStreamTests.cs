using System.Diagnostics;
using System.Globalization;
using ArenaBoxBot;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class DeckDiscardStreamTests
{
    private static int _checks;
    internal static void Run()
    {
        ArenaRuntimePaths.Root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "discard-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArenaRuntimePaths.Root);
        var fixture = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "arena-discard-capture-20261007.json")));
        var sourceLine = (string)fixture["line"]!;
        var log = ArenaRuntimePaths.File("hsang-recommend-call.log");
        var heartbeat = ArenaRuntimePaths.File("boxai-heartbeat.json");
        void Heartbeat(string producer = "producer-a", int? gamePid = null, int boxPid = 23936)
        {
            File.WriteAllText(heartbeat, new JObject { ["pid"] = boxPid, ["gamePid"] = gamePid ?? Process.GetCurrentProcess().Id,
                ["producerInstanceId"] = producer, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.ToString(Formatting.None));
        }
        string Line(DateTime? stamp = null, int pid = 23936)
        {
            var envelope = JObject.Parse(sourceLine.Substring(24));
            envelope["pid"] = pid;
            return (stamp ?? DateTime.Now.AddMilliseconds(5)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                " " + envelope.ToString(Formatting.None);
        }
        var cards = new List<ArenaDeckEditCard>
        {
            new() { CardId = "MEND_303", Count = 2 }, new() { CardId = "EDR_530", Count = 1 },
            new() { CardId = "TLC_987", Count = 1 }, new() { CardId = "EDR_014", Count = 1 },
            new() { CardId = "KEEP_CARD", Count = 30 }
        };
        var window = ArenaDeckDiscardStream.NativeWindow(true, 1, 2, 2, 1);
        ArenaDeckDiscardStream.BoundRecommendation? Read(string? key = null, IList<ArenaDeckEditCard>? merged = null) =>
            ArenaDeckDiscardStream.Read(key ?? window, "native-current-full-context", cards, merged ?? cards, 30);
        Heartbeat();
        File.WriteAllText(log, Line() + "\n");
        ArenaDeckDiscardStream.BeginNativeWindow(window, "REDRAFTING");
        Check(Read() == null, "Historical last discard must never satisfy a new native phase");
        File.AppendAllText(log, Line() + "\n");
        ArenaDeckDiscardStream.Poll();
        ArenaDeckDiscardStream.BeginNativeWindow(window, "EDITING_DECK");
        var received = Read();
        Check(received != null && received.RemoveCounts["MEND_303"] == 2,
            "One-shot event before first editing tick must remain cached with duplicate counts");
        Check(received!.Evidence.Contains("producer-a") && received.Evidence.Contains("nativeWindow=" + window),
            "Evidence must identify the observed producer and native phase");
        Check(Read("wrong-window") == null, "Callback cannot cross a native phase identity");
        var wrongCards = cards.Select(card => new ArenaDeckEditCard { CardId = card.CardId, Count = card.Count }).ToList();
        wrongCards[4].CardId = "WRONG_CARD";
        Check(Read(merged: wrongCards) == null, "Same count but different server multiset must not bind");

        window = ArenaDeckDiscardStream.NativeWindow(true, 1, 2, 2, 2);
        ArenaDeckDiscardStream.BeginNativeWindow(window, "REDRAFTING");
        Check(Read() == null, "Reused deck IDs after another loss must not reuse prior recommendation");
        var split = Line();
        File.AppendAllText(log, split.Substring(0, split.Length / 2));
        Check(Read() == null, "Partial callback line cannot be consumed");
        File.AppendAllText(log, split.Substring(split.Length / 2));
        Check(Read() == null, "Complete JSON without terminating record boundary must wait");
        File.AppendAllText(log, "\n");
        Check(Read() != null, "A split one-shot record is consumed exactly when complete");

        Heartbeat(gamePid: Process.GetCurrentProcess().Id + 1);
        Check(Read() == null, "Heartbeat for another Hearthstone process cannot authorize captured cards");
        Heartbeat("producer-b");
        Check(Read() == null, "Producer restart invalidates a cached recommendation");
        File.AppendAllText(log, Line() + "\n");
        Check(Read() != null, "New callback from freshly bound producer can recover");

        window += ":new-cycle";
        ArenaDeckDiscardStream.BeginNativeWindow(window, "EDITING_DECK");
        File.AppendAllText(log, Line(pid: 9999) + "\n");
        Check(Read() == null, "Envelope PID must equal the fresh heartbeat producer PID");
        File.AppendAllText(log, Line(DateTime.Now.AddMinutes(-5)) + "\n");
        Check(Read() == null, "Newly appended old timestamp cannot impersonate a current recommendation");
        File.AppendAllText(log, Line() + "\n");
        Check(Read() != null, "Current window remains usable after rejected unrelated records");

        File.WriteAllText(log, "rotated\n" + Line() + "\n");
        Check(Read() != null, "Rotation must consume its first one-shot discard instead of seeking past it");
        File.WriteAllText(log, "rotated-again\n" + Line(DateTime.Now.AddMinutes(-5)) + "\n");
        Check(Read() == null, "New file generation cannot bypass the original native-window time boundary");
        File.AppendAllText(log, Line() + "\n");
        Check(Read() != null, "Current callback after rejected old rotated contents may recover");

        File.WriteAllText(heartbeat, "{incomplete");
        window += ":first-bind";
        ArenaDeckDiscardStream.BeginNativeWindow(window, "REDRAFTING");
        File.AppendAllText(log, Line() + "\n");
        Heartbeat("producer-c");
        ArenaDeckDiscardStream.BeginNativeWindow(window, "EDITING_DECK");
        Check(Read() != null, "Initial heartbeat repair must retain the only event already appended after phase entry");
        File.WriteAllText(heartbeat, "{incomplete");
        window += ":first-bind-wrong-pid";
        ArenaDeckDiscardStream.BeginNativeWindow(window, "EDITING_DECK");
        File.AppendAllText(log, Line(pid: 11111) + "\n");
        Heartbeat("producer-d");
        Check(Read() == null, "First binding must not accept another Box PID's intervening record");
        File.AppendAllText(log, Line() + "\n");
        Check(Read() != null, "Initial binding remains usable after rejecting an unrelated Box process");
        File.AppendAllText(log, Line(pid: 23937) + "\n");
        Heartbeat("producer-e", boxPid: 23937);
        Check(Read() != null, "Changed Box PID identifies its already-written one-shot record without an EOF skip");
        File.AppendAllText(log, Line() + "\n");
        Check(Read()!.Evidence.Contains("BoxPid=23937"), "An old producer's later record cannot replace the new producer recommendation");
        ArenaDeckDiscardStream.BeginNativeWindow(window, "MIDRUN");
        Check(Read() == null, "Leaving drafting/editing invalidates phase ownership");
        Console.WriteLine("PASS: actual discard log stream, phase boundaries, split records, producer identity and rotation (" + _checks + " assertions)");
    }
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message + "; diagnostic=" + ArenaDeckDiscardStream.Diagnostic);
    }
}

namespace ArenaBoxBot
{
    // Only filesystem path and logging are substituted; the production stream,
    // parser and multiset policy above execute without a Unity/game process.
    internal static class ArenaRuntimePaths
    {
        internal static string Root = "";
        internal static string File(string name) => Path.Combine(Root, name);
    }
    internal static class ArenaBoxBotPlugin
    {
        internal static TestLogger? LogSource => null;
    }
    internal sealed class TestLogger { internal void LogInfo(string message) { } }
}
