using System.Net;
using System.Text;
using System.Text.Json;
using ArenaController;

internal static class RuntimeLimitTests
{
    internal static async Task Run()
    {
        foreach (var mode in new[] { "Standard", "Wild", "Casual", "Battlegrounds" })
        {
            using var handler = new NativeConfigHandler { Mode = mode };
            using var client = new HttpClient(handler);
            if (!await HsModRuntimeLimitClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token", mode, 2) ||
                handler.LiveLimit != 2 || handler.Posts != 1 || handler.Reads != 2)
                throw new InvalidOperationException("Native limit must be applied and re-read in " + mode);
        }
        using (var handler = new NativeConfigHandler { Mode = "Arena", LiveLimit = 2 })
        using (var client = new HttpClient(handler))
        {
            if (!await HsModRuntimeLimitClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token", HsModModeMapping.ToRuntime("竞技场"), 0) ||
                handler.LiveLimit != 0 || handler.Posts != 1 || handler.Reads != 2)
                throw new InvalidOperationException("Arena must clear the preceding Casual quota and verify its projected live mode");
        }
        using (var handler = new NativeConfigHandler { Mode = "Arena", LiveLimit = 2, IgnorePost = true })
        using (var client = new HttpClient(handler))
        {
            if (await HsModRuntimeLimitClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token", "Arena", 0))
                throw new InvalidOperationException("Arena cannot start when old constructed quota failed to clear");
        }
        using (var handler = new NativeConfigHandler { Mode = "Battlegrounds", IgnorePost = true })
        using (var client = new HttpClient(handler))
        {
            var enabled = false;
            try
            {
                await ScriptActivationGate.EnableAsync(async () =>
                {
                    if (!await HsModRuntimeLimitClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token", "Battlegrounds", 1))
                        throw new InvalidOperationException("Native read-back mismatch");
                }, () => { }, () => { enabled = true; return true; });
            }
            catch (InvalidOperationException) { }
            if (enabled) throw new InvalidOperationException("HTTP success with unchanged live limit must block activation");
        }
        using (var handler = new NativeConfigHandler { Mode = "Wild" })
        using (var client = new HttpClient(handler))
            if (await HsModRuntimeLimitClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token", "Standard", 1) || handler.Posts != 0)
                throw new InvalidOperationException("A different live mode must never receive this task's configuration");
        using (var handler = new NativeConfigHandler { Mode = "Standard", PostFails = true })
        using (var client = new HttpClient(handler))
            if (await HsModRuntimeLimitClient.ApplyAndVerifyAsync(client, "http://test.invalid", "test-token", "Standard", 1))
                throw new InvalidOperationException("Failed native configuration must block activation");
        Console.WriteLine("PASS: native live limit apply/read-back, unchanged-value activation gate and mode isolation");
    }

    private sealed class NativeConfigHandler : HttpMessageHandler
    {
        internal string Mode = "Standard";
        internal int LiveLimit, Posts, Reads;
        internal bool IgnorePost, PostFails;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.Headers.TryGetValues("X-HsMod-Token", out var values) || values.Single() != "test-token")
                throw new InvalidOperationException("Native page token must accompany every operation");
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                if (request.RequestUri!.AbsolutePath != "/config") throw new InvalidOperationException("Wrong native endpoint");
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (json.RootElement.GetProperty("key").GetString() != "wildLadderStopAfterGames.name") throw new InvalidOperationException("Only stop limit can change");
                if (!IgnorePost && !PostFails) LiveLimit = int.Parse(json.RootElement.GetProperty("value").GetString()!);
                return new HttpResponseMessage(PostFails ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            }
            Reads++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { groups = new Dictionary<string, object>
                { ["自动脚本"] = new[] { new { key = "wildLadderPlayMode", value = Mode },
                    new { key = "wildLadderStopAfterGames", value = LiveLimit.ToString() } } } }), Encoding.UTF8, "application/json")
            };
        }
    }
}
