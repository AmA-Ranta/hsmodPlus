using ArenaBoxBot;

internal static class PowerQueueProgressWindowTests
{
    internal static void Run()
    {
        var window = new PowerQueueProgressWindow();
        Require(!window.Observe("game:6:100:2:101", 0), "initial queue is not a stall");
        Require(!window.Observe("game:6:100:2:101", 2), "short animation wait");
        Require(window.Observe("game:6:100:2:101", 3), "same pending work reports after 3 seconds");
        Require(!window.Observe("game:6:100:2:101", 4), "rate limited");
        Require(window.Observe("game:6:100:2:101", 8), "continuing queue blockage remains visible");
        Require(!window.Observe("game:6:100:1:101", 9), "task completion resets stall");
        Require(!window.Observe(null, 20), "empty queue clears history");
        Require(!window.Observe("game:6:100:1:101", 21), "new work has a fresh window");
        Require(!window.Observe("newgame:6:100:1:101", 30), "game identity prevents cross-game carry");
        Require(!window.Observe("newgame:6:100:1:101", 1), "clock rewind resets window");
        Require(window.Observe("newgame:6:100:1:101", 4), "reports after rewind on a fresh threshold");
    }

    private static void Require(bool value, string description)
    {
        if (!value) throw new System.Exception("Power queue observation: " + description);
    }
}
