using System;
using System.Globalization;

namespace ArenaBoxBot;

/// <summary>
/// The SDK matchmaking path does not populate LastGameData.GameConnectionInfo.
/// Identify a game by its native server endpoint and game handle. Client handles
/// are connection credentials and may change when reconnecting to the same game.
/// </summary>
internal static class ArenaGameIdentity
{
    internal static string Resolve(string runKey, int currentGameHandle,
        string sdkAddress, uint sdkPort, uint sdkGameHandle, long sdkClientHandle,
        string legacyAddress, int legacyPort, int legacyGameHandle, long legacyClientHandle,
        bool legacyHasGameHandle, bool legacyHasClientHandle, string pendingKey)
    {
        if (string.IsNullOrEmpty(runKey) || currentGameHandle == 0) return "";
        var current = unchecked((uint)currentGameHandle);
        // Network retains the last joined server during transitions. It may
        // only identify this game when GameMgr independently agrees on its ID.
        if (sdkGameHandle == current && ValidEndpoint(sdkAddress, sdkPort) && sdkClientHandle != 0)
            return Key(runKey, sdkAddress, sdkPort, sdkGameHandle, sdkClientHandle, pendingKey);
        if (legacyHasGameHandle && legacyHasClientHandle && legacyGameHandle != 0 &&
            unchecked((uint)legacyGameHandle) == current && legacyPort > 0 &&
            ValidEndpoint(legacyAddress, unchecked((uint)legacyPort)) && legacyClientHandle != 0)
            return Key(runKey, legacyAddress, unchecked((uint)legacyPort), current, legacyClientHandle, pendingKey);
        return "";
    }

    private static bool ValidEndpoint(string address, uint port)
        => !string.IsNullOrWhiteSpace(address) && port > 0 && port <= 65535;

    private static string Key(string runKey, string address, uint port, uint gameHandle,
        long clientHandle, string pendingKey)
    {
        // Preserve an already counted pre-upgrade game only with an exact
        // match of the old run/game/client tuple; never guess across games.
        var oldKey = runKey + ":" + unchecked((int)gameHandle).ToString(CultureInfo.InvariantCulture) +
            ":" + clientHandle.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(pendingKey, oldKey, StringComparison.Ordinal)) return oldKey;
        // An old pending key has no endpoint. If that same game handle now
        // has a different client, it might be a reconnect or handle reuse;
        // neither a new count nor reuse of the old result is justified.
        var oldPrefix = runKey + ":" + unchecked((int)gameHandle).ToString(CultureInfo.InvariantCulture) + ":";
        if (!string.IsNullOrEmpty(pendingKey) && pendingKey.StartsWith(oldPrefix, StringComparison.Ordinal)) return "";
        return runKey + ":server:" + address.Trim().ToLowerInvariant() + ":" +
            port.ToString(CultureInfo.InvariantCulture) + ":" + gameHandle.ToString(CultureInfo.InvariantCulture);
    }
}
