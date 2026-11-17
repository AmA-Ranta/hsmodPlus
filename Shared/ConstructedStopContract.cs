using System;

namespace ArenaControl.Contracts;

[Serializable]
public sealed class ConstructedStopRequest
{
    public int SchemaVersion = 1;
    public string RequestId = "";
    public int GameProcessId;
    public long GameStartedAtUtcTicks;
    public long RequestedAtUtcTicks;
    public string Mode = "";
    public long CompletedAtUtcTicks;
}

public static class ConstructedStopContract
{
    public const string RequestFile = "controller-constructed-stop-after-game.json";
    public const string ReceiptFile = "controller-constructed-stop-completed.json";

    public static bool CanCompleteStop(bool gameRunning, bool gameplayKnown, bool gameplayActive,
        bool receiptConfirmed, bool nativeSafeWhenScriptOff = false)
        => !gameRunning ||
           (receiptConfirmed && !(gameplayKnown && gameplayActive)) ||
           // A stale GAMEPLAY marker in the native log must not trap the stop
           // forever: the fresh API already reports the script off and a safe
           // post-game/idle activity.
           // A stopped script on a fresh post-game/idle boundary is already
           // safe; the plugin receipt can be missing when it consumed the
           // request before the settled match ended.
           nativeSafeWhenScriptOff;

    public static int RemainingGames(int limit, int? baseline, int current, bool reliable)
    {
        if (limit == 0) return 0;
        if (limit < 0 || !reliable || !baseline.HasValue || baseline.Value < 0 || current < baseline.Value)
            throw new InvalidOperationException("Task game count is not confirmed");
        return Math.Max(0, limit - (current - baseline.Value));
    }

    public static bool Matches(ConstructedStopRequest request, int pid, long started, string mode, long now)
        => request != null && request.SchemaVersion == 1 &&
           Guid.TryParse(request.RequestId, out var id) && id != Guid.Empty &&
           pid > 0 && request.GameProcessId == pid && started > 0 && request.GameStartedAtUtcTicks == started &&
           request.RequestedAtUtcTicks >= started && request.RequestedAtUtcTicks <= now &&
           (mode == "标准" || mode == "狂野" || mode == "休闲" || mode == "酒馆战棋") && request.Mode == mode;

    // Unknown scenes and transitions never authorize disabling gameplay.
    public static bool IsSafeBoundary(string scene, string nextScene, bool transitioning,
        bool findingGame, bool connectedToGame)
        => (scene == "HUB" || scene == "TOURNAMENT" || scene == "BACON") &&
           nextScene == "INVALID" && !transitioning && !findingGame && !connectedToGame;

    public static bool Confirms(ConstructedStopRequest expected, ConstructedStopRequest completed,
        int pid, long started, string mode, long now)
        => Matches(expected, pid, started, mode, now) && Matches(completed, pid, started, mode, now) &&
           completed.RequestId == expected.RequestId && completed.RequestedAtUtcTicks == expected.RequestedAtUtcTicks &&
           completed.CompletedAtUtcTicks >= expected.RequestedAtUtcTicks && completed.CompletedAtUtcTicks <= now;

    public static bool CanAdoptExternalStop(ConstructedStopRequest request, int pid, long started,
        string mode, long taskStarted, long now, bool receipt)
        => Matches(request, pid, started, mode, now) && taskStarted > 0 &&
           request.RequestedAtUtcTicks >= taskStarted &&
           (!receipt || (request.CompletedAtUtcTicks >= request.RequestedAtUtcTicks && request.CompletedAtUtcTicks <= now));
}
