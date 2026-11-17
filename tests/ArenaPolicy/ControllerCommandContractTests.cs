using System;
using ArenaControl.Contracts;

internal static class ControllerCommandContractTests
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string why) { checks++; if (!value) throw new InvalidOperationException(why); }
        ControllerCommand New(string action, string boundary) => new ControllerCommand
        {
            RequestId = Guid.NewGuid().ToString("N"), Action = action, Boundary = boundary,
            GameProcessId = 4321, GameStartedAtUtcTicks = 1000, IssuedAtUtcTicks = 2000
        };

        foreach (var action in new[] { ControllerCommandContract.ActionStop, ControllerCommandContract.ActionReturnHub })
        foreach (var boundary in new[] { ControllerCommandContract.BoundaryImmediate, ControllerCommandContract.BoundaryAfterMatch })
        {
            var command = New(action, boundary);
            Check(ControllerCommandContract.Matches(command, 4321, 1000, 3000), "Current client command accepted: " + action + "/" + boundary);
            Check(!ControllerCommandContract.Matches(command, 4321, 1001, 3000), "Command for another client start rejected");
            Check(!ControllerCommandContract.Matches(command, 9999, 1000, 3000), "Command for another process rejected");
            Check(!ControllerCommandContract.Matches(command, 4321, 1000, 1999), "Future command rejected");
            command.IssuedAtUtcTicks = 999;
            Check(!ControllerCommandContract.Matches(command, 4321, 1000, 3000), "Command issued before the client started rejected");
        }

        var bad = New("stop", "afterMatch");
        bad.RequestId = "";
        Check(!ControllerCommandContract.Matches(bad, 4321, 1000, 3000), "Missing request id rejected");
        bad.RequestId = Guid.NewGuid().ToString("N"); bad.Action = "start";
        Check(!ControllerCommandContract.Matches(bad, 4321, 1000, 3000), "Unknown action rejected");
        bad.Action = "stop"; bad.Boundary = "whenever";
        Check(!ControllerCommandContract.Matches(bad, 4321, 1000, 3000), "Unknown boundary rejected");
        bad.Boundary = "afterMatch"; bad.SchemaVersion = 2;
        Check(!ControllerCommandContract.Matches(bad, 4321, 1000, 3000), "Other schema version rejected");

        var configure = New(ControllerCommandContract.ActionConfigure, ControllerCommandContract.BoundaryImmediate);
        Check(!ControllerCommandContract.Matches(configure, 4321, 1000, 3000), "Configure without a mode is rejected");
        configure.Mode = "Standard"; configure.GameLimit = 3;
        Check(ControllerCommandContract.Matches(configure, 4321, 1000, 3000), "Configure command with a mode is accepted");

        var expected = New("stop", "afterMatch");
        var receipt = new ControllerCommand
        {
            RequestId = expected.RequestId, Action = expected.Action, Boundary = expected.Boundary,
            GameProcessId = 4321, GameStartedAtUtcTicks = 1000, IssuedAtUtcTicks = 2000, CompletedAtUtcTicks = 2500
        };
        Check(ControllerCommandContract.Confirms(expected, receipt, 4321, 1000, 3000), "Exact receipt confirms the command");
        var other = new ControllerCommand
        {
            RequestId = Guid.NewGuid().ToString("N"), Action = "stop", Boundary = "afterMatch",
            GameProcessId = 4321, GameStartedAtUtcTicks = 1000, IssuedAtUtcTicks = 2000, CompletedAtUtcTicks = 2500
        };
        Check(!ControllerCommandContract.Confirms(expected, other, 4321, 1000, 3000), "Receipt for another request rejected");
        receipt.Boundary = "immediate";
        Check(!ControllerCommandContract.Confirms(expected, receipt, 4321, 1000, 3000), "Receipt with another boundary rejected");
        receipt.Boundary = "afterMatch"; receipt.CompletedAtUtcTicks = 2000;
        Check(!ControllerCommandContract.Confirms(expected, receipt, 4321, 1000, 3000), "Completion must be strictly after the request");
        receipt.CompletedAtUtcTicks = 3001;
        Check(!ControllerCommandContract.Confirms(expected, receipt, 4321, 1000, 3000), "Future completion rejected");
        Console.WriteLine("PASS: controller command contract (" + checks + " assertions)");
    }
}