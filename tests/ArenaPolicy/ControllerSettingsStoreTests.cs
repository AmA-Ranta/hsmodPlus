using System;
using System.IO;
using ArenaController;

internal static class ControllerSettingsStoreTests
{
    internal static void Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "controller-settings.json");
            var legacy = Path.Combine(root, "legacy-settings.json");
            File.WriteAllText(legacy, "{\"Mode\":\"狂野\",\"DeckName\":\"快攻\"}");
            var store = new ControllerSettingsStore(path, legacy);

            var defaults = store.Load();
            Check(defaults.Mode == "标准", "missing file loads defaults");

            Check(store.MigrateLegacyIfNeeded(), "legacy settings are migrated once");
            Check(!store.MigrateLegacyIfNeeded(), "a second migration is a no-op");
            Check(store.Load().Mode == "狂野", "migrated settings are readable");

            var settings = store.Load();
            settings.Mode = "竞技场";
            settings.ArenaMode = "地下竞技场";
            settings.MaxRuns = 7;
            store.Save(settings);
            var reloaded = store.Load();
            Check(reloaded.Mode == "竞技场" && reloaded.ArenaMode == "地下竞技场" && reloaded.MaxRuns == 7,
                "settings round-trip through the store");

            File.WriteAllText(path, "{ not json");
            Check(store.Load().Mode == "标准", "a corrupt file falls back to defaults");

            var copy = ControllerSettingsStore.Clone(settings);
            copy.Mode = "休闲";
            Check(settings.Mode == "竞技场" && copy.Mode == "休闲", "clone is a deep copy");
            Console.WriteLine("PASS: controller settings store (7 assertions)");
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
