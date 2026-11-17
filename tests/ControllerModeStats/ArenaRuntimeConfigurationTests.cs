using System.Net;
using System.Text;
using System.Text.Json;
using ArenaController;

internal static class ArenaRuntimeConfigurationTests
{
    internal static async Task Run()
    {
        foreach (var destination in new[] { "普通竞技场", "地下竞技场" })
        {
            using var handler = new Handler();
            handler.Values["arenaMode"] = destination == "普通竞技场" ? "地下竞技场" : "普通竞技场";
            using var client = new HttpClient(handler);
            if (!await ArenaRuntimeConfigurationClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token",
                destination, 1, true, true, true) || handler.Values["arenaMode"] != destination || handler.Posts != 4)
                throw new InvalidOperationException("A persisted subtype change must update all controller-owned live settings before ON");
        }
        foreach (var failure in new[] { "ignored-write", "failed-write", "running", "start-after-post", "stale-status", "unsafe-boundary", "wrong-mode", "missing-key", "duplicate-key", "readonly" })
        {
            using var handler = new Handler { Failure = failure };
            using var client = new HttpClient(handler);
            var enabled = false;
            try
            {
                await ScriptActivationGate.EnableAsync(async () =>
                {
                    if (!await ArenaRuntimeConfigurationClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token",
                        "普通竞技场", 1, true, true, failure != "unsafe-boundary"))
                        throw new InvalidOperationException("Runtime read-back failed");
                }, () => { }, () => { enabled = true; return true; });
            }
            catch (InvalidOperationException) { }
            if (enabled) throw new InvalidOperationException("Failed runtime configuration must block activation: " + failure);
            if (failure is not ("ignored-write" or "failed-write" or "start-after-post") && handler.Posts != 0)
                throw new InvalidOperationException("Unknown, active or wrong-mode client must not receive subtype writes: " + failure);
        }
        using (var handler = new Handler { Failure = "running" })
        using (var client = new HttpClient(handler))
        {
            if (!await ArenaRuntimeConfigurationClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token",
                "地下竞技场", 0, false, false, false) || handler.Posts != 0)
                throw new InvalidOperationException("Identical current settings permit reattachment without changing an active run");
        }
        Console.WriteLine("PASS: Arena stale ConfigEntry subtype reproduction, bidirectional live synchronization, full read-back and failed-activation isolation");
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal string Failure = "";
        internal int Posts;
        internal readonly Dictionary<string, string> Values = new()
        {
            ["wildLadderPlayMode"] = "Arena", ["arenaMode"] = "地下竞技场", ["arenaMaxGamesPerRun"] = "0",
            ["arenaStopOnTicketShortage"] = "false", ["arenaStopOnGoldShortage"] = "false"
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.Headers.TryGetValues("X-HsMod-Token", out var token) || token.Single() != "test-token")
                throw new InvalidOperationException("Every original API operation needs the current page token");
            if (request.Method == HttpMethod.Post)
            {
                if (request.RequestUri!.AbsolutePath != "/config") throw new InvalidOperationException("No script/input endpoints allowed");
                Posts++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var key = body.RootElement.GetProperty("key").GetString()!;
                if (!key.EndsWith(".name") || !Values.ContainsKey(key[..^5]) || key == "wildLadderPlayMode.name")
                    throw new InvalidOperationException("Only explicit Arena controller settings can change");
                if (Failure is not ("ignored-write" or "failed-write")) Values[key[..^5]] = body.RootElement.GetProperty("value").GetString()!;
                return new HttpResponseMessage(Failure == "failed-write" ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            }
            object content;
            if (request.RequestUri!.AbsolutePath == "/api/status")
                content = new { ready = true, loggedIn = true, scriptEnabled = Failure == "running" || (Failure == "start-after-post" && Posts > 0), mode = "Arena",
                    updatedAt = DateTimeOffset.UtcNow.AddSeconds(Failure == "stale-status" ? -10 : 0) };
            else
            {
                var items = Values.Where(p => Failure != "missing-key" || p.Key != "arenaMode")
                    .Select(p => new { key = p.Key, value = p.Key == "wildLadderPlayMode" && Failure == "wrong-mode" ? "Standard" : p.Value, canWrite = Failure != "readonly" }).ToList();
                if (Failure == "duplicate-key") items.Add(new { key = "arenaMode", value = "普通竞技场", canWrite = true });
                content = new { groups = new Dictionary<string, object> { ["竞技场脚本"] = items } };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json") };
        }
    }
}
