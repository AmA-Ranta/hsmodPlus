using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ArenaControl.Contracts;
using Newtonsoft.Json;
using UnityEngine;

namespace ArenaBoxBot;

/// <summary>
/// The controller's only write path into the game is one atomic command file.
/// This adapter turns that command into the plugin's existing internal triggers,
/// so the controller never writes game-side markers or config itself.
/// </summary>
internal static class ControllerCommandRuntime
{
    private static readonly Tuple<int, long> s_identity = ReadIdentity();
    private static float s_nextPoll;
    private static string s_appliedRequestId = "";

    private static Tuple<int, long> ReadIdentity()
    {
        using (var game = Process.GetCurrentProcess())
            return Tuple.Create(game.Id, game.StartTime.ToUniversalTime().Ticks);
    }

    private static string CommandPath => ArenaRuntimePaths.File(ControllerCommandContract.CommandFile);
    private static string ReceiptPath => ArenaRuntimePaths.File(ControllerCommandContract.ReceiptFile);

    internal static void Apply(Action<string> log)
    {
        try
        {
            if (Time.unscaledTime < s_nextPoll) return;
            s_nextPoll = Time.unscaledTime + 0.5f;
            if (!File.Exists(CommandPath)) return;
            var command = JsonConvert.DeserializeObject<ControllerCommand>(File.ReadAllText(CommandPath));
            if (!ControllerCommandContract.Matches(command, s_identity.Item1, s_identity.Item2, DateTime.UtcNow.Ticks)) return;
            if (command.RequestId == s_appliedRequestId) return;
            if (command.Action == ControllerCommandContract.ActionConfigure)
            {
                // The plugin owns runtime configuration now; applying it does
                // local HTTP work, so it runs off the main thread and writes the
                // receipt itself.
                s_appliedRequestId = command.RequestId;
                ApplyConfigure(command, log);
                return;
            }
            if (!ApplyAction(command, log)) return;
            s_appliedRequestId = command.RequestId;
            WriteReceipt(command);
            log("controller command applied: action=" + command.Action + ";boundary=" + command.Boundary +
                ";requestId=" + command.RequestId);
        }
        catch (Exception ex) { log("controller command failed: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static bool ApplyAction(ControllerCommand command, Action<string> log)
    {
        switch (command.Action)
        {
            case ControllerCommandContract.ActionStop:
                // Both boundaries keep the plugin's own safe-boundary rule: a live
                // match is never interrupted, the script stops after it ends.
                var stopPath = ArenaRuntimePaths.File("controller-stop-after-game.marker");
                Directory.CreateDirectory(Path.GetDirectoryName(stopPath));
                File.WriteAllText(stopPath, command.Note ?? "controller stop", new UTF8Encoding(false));
                log("controller stop requested: boundary=" + command.Boundary);
                return true;
            case ControllerCommandContract.ActionReturnHub:
                var hubPath = ArenaRuntimePaths.File("controller-return-hub.marker");
                Directory.CreateDirectory(Path.GetDirectoryName(hubPath));
                File.WriteAllText(hubPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                log("controller return-hub requested");
                return true;
            default:
                return false;
        }
    }

    private static void ApplyConfigure(ControllerCommand command, Action<string> log)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using (var http = new System.Net.WebClient())
                {
                    http.Headers["X-HsMod-Token"] = "";
                    for (var port = 58744; port <= 58748; port++)
                    {
                        var root = "http://127.0.0.1:" + port;
                        var token = DiscoverToken(http, root);
                        if (string.IsNullOrEmpty(token)) continue;
                        http.Headers["X-HsMod-Token"] = token;
                        var errors = new List<string>();
                        if (!TrySetConfig(http, root, "wildLadderStopAfterGames.name",
                                command.GameLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), errors))
                            errors.Add("limit");
                        if (string.Equals(command.Mode, "Arena", StringComparison.OrdinalIgnoreCase))
                        {
                            TrySetConfig(http, root, "arenaMode.name", command.ArenaMode, errors);
                            TrySetConfig(http, root, "arenaMaxGamesPerRun.name",
                                command.ArenaMaxGamesPerRun.ToString(System.Globalization.CultureInfo.InvariantCulture), errors);
                            TrySetConfig(http, root, "arenaStopOnTicketShortage.name", command.ArenaStopOnTicket ? "true" : "false", errors);
                            TrySetConfig(http, root, "arenaStopOnGoldShortage.name", command.ArenaStopOnGold ? "true" : "false", errors);
                        }
                        command.Ok = errors.Count == 0;
                        command.Detail = command.Ok ? "applied" : "failed: " + string.Join(",", errors);
                        WriteReceipt(command);
                        log("controller configure applied: mode=" + command.Mode + ";limit=" +
                            command.GameLimit + ";ok=" + command.Ok + ";detail=" + command.Detail);
                        return;
                    }
                }
                command.Ok = false;
                command.Detail = "hsmod api unreachable";
                WriteReceipt(command);
                log("controller configure failed: " + command.Detail);
            }
            catch (Exception ex)
            {
                command.Ok = false;
                command.Detail = ex.GetType().Name + ": " + ex.Message;
                try { WriteReceipt(command); } catch { }
                log("controller configure failed: " + command.Detail);
            }
        });
    }

    private static string DiscoverToken(System.Net.WebClient http, string root)
    {
        try
        {
            var page = http.DownloadString(root + "/");
            var match = System.Text.RegularExpressions.Regex.Match(page,
                "name=\"hsmod-token\"[^>]*content=\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    private static bool TrySetConfig(System.Net.WebClient http, string root, string key, string value, List<string> errors)
    {
        try
        {
            var body = "{\"key\":\"" + key + "\",\"value\":\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
            http.Headers["Content-Type"] = "application/json";
            http.UploadString(root + "/config", "POST", body);
            return true;
        }
        catch (Exception ex)
        {
            errors.Add(key + ":" + ex.GetType().Name);
            return false;
        }
    }
    private static void WriteReceipt(ControllerCommand command)
    {
        command.CompletedAtUtcTicks = DateTime.UtcNow.Ticks;
        var pending = ReceiptPath + ".pending";
        File.WriteAllText(pending, JsonConvert.SerializeObject(command));
        if (File.Exists(ReceiptPath)) File.Delete(ReceiptPath);
        File.Move(pending, ReceiptPath);
    }
}