using System.Text.Json;

namespace ArenaController;

/// <summary>The game process identity sampled around an HTTP read.  A status
/// response only describes the client that was running while it was produced.</summary>
internal readonly struct GameIdentitySample
{
    public GameIdentitySample(bool known, bool running, int processId, DateTime startedAtUtc)
    {
        Known = known;
        Running = running;
        ProcessId = processId;
        StartedAtUtc = startedAtUtc;
    }

    public bool Known { get; }
    public bool Running { get; }
    public int ProcessId { get; }
    public DateTime StartedAtUtc { get; }
}

/// <summary>Point-in-time HsMod runtime state, parsed from /api/status.</summary>
internal sealed class HsModStatusSnapshot
{
    public bool Ready { get; init; }
    public bool LoggedIn { get; init; }
    public int GameProcessId { get; init; }
    public DateTime GameStartedAtUtc { get; init; }
    public bool PluginEnabled { get; init; }
    public bool ScriptEnabled { get; init; }
    public string Mode { get; init; } = "";
    public string Source { get; init; } = "";
    public string Activity { get; init; } = "";
    public DateTime? UpdatedAt { get; init; }
}

internal sealed class HsModDeckOption
{
    public string Value { get; init; } = "";
    public string Label { get; init; } = "";
}

internal sealed class HsModDeckSnapshot
{
    public string NativeMode { get; init; } = "";
    public string CurrentValue { get; init; } = "";
    public bool Pending { get; init; }
    public List<HsModDeckOption> Options { get; init; } = new();
}

internal static class HsModApiParser
{
    /// <summary>Extracts the token from the HsMod web page; attribute order varies.</summary>
    internal static string? ExtractToken(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(html,
            "name=[\"']hsmod-token[\"'][^>]*content=[\"']([^\"']+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            match = System.Text.RegularExpressions.Regex.Match(html,
                "content=[\"']([^\"']+)[\"'][^>]*name=[\"']hsmod-token[\"']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses /api/status.  Returns null when the payload cannot be a
    /// confirmed native state (missing scriptEnabled).</summary>
    internal static HsModStatusSnapshot? ParseStatus(string json, bool sameGame, int processId, DateTime startedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        var node = document.RootElement;
        if (!node.TryGetProperty("scriptEnabled", out var scriptValue) ||
            (scriptValue.ValueKind != JsonValueKind.True && scriptValue.ValueKind != JsonValueKind.False))
            return null;
        var updated = node.TryGetProperty("updatedAt", out var updatedValue) &&
                      DateTime.TryParse(updatedValue.GetString(), out var parsed)
            ? parsed
            : (DateTime?)null;
        return new HsModStatusSnapshot
        {
            Ready = node.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True,
            LoggedIn = node.TryGetProperty("loggedIn", out var loggedIn) && loggedIn.ValueKind == JsonValueKind.True,
            GameProcessId = sameGame ? processId : 0,
            GameStartedAtUtc = sameGame ? startedAtUtc : DateTime.MinValue,
            PluginEnabled = node.TryGetProperty("pluginEnabled", out var plugin) && plugin.ValueKind == JsonValueKind.True,
            ScriptEnabled = scriptValue.ValueKind == JsonValueKind.True,
            Mode = node.TryGetProperty("mode", out var mode) ? mode.ToString() : "",
            Source = node.TryGetProperty("source", out var source) ? source.ToString() : "",
            Activity = node.TryGetProperty("activity", out var activity) ? activity.ToString() : "",
            UpdatedAt = updated
        };
    }

    /// <summary>Parses /api/config for the wild-ladder mode and deck options.</summary>
    internal static HsModDeckSnapshot? ParseDeckSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Object)
            return null;
        var nativeMode = "";
        foreach (var modeGroup in groups.EnumerateObject())
        {
            if (modeGroup.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in modeGroup.Value.EnumerateArray())
                if (item.TryGetProperty("key", out var modeKey) && modeKey.GetString() == "wildLadderPlayMode" &&
                    item.TryGetProperty("value", out var modeValue))
                    nativeMode = modeValue.ToString();
        }
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in group.Value.EnumerateArray())
            {
                if (!item.TryGetProperty("key", out var key) ||
                    !string.Equals(key.GetString(), "wildLadderDeckName", StringComparison.Ordinal)) continue;
                var current = item.TryGetProperty("value", out var value) ? value.GetString() ?? "" : "";
                var pending = item.TryGetProperty("deckPending", out var pendingValue) &&
                              pendingValue.ValueKind == JsonValueKind.True;
                var options = new List<HsModDeckOption>();
                if (item.TryGetProperty("deckOptions", out var deckOptions) && deckOptions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in deckOptions.EnumerateArray())
                    {
                        var optionValue = option.TryGetProperty("value", out var optionValueNode)
                            ? optionValueNode.GetString() ?? "" : "";
                        var optionLabel = option.TryGetProperty("label", out var optionLabelNode)
                            ? optionLabelNode.GetString() ?? "" : "";
                        if (optionValue.Length != 0 && optionLabel.Length != 0)
                            options.Add(new HsModDeckOption { Value = optionValue, Label = optionLabel });
                    }
                }
                return new HsModDeckSnapshot
                {
                    NativeMode = nativeMode,
                    CurrentValue = current,
                    Pending = pending,
                    Options = options
                };
            }
        }
        return null;
    }
}
