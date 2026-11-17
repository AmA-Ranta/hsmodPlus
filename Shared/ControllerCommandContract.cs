using System;

namespace ArenaControl.Contracts;

/// <summary>
/// One atomic operation the controller may ask the plugin to perform.  The
/// controller no longer writes any game-side marker or config directly: it
/// publishes one command, the plugin performs it and acknowledges it.
/// </summary>
[Serializable]
public sealed class ControllerCommand
{
    public int SchemaVersion = 1;
    public string RequestId = "";
    /// <summary>stop | returnHub</summary>
    public string Action = "";
    /// <summary>immediate | afterMatch</summary>
    public string Boundary = "";
    /// <summary>configure payload: the plugin applies these runtime settings itself.</summary>
    public string Mode = "";
    public string DeckName = "";
    public int GameLimit;
    public string ArenaMode = "";
    public int ArenaMaxGamesPerRun;
    public bool ArenaStopOnTicket;
    public bool ArenaStopOnGold;
    /// <summary>Receipt result: false means the plugin refused or failed; Detail explains.</summary>
    public bool Ok;
    public string Detail = "";
    public string Note = "";
    public int GameProcessId;
    public long GameStartedAtUtcTicks;
    public long IssuedAtUtcTicks;
    public long CompletedAtUtcTicks;
}

public static class ControllerCommandContract
{
    public const string CommandFile = "controller-command.json";
    public const string ReceiptFile = "controller-command-receipt.json";

    public const string ActionStop = "stop";
    public const string ActionReturnHub = "returnHub";
    public const string ActionConfigure = "configure";
    public const string BoundaryImmediate = "immediate";
    public const string BoundaryAfterMatch = "afterMatch";

    public static bool IsKnownAction(string action)
        => string.Equals(action, ActionStop, StringComparison.Ordinal) ||
           string.Equals(action, ActionReturnHub, StringComparison.Ordinal) ||
           string.Equals(action, ActionConfigure, StringComparison.Ordinal);

    public static bool IsKnownBoundary(string boundary)
        => string.Equals(boundary, BoundaryImmediate, StringComparison.Ordinal) ||
           string.Equals(boundary, BoundaryAfterMatch, StringComparison.Ordinal);

    /// <summary>The command belongs to this exact game process and was issued
    /// after that process started, so a recycled PID or an old client's file
    /// can never drive a new client.</summary>
    public static bool Matches(ControllerCommand command, int pid, long gameStartedAtUtcTicks, long nowUtcTicks)
        => command != null && command.SchemaVersion == 1 && Guid.TryParse(command.RequestId, out var id) &&
           id != Guid.Empty && IsKnownAction(command.Action) && IsKnownBoundary(command.Boundary) &&
           (command.Action != ActionConfigure || !string.IsNullOrEmpty(command.Mode)) &&
           pid > 0 && command.GameProcessId == pid &&
           gameStartedAtUtcTicks > 0 && command.GameStartedAtUtcTicks == gameStartedAtUtcTicks &&
           command.IssuedAtUtcTicks >= gameStartedAtUtcTicks && command.IssuedAtUtcTicks <= nowUtcTicks;

    public static bool Confirms(ControllerCommand expected, ControllerCommand receipt,
        int pid, long gameStartedAtUtcTicks, long nowUtcTicks)
        => Matches(expected, pid, gameStartedAtUtcTicks, nowUtcTicks) &&
           Matches(receipt, pid, gameStartedAtUtcTicks, nowUtcTicks) &&
           receipt.RequestId == expected.RequestId && receipt.Action == expected.Action &&
           receipt.Boundary == expected.Boundary &&
           receipt.IssuedAtUtcTicks == expected.IssuedAtUtcTicks &&
           receipt.CompletedAtUtcTicks > expected.IssuedAtUtcTicks &&
           receipt.CompletedAtUtcTicks <= nowUtcTicks;
}
