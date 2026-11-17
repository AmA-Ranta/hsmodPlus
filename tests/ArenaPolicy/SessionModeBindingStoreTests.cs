using System;
using System.IO;
using ArenaController;

internal static class SessionModeBindingStoreTests
{
    internal static void Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "binding-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "session-mode.json");
            var store = new SessionModeBindingStore(path);
            Check(store.Read() == null, "missing binding reads as null");

            var started = new DateTime(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);
            store.Write("狂野", 4242, started);
            var binding = store.Read();
            Check(binding != null && binding.Mode == "狂野" && binding.GameProcessId == 4242,
                "binding round-trips mode and process id");
            Check(SessionModeBindingStore.Matches(binding, "狂野", 4242, started),
                "matching mode + same process instance is accepted");
            Check(!SessionModeBindingStore.Matches(binding, "标准", 4242, started),
                "a different mode is rejected");
            Check(!SessionModeBindingStore.Matches(binding, "狂野", 9999, started),
                "a different process id is rejected");
            Check(!SessionModeBindingStore.Matches(binding, "狂野", 4242, started.AddMinutes(1)),
                "a different process start time is rejected");

            File.WriteAllText(path, "{ broken");
            Check(store.Read() == null, "a corrupt binding reads as null instead of throwing");
            Console.WriteLine("PASS: session mode binding store (7 assertions)");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
