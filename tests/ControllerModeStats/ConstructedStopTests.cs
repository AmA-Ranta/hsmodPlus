using ArenaControl.Contracts;
using System.Text.Json;

internal static class ConstructedStopTests
{
    internal static void Run()
    {
        var start = DateTime.UtcNow.AddHours(-1).Ticks;
        var now = DateTime.UtcNow.Ticks;
        var request = new ConstructedStopRequest
        {
            RequestId = Guid.NewGuid().ToString("N"), GameProcessId = 123,
            GameStartedAtUtcTicks = start, RequestedAtUtcTicks = now - TimeSpan.FromMinutes(5).Ticks, Mode = "酒馆战棋"
        };
        ConstructedStopRequest Copy() => JsonSerializer.Deserialize<ConstructedStopRequest>(
            JsonSerializer.Serialize(request, new JsonSerializerOptions { IncludeFields = true }),
            new JsonSerializerOptions { IncludeFields = true })!;
        Require(ConstructedStopContract.Matches(Copy(), 123, start, "酒馆战棋", now), "Durable request retains its exact game identity");
        Require(!ConstructedStopContract.Matches(request, 124, start, "酒馆战棋", now), "Another process cannot consume request");
        Require(!ConstructedStopContract.Matches(request, 123, start + 1, "酒馆战棋", now), "Reused PID cannot consume old request");
        Require(!ConstructedStopContract.Matches(request, 123, start, "标准", now), "Manual mode change cannot inherit old stop");
        Require(!ConstructedStopContract.Matches(request, 123, start, "竞技场", now), "Arena keeps its reward-preserving protocol");
        Require(!ConstructedStopContract.Matches(request, 123, start, "酒馆战棋", request.RequestedAtUtcTicks - 1), "Future request is unknown");
        var malformed = Copy(); malformed.RequestId = "";
        Require(!ConstructedStopContract.Matches(malformed, 123, start, "酒馆战棋", now), "Missing request identity cannot stop script");
        var complete = Copy(); complete.CompletedAtUtcTicks = now;
        var taskStarted = request.RequestedAtUtcTicks - TimeSpan.FromMinutes(10).Ticks;
        Require(ConstructedStopContract.CanAdoptExternalStop(request, 123, start, "酒馆战棋", taskStarted, now, false), "External pending request is adopted while current game continues");
        Require(ConstructedStopContract.CanAdoptExternalStop(complete, 123, start, "酒馆战棋", taskStarted, now, true), "Completed current-task receipt stops recovery even after normal process exit");
        Require(!ConstructedStopContract.CanAdoptExternalStop(request, 123, start, "酒馆战棋", taskStarted, now, true), "Pending request alone cannot prove an exited game completed stop");
        Require(!ConstructedStopContract.CanAdoptExternalStop(complete, 123, start, "酒馆战棋", now, now, true), "Previous task receipt cannot stop a new task");
        Require(!ConstructedStopContract.CanAdoptExternalStop(complete, 124, start, "酒馆战棋", taskStarted, now, true), "Replaced process cannot inherit external receipt");
        Require(!ConstructedStopContract.CanAdoptExternalStop(complete, 123, start + 1, "酒馆战棋", taskStarted, now, true), "Same PID with new creation time cannot inherit receipt");
        Require(ConstructedStopContract.Confirms(request, complete, 123, start, "酒馆战棋", now), "Exact stop receipt confirms this request");
        Require(!ConstructedStopContract.CanCompleteStop(true, true, false, false), "Settlement plus no gameplay log does not prove next queue is idle");
        Require(!ConstructedStopContract.CanCompleteStop(true, false, false, false), "Unknown client cannot complete without receipt");
        Require(!ConstructedStopContract.CanCompleteStop(true, true, true, true), "New gameplay overrides a previously completed stop receipt");
        Require(ConstructedStopContract.CanCompleteStop(true, false, false, true), "Native exact receipt can confirm stop even if gameplay log is unavailable");
        Require(ConstructedStopContract.CanCompleteStop(false, true, true, false), "Exited client does not require an API stop receipt");
        complete.RequestId = Guid.NewGuid().ToString("N");
        Require(!ConstructedStopContract.Confirms(request, complete, 123, start, "酒馆战棋", now), "Older task receipt cannot finish new task");
        complete = Copy(); complete.CompletedAtUtcTicks = request.RequestedAtUtcTicks - 1;
        Require(!ConstructedStopContract.Confirms(request, complete, 123, start, "酒馆战棋", now), "Completion must follow request");
        foreach (var scene in new[] { "GAMEPLAY", "DRAFT", "UNKNOWN", "INITIALIZING" })
            Require(!ConstructedStopContract.IsSafeBoundary(scene, "INVALID", false, false, false), "Unsafe scene holds current game/rewards: " + scene);
        foreach (var scene in new[] { "BACON", "HUB", "TOURNAMENT" })
        {
            Require(ConstructedStopContract.IsSafeBoundary(scene, "INVALID", false, false, false), "Settled lobby is safe: " + scene);
            Require(!ConstructedStopContract.IsSafeBoundary(scene, "GAMEPLAY", false, false, false), "Incoming game must not be disabled");
            Require(!ConstructedStopContract.IsSafeBoundary(scene, "INVALID", true, false, false), "Transition must settle");
            Require(!ConstructedStopContract.IsSafeBoundary(scene, "INVALID", false, true, false), "An existing queue is not idle");
            Require(!ConstructedStopContract.IsSafeBoundary(scene, "INVALID", false, false, true), "Connected game keeps protection");
        }
        Require(ConstructedStopContract.RemainingGames(10, 100, 100, true) == 10, "New task gets its requested budget");
        Require(ConstructedStopContract.RemainingGames(10, 100, 108, true) == 2, "Recovery gets remaining games, not a new full budget");
        Require(ConstructedStopContract.RemainingGames(10, 100, 110, true) == 0, "Completed budget cannot resume");
        Require(ConstructedStopContract.RemainingGames(0, null, 0, false) == 0, "Unlimited task does not require counter evidence");
        RejectBudget(10, null, 5, true); RejectBudget(10, 5, 4, true); RejectBudget(10, 5, 8, false);
        Console.WriteLine("PASS: process-scoped constructed stop receipts, safe idle boundaries and remaining game budget");
    }

    private static void RejectBudget(int limit, int? baseline, int current, bool reliable)
    {
        try { ConstructedStopContract.RemainingGames(limit, baseline, current, reliable); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Unknown/reversed counter must block native activation");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
