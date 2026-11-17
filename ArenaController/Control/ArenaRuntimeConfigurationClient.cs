using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ArenaController;

/// <summary>Persisted sidecar edits do not reload existing ConfigEntry values.</summary>
public static class ArenaRuntimeConfigurationClient
{
    public static async Task<bool> ApplyAndVerifyAsync(HttpClient client, string root, string token,
        string arenaMode, int maxGames, bool ticketStop, bool goldStop, bool allowChanges)
    {
        if (arenaMode is not ("普通竞技场" or "地下竞技场") || maxGames < 0) return false;
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["arenaMode"] = arenaMode,
            ["arenaMaxGamesPerRun"] = maxGames.ToString(CultureInfo.InvariantCulture),
            ["arenaStopOnTicketShortage"] = ticketStop ? "true" : "false",
            ["arenaStopOnGoldShortage"] = goldStop ? "true" : "false"
        };
        var before = await ReadAsync(client, root, token, wanted.Keys).ConfigureAwait(false);
        if (before == null) return false;
        if (Matches(before, wanted)) return true; // Reattach/recovery needs no mutation.
        if (!allowChanges || !await IsOffAsync(client, root, token).ConfigureAwait(false)) return false;
        foreach (var pair in wanted)
        {
            if (Same(before[pair.Key], pair.Value)) continue;
            // A concurrent manual start must not permit subtype changes during play.
            if (!await IsOffAsync(client, root, token).ConfigureAwait(false)) return false;
            using var post = Request(HttpMethod.Post, root + "/config", token);
            post.Content = new StringContent(JsonSerializer.Serialize(new { key = pair.Key + ".name", value = pair.Value }),
                Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(post).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
        }
        var after = await ReadAsync(client, root, token, wanted.Keys).ConfigureAwait(false);
        return after != null && Matches(after, wanted) && await IsOffAsync(client, root, token).ConfigureAwait(false);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Matches(Dictionary<string, string> actual, Dictionary<string, string> wanted)
        => wanted.All(p => actual.TryGetValue(p.Key, out var value) && Same(value, p.Value));
    private static HttpRequestMessage Request(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-HsMod-Token", token);
        return request;
    }

    private static async Task<Dictionary<string, string>?> ReadAsync(HttpClient client, string root, string token,
        IEnumerable<string> expectedKeys)
    {
        using var get = Request(HttpMethod.Get, root + "/api/config?lang=zhCN", token);
        using var response = await client.SendAsync(get).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Object) return null;
        var keys = new HashSet<string>(expectedKeys, StringComparer.Ordinal) { "wildLadderPlayMode" };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in group.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                    !keys.Contains(key.GetString()!)) continue;
                if (!item.TryGetProperty("value", out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array ||
                    (key.GetString() != "wildLadderPlayMode" && (!item.TryGetProperty("canWrite", out var writable) || writable.ValueKind != JsonValueKind.True)) ||
                    !values.TryAdd(key.GetString()!, value.ToString())) return null;
            }
        }
        if (keys.Any(k => !values.ContainsKey(k)) || !Same(values["wildLadderPlayMode"], "Arena")) return null;
        return values;
    }

    private static async Task<bool> IsOffAsync(HttpClient client, string root, string token)
    {
        using var get = Request(HttpMethod.Get, root + "/api/status", token);
        using var response = await client.SendAsync(get).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var s = doc.RootElement;
        return s.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True &&
            s.TryGetProperty("loggedIn", out var login) && login.ValueKind == JsonValueKind.True &&
            s.TryGetProperty("scriptEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.False &&
            s.TryGetProperty("mode", out var mode) && mode.GetString() == "Arena" &&
            s.TryGetProperty("updatedAt", out var time) && DateTimeOffset.TryParse(time.GetString(), out var updated) &&
            (DateTimeOffset.UtcNow - updated).TotalSeconds is >= -1 and <= 5;
    }
}
