using System;
using ArenaController;

internal static class ControllerLobbyPolicyTests
{
    internal static void Run()
    {
        // Heartbeat freshness decides whether we may trust the reported scene.
        Check(ControllerLobbyPolicy.IsHeartbeatFresh(1_000_000, 1_001_000), "1s old heartbeat is fresh");
        Check(!ControllerLobbyPolicy.IsHeartbeatFresh(1_000_000, 1_006_000), "6s old heartbeat is stale");
        Check(!ControllerLobbyPolicy.IsHeartbeatFresh(0, 1_000_000), "missing heartbeat is not fresh");
        Check(!ControllerLobbyPolicy.IsHeartbeatFresh(2_000_000, 1_000_000), "heartbeat far in the future is rejected");

        // Scene classification.
        Check(ControllerLobbyPolicy.IsMainLobbyScene("HUB"), "HUB is the main lobby");
        Check(ControllerLobbyPolicy.IsMainLobbyScene("TOURNAMENT"), "TOURNAMENT is the mode lobby");
        Check(!ControllerLobbyPolicy.IsMainLobbyScene("REWARDS"), "rewards page is not a lobby");
        Check(ControllerLobbyPolicy.IsMatchScene("GAMEPLAY"), "GAMEPLAY is a match");
        Check(ControllerLobbyPolicy.IsMatchScene("GAME_MODE"), "GAME_MODE is a match");
        Check(ControllerLobbyPolicy.IsLoginScene("LOGIN"), "LOGIN is the login scene");

        // The decision the controller follows before enabling the script.
        Check(ControllerLobbyPolicy.Decide("HUB", false, true) == LobbyWaitAction.AlreadyAtLobby,
            "fresh HUB means the script may start");
        Check(ControllerLobbyPolicy.Decide("TOURNAMENT", false, true) == LobbyWaitAction.AlreadyAtLobby,
            "fresh TOURNAMENT also counts as the main lobby");
        Check(ControllerLobbyPolicy.Decide("GAMEPLAY", true, true) == LobbyWaitAction.WaitForMatchEnd,
            "a running match must never be interrupted");
        Check(ControllerLobbyPolicy.Decide("GAMEPLAY", false, true) == LobbyWaitAction.RequestHub,
            "the post-game results screen keeps scene=GAMEPLAY but must return to the hub");
        Check(ControllerLobbyPolicy.Decide("HUB", true, true) == LobbyWaitAction.WaitForMatchEnd,
            "the gameplay flag wins over a stale HUB label");
        Check(ControllerLobbyPolicy.Decide("LOGIN", false, true) == LobbyWaitAction.WaitForLogin,
            "login is waited out, not nudged");
        Check(ControllerLobbyPolicy.Decide("REWARDS", false, true) == LobbyWaitAction.RequestHub,
            "settlement page must return to the hub before the script starts");
        Check(ControllerLobbyPolicy.Decide("DRAFT", false, true) == LobbyWaitAction.RequestHub,
            "draft page must return to the hub before the script starts");
        Check(ControllerLobbyPolicy.Decide("PACKOPENING", false, true) == LobbyWaitAction.RequestHub,
            "pack opening must return to the hub before the script starts");
        Check(ControllerLobbyPolicy.Decide("HUB", false, false) == LobbyWaitAction.Wait,
            "stale heartbeat never counts as a lobby");
        Check(ControllerLobbyPolicy.Decide("FATAL_ERROR", false, true) == LobbyWaitAction.RestartClient,
            "fatal error page requires a client restart, not a hub request");
        Check(!ControllerLobbyPolicy.IsFatalErrorScene("HUB"), "hub is not a fatal error");
        Check(ControllerLobbyPolicy.Decide(null, false, true) == LobbyWaitAction.RequestHub,
            "unknown scene is handled like a non-lobby page");

        Check(ControllerLobbyPolicy.IsLiveMatchConfirmed("GAMEPLAY", true, true),
            "fresh gameplay heartbeat confirms a live match");
        Check(ControllerLobbyPolicy.IsLiveMatchConfirmed("GAME_MODE", true, true),
            "loading into a match is reported as gameplay by the plugin");
        Check(!ControllerLobbyPolicy.IsLiveMatchConfirmed("GAME_MODE", false, true),
            "a scene name alone no longer proves a live match: the plugin flag is authoritative");
        Check(!ControllerLobbyPolicy.IsLiveMatchConfirmed("HUB", false, true),
            "hub is never a live match");
        Check(!ControllerLobbyPolicy.IsLiveMatchConfirmed("GAMEPLAY", true, false),
            "stale heartbeat cannot block forever");
        Check(ControllerLobbyPolicy.DecideLoginStall(10) == ControllerLobbyPolicy.LoginStallAction.None,
            "a short login is normal");
        Check(ControllerLobbyPolicy.DecideLoginStall(ControllerLobbyPolicy.LoginWarnSeconds) == ControllerLobbyPolicy.LoginStallAction.RequestCleanup,
            "45s on the login screen asks the plugin to clean up");
        Check(ControllerLobbyPolicy.DecideLoginStall(ControllerLobbyPolicy.LoginStuckSeconds) == ControllerLobbyPolicy.LoginStallAction.GiveUp,
            "10 minutes stuck at login gives up with an actionable message");
        Check(ControllerLobbyPolicy.IsQuietSceneHeartbeat("HUB", false, true),
            "a fresh heartbeat without a match is a quiet boundary");
        Check(!ControllerLobbyPolicy.IsQuietSceneHeartbeat("GAME_MODE", false, true),
            "loading into a match is not a quiet boundary");
        Check(!ControllerLobbyPolicy.IsQuietSceneHeartbeat("GAMEPLAY", true, true),
            "a live match is not a quiet boundary");
        Check(!ControllerLobbyPolicy.IsQuietSceneHeartbeat("HUB", false, false),
            "a stale heartbeat proves nothing");
        Check(ConstructedStopBoundary.IsKnownSafe("竞技场脚本已关闭", false),
            "an Arena idle label is a safe stop boundary once the script is off");
        Check(ConstructedStopBoundary.IsKnownSafe("脚本未开启", false),
            "the generic stopped label is a safe boundary");
        Check(ControllerLaunchRecoveryPolicy.Decide(true, true, "FATAL_ERROR") == LaunchRecoveryAction.RestartClient,
            "a crashed client is restarted once after a failed launch wait");
        Check(ControllerLaunchRecoveryPolicy.Decide(true, true, "LOGIN") == LaunchRecoveryAction.Continue,
            "a live client with a slow launch-chain check continues instead of failing");
        Check(ControllerLaunchRecoveryPolicy.Decide(true, false, "LOGIN") == LaunchRecoveryAction.Fail,
            "a stale heartbeat still fails the task");
        Check(ControllerLaunchRecoveryPolicy.Decide(false, false, "") == LaunchRecoveryAction.Fail,
            "no heartbeat at all fails the task");
        var decks = new[] {
            new ConstructedDeckOption("deck:1", "快攻  (标准)"),
            new ConstructedDeckOption("deck:2", "海盗战  (狂野)"),
            new ConstructedDeckOption("deck:3", "快攻  (狂野)")
        };
        Check(ConstructedDeckSelection.Resolve(decks, "海盗战", "狂野").Value == "deck:2",
            "a bare deck name resolves through the HsMod deck list");
        Check(ConstructedDeckSelection.Resolve(decks, "快攻  (标准)", "标准").Value == "deck:1",
            "the full decorated label resolves too");
        var duplicate = false;
        try { ConstructedDeckSelection.Resolve(decks, "快攻", "标准"); } catch (InvalidOperationException) { duplicate = true; }
        Check(duplicate, "an ambiguous bare name asks for the full label instead of guessing");
        var missing = false;
        try { ConstructedDeckSelection.Resolve(decks, "不存在", "狂野"); } catch (InvalidOperationException ex) { missing = ex.Message.Contains("海盗战"); }
        Check(missing, "a missing deck lists the decks that do exist in that mode");
        var empty = false;
        try { ConstructedDeckSelection.Resolve(Array.Empty<ConstructedDeckOption>(), "快攻", "狂野"); } catch (InvalidOperationException) { empty = true; }
        Check(empty, "an empty mode deck list fails with a clear message");
        Check(ConstructedDeckSelection.Resolve(new[] { new ConstructedDeckOption("deck:9", "海盗战  (狂野)") }, "快攻", "狂野", true).Value == "deck:9",
            "a mode with a single deck auto-selects it instead of stalling");
        var stillMissing = false;
        try { ConstructedDeckSelection.Resolve(decks, "不存在", "狂野", true); } catch (InvalidOperationException) { stillMissing = true; }
        Check(stillMissing, "auto-select never guesses when several decks exist");
        Console.WriteLine("PASS: controller lobby policy (47 assertions)");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
