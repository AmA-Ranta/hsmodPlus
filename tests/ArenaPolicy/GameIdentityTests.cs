using ArenaBoxBot;

internal static class GameIdentityTests
{
    internal static void Run()
    {
        const string run = "normal:151:2770175839";
        var data = new ArenaStatsData();
        ArenaStatsLedger.SelectMode(data, false);
        ArenaStatsLedger.StartRun(data, run, 1, 1);
        // Actual 2026-10-07 SDK queue: game/client logged before GAMEPLAY,
        // but LastGameData.GameConnectionInfo remains null after OnGameSetup.
        var sdk = Resolve(run, 5825, "5.42.177.142", 3724, 5825, 80802853);
        Check(sdk == run + ":server:5.42.177.142:3724:5825", "SDK identity must not require legacy protobuf fields");
        Check(ArenaStatsLedger.BeginGame(data, sdk) && data.AutomaticGamesInRun == 1,
            "Real SDK queue must count the game immediately");
        Check(!ArenaStatsLedger.BeginGame(data, sdk), "Repeated gameplay ticks cannot recount");

        var legacy = ArenaGameIdentity.Resolve(run, 5825, null!, 0, 0, 0,
            "5.42.177.142", 3724, 5825, 80802853, true, true, data.PendingGameKey);
        Check(legacy == sdk && !ArenaStatsLedger.BeginGame(data, legacy),
            "Legacy ConnectToGame and SDK paths identify the same server game");
        var reconnect = Resolve(run, 5825, "5.42.177.142", 3724, 5825, 80802854, data.PendingGameKey);
        Check(reconnect == sdk, "A new client connection handle must not invent a new game");
        data = ArenaStatsPersistence.Read(ArenaStatsPersistence.Write(data), out _);
        Check(!ArenaStatsLedger.BeginGame(data, reconnect) && data.AutomaticGamesInRun == 1,
            "Process restart/reconnect retains the counted server game");
        Check(ArenaStatsLedger.FinishGame(data, reconnect, false) && data.TotalLosses == 1,
            "SDK game-over receipt must reconcile exactly one result");
        Check(!ArenaStatsLedger.FinishGame(data, legacy, false), "A second source cannot duplicate the result");

        Check(Resolve(run, 5826, "5.42.177.142", 3724, 5825, 80802853) == "",
            "Previous server metadata cannot identify the next GameMgr handle");
        var next = Resolve(run, 5826, "5.42.177.142", 3724, 5826, 80802900, data.PendingGameKey);
        Check(next != sdk && ArenaStatsLedger.BeginGame(data, next) && data.AutomaticGamesInRun == 2,
            "Next server game counts exactly once");
        Check(Resolve(run, 5825, "5.42.177.143", 3724, 5825, 80802900) != sdk,
            "Server-local handle reuse on another host cannot merge distinct games");
        Check(Resolve(run, 5825, "5.42.177.142", 3725, 5825, 80802900) != sdk,
            "Server-local handle reuse on another port cannot merge distinct games");
        Check(Resolve("underground:151:other", 5825, "5.42.177.142", 3724, 5825, 80802853) != sdk,
            "Arena run and mode remain part of identity");

        var old = run + ":5825:80802853";
        Check(Resolve(run, 5825, "5.42.177.142", 3724, 5825, 80802853, old) == old,
            "Exact persisted pre-upgrade identity preserves its counted game");
        Check(Resolve(run, 5825, "5.42.177.142", 3724, 5825, 80802854, old) == "",
            "Ambiguous old identity with a changed client must not count another game or reuse an unproven result");
        Check(Resolve(run, 0, "5.42.177.142", 3724, 5825, 80802853) == "", "No current GameMgr handle means no identity");
        Check(Resolve("", 5825, "5.42.177.142", 3724, 5825, 80802853) == "", "No run means no identity");
        Check(Resolve(run, 5825, "", 3724, 5825, 80802853) == "", "No server endpoint means no identity");
        Check(Resolve(run, 5825, "5.42.177.142", 0, 5825, 80802853) == "", "No server port means no identity");
        Check(Resolve(run, 5825, "5.42.177.142", 3724, 5825, 0) == "", "No native client connection evidence means no identity");
        Check(ArenaGameIdentity.Resolve(run, 5825, null!, 0, 0, 0,
            "5.42.177.142", 3724, 5825, 80802853, false, true, "") == "",
            "Legacy protobuf presence flags still guard that fallback");
        var fallback = ArenaGameIdentity.Resolve(run, 5825, "5.42.177.142", 3724, 5824, 80802852,
            "5.42.177.142", 3724, 5825, 80802853, true, true, "");
        Check(fallback == sdk, "A verified current legacy packet may replace stale SDK metadata");
        Console.WriteLine("PASS: native SDK/legacy game identity, persisted reconnect and per-game budget");
    }

    private static string Resolve(string run, int current, string address, uint port, uint game, long client, string pending = "")
        => ArenaGameIdentity.Resolve(run, current, address, port, game, client, null!, 0, 0, 0, false, false, pending);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
