using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ArenaController;

public static class HsModRuntimeLimitClient
{
    public static async Task<bool> ApplyAndVerifyAsync(HttpClient client, string root, string token,
        string expectedMode, int remaining)
    {
        if (remaining < 0) return false;
        var before = await ReadAsync(client, root, token).ConfigureAwait(false);
        if (!string.Equals(before.Mode, expectedMode, StringComparison.OrdinalIgnoreCase)) return false;
        using var post = new HttpRequestMessage(HttpMethod.Post, root + "/config");
        post.Headers.Add("X-HsMod-Token", token);
        post.Content = new StringContent(JsonSerializer.Serialize(new
        { key = "wildLadderStopAfterGames.name", value = remaining.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(post).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        var after = await ReadAsync(client, root, token).ConfigureAwait(false);
        return string.Equals(after.Mode, expectedMode, StringComparison.OrdinalIgnoreCase) && after.Limit == remaining;
    }

    private static async Task<(string? Mode, int? Limit)> ReadAsync(HttpClient client, string root, string token)
    {
        using var read = new HttpRequestMessage(HttpMethod.Get, root + "/api/config?lang=zhCN");
        read.Headers.Add("X-HsMod-Token", token);
        using var response = await client.SendAsync(read).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return (null, null);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        string? mode = null; int? limit = null;
        if (!doc.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Object) return (null, null);
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in group.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("key", out var key) || !item.TryGetProperty("value", out var value)) continue;
                if (key.GetString() == "wildLadderPlayMode") mode = value.ToString();
                if (key.GetString() == "wildLadderStopAfterGames" && int.TryParse(value.ToString(), out var number)) limit = number;
            }
        }
        return (mode, limit);
    }
}
