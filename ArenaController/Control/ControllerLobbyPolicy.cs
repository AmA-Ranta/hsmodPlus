using System;

namespace ArenaController;

/// <summary>
/// Starting a task must always begin from the main lobby (HUB/TOURNAMENT), not
/// from a settlement page, a draft screen or the middle of a mode transition.
/// This policy is pure so the decision can be tested without a live client.
/// </summary>
public enum LobbyWaitAction
{
    /// <summary>Scene heartbeat missing or stale: keep waiting, do not act.</summary>
    Wait,

    /// <summary>Already on the main lobby; the script may be enabled.</summary>
    AlreadyAtLobby,

    /// <summary>A match is running: never interrupt it, wait for the boundary.</summary>
    WaitForMatchEnd,

    /// <summary>Client is still logging in; wait for the hub instead of nudging.</summary>
    WaitForLogin,

    /// <summary>Some other page (rewards, draft, pack opening, mode select...): ask the plugin to return to the hub.</summary>
    RequestHub,

    /// <summary>The client is stuck on a fatal error page; only a restart can
    /// recover it (SceneMgr refuses to navigate away from FATAL_ERROR).</summary>
    RestartClient
}

public static class ControllerLobbyPolicy
{
    /// <summary>A scene heartbeat older than this cannot prove where the client is.</summary>
    public const int SceneHeartbeatMaxAgeMs = 5000;

    public static bool IsHeartbeatFresh(long updatedAtUnixMs, long nowUnixMs)
    {
        if (updatedAtUnixMs <= 0) return false;
        // Tolerate a little clock skew but never accept a heartbeat from the future.
        if (updatedAtUnixMs - nowUnixMs > SceneHeartbeatMaxAgeMs) return false;
        return nowUnixMs - updatedAtUnixMs <= SceneHeartbeatMaxAgeMs;
    }

    public static bool IsMainLobbyScene(string? scene) =>
        string.Equals(scene, "HUB", StringComparison.Ordinal) ||
        string.Equals(scene, "TOURNAMENT", StringComparison.Ordinal);

    public static bool IsMatchScene(string? scene) =>
        string.Equals(scene, "GAMEPLAY", StringComparison.Ordinal) ||
        string.Equals(scene, "GAME_MODE", StringComparison.Ordinal);

    public static bool IsLoginScene(string? scene) =>
        string.Equals(scene, "LOGIN", StringComparison.Ordinal);

    public static bool IsFatalErrorScene(string? scene) =>
        string.Equals(scene, "FATAL_ERROR", StringComparison.Ordinal);

    /// <summary>How long the controller tolerates a login screen before giving up.</summary>
    public const int LoginWarnSeconds = 45;

    /// <summary>After this long on the login screen the client is considered
    /// stuck (the login intro popup never appears); the controller stops with an
    /// actionable message instead of waiting for the full lobby deadline.</summary>
    public const int LoginStuckSeconds = 600;

    public enum LoginStallAction
    {
        None,
        /// <summary>Ask the plugin to clear the login intro/blockers.</summary>
        RequestCleanup,
        /// <summary>Give up with a message telling the operator to log in manually.</summary>
        GiveUp
    }

    public static LoginStallAction DecideLoginStall(int secondsOnLoginScreen)
    {
        if (secondsOnLoginScreen >= LoginStuckSeconds) return LoginStallAction.GiveUp;
        if (secondsOnLoginScreen >= LoginWarnSeconds) return LoginStallAction.RequestCleanup;
        return LoginStallAction.None;
    }

    /// <summary>A fresh scene heartbeat is authoritative proof of a live match;
    /// a mode switch must never land on it, even when a stale activity label says
    /// the lobby is idle.</summary>
    /// <summary>A fresh heartbeat with no live match and no match loading is
    /// authoritative proof that the client is between matches.</summary>
    public static bool IsQuietSceneHeartbeat(string? scene, bool gameplay, bool heartbeatFresh)
        => heartbeatFresh && !gameplay && !string.Equals(scene, "GAME_MODE", StringComparison.Ordinal);

    public static bool IsLiveMatchConfirmed(string? scene, bool gameplay, bool heartbeatFresh)
        => heartbeatFresh && gameplay;

    public static LobbyWaitAction Decide(string? scene, bool gameplay, bool heartbeatFresh)
    {
        if (!heartbeatFresh) return LobbyWaitAction.Wait;
        if (IsFatalErrorScene(scene)) return LobbyWaitAction.RestartClient;
        // The plugin reports gameplay=true for a real match and for GAME_MODE
        // loading. GAMEPLAY with gameplay=false is the post-game results screen
        // (common after Battlegrounds), which must not block the lobby gate.
        if (gameplay) return LobbyWaitAction.WaitForMatchEnd;
        if (IsLoginScene(scene)) return LobbyWaitAction.WaitForLogin;
        if (IsMainLobbyScene(scene)) return LobbyWaitAction.AlreadyAtLobby;
        return LobbyWaitAction.RequestHub;
    }
}
