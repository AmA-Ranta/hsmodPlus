using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArenaBoxBot;

/// <summary>Projects Arena's read-only view without rewriting HsMod's cached snapshot.</summary>
internal sealed class ArenaStatusProjection
{
    private readonly object _gate = new object();
    private string _source;
    private string _result;
    private bool _arena;
    private bool _cached;
    private long _activityVersion;
    private bool _hasActivity;

    internal string Project(string source, bool arenaSelected, ArenaActivitySnapshot activity = null)
    {
        lock (_gate)
        {
            var hasActivity = activity != null;
            var version = activity?.Version ?? 0;
            if (_cached && ReferenceEquals(source, _source) && arenaSelected == _arena &&
                hasActivity == _hasActivity && version == _activityVersion) return _result;
            _source = source;
            _arena = arenaSelected;
            _hasActivity = hasActivity;
            _activityVersion = version;
            _result = arenaSelected ? ProjectArena(source, activity) : source;
            _cached = true;
            return _result;
        }
    }

    private static string ProjectArena(string source, ArenaActivitySnapshot activity)
    {
        if (string.IsNullOrEmpty(source)) return source;
        try
        {
            var lineStarts = new List<int> { 0 };
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '\r')
                {
                    if (i + 1 < source.Length && source[i + 1] == '\n') i++;
                    lineStarts.Add(i + 1);
                }
                else if (source[i] == '\n') lineStarts.Add(i + 1);
            }
            var modeStart = -1;
            var modeEnd = -1;
            var modeCount = 0;
            var activityCount = 0;
            var activityKeys = new HashSet<string>(StringComparer.Ordinal);
            var replacements = new List<Replacement>();
            var objectComplete = false;
            using (var text = new StringReader(source))
            using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
            {
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject) return source;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonToken.EndObject && reader.Depth == 0) objectComplete = true;
                    if (reader.TokenType != JsonToken.PropertyName) continue;
                    var key = (string)reader.Value;
                    if (reader.Depth == 1 && key == "activity") activityCount++;
                    var topMode = reader.Depth == 1 && key == "mode";
                    var activityField = activity != null && reader.Depth == 2 &&
                        reader.Path == "activity." + key && activity.TryGetValue(key, out _);
                    if (!topMode && !activityField) continue;
                    if (topMode) modeCount++;
                    else if (!activityKeys.Add(key)) return source;
                    // JsonTextReader has already consumed the property's
                    // colon; its position is the beginning of the value gap.
                    var start = lineStarts[reader.LineNumber - 1] + reader.LinePosition;
                    while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
                    if (!reader.Read() || start >= source.Length) return source;
                    if (topMode)
                    {
                        if (reader.TokenType != JsonToken.String || source[start] != '"') return source;
                        modeStart = start;
                        modeEnd = lineStarts[reader.LineNumber - 1] + reader.LinePosition;
                    }
                    else
                    {
                        // Replace only existing, known activity fields. Skip
                        // their entire value so nested unknown JSON cannot
                        // accidentally become an editable sibling field.
                        var token = reader.TokenType;
                        var validType = key == "games" || key == "wins" || key == "losses"
                            ? token == JsonToken.Integer || token == JsonToken.Null
                            : key == "statusArgs" || key == "actionArgs"
                                ? token == JsonToken.StartArray || token == JsonToken.Null
                                : token == JsonToken.String || token == JsonToken.Null;
                        if (!validType) return source;
                        reader.Skip();
                        var end = lineStarts[reader.LineNumber - 1] + reader.LinePosition;
                        if (end <= start || end > source.Length) return source;
                        activity.TryGetValue(key, out var value);
                        replacements.Add(new Replacement(start, end, JsonConvert.SerializeObject(value)));
                    }
                }
            }
            // Parse the entire document before changing anything. Ambiguous,
            // partial and startup snapshots are returned exactly as received.
            if (!objectComplete || modeCount != 1 || activityCount > 1 || modeStart < 0 || modeEnd <= modeStart || modeEnd > source.Length) return source;
            replacements.Add(new Replacement(modeStart, modeEnd, "\"Arena\""));
            // Work backwards over exact source spans: flags, timestamps,
            // deck, unknown fields, whitespace and large numbers retain
            // their original bytes, including inside activity.
            replacements.Sort((a, b) => b.Start.CompareTo(a.Start));
            var result = source;
            foreach (var replacement in replacements)
                result = result.Substring(0, replacement.Start) + replacement.Value + result.Substring(replacement.End);
            return result;
        }
        catch { return source; }
    }

    private sealed class Replacement
    {
        internal readonly int Start, End;
        internal readonly string Value;
        internal Replacement(int start, int end, string value) { Start = start; End = end; Value = value; }
    }
}

/// <summary>A value copy published on Unity's main thread; safe for HTTP readers.</summary>
internal sealed class ArenaActivitySnapshot
{
    internal readonly long Version;
    internal readonly string Status, Action;
    internal readonly int? Games, Wins, Losses;

    internal ArenaActivitySnapshot(long version, string status, string action, int? wins, int? losses)
    {
        Version = version; Status = status ?? ""; Action = action ?? "尚无已确认动作";
        Wins = wins; Losses = losses;
        var games = (long)(wins ?? 0) + (losses ?? 0);
        Games = wins.HasValue && losses.HasValue && games <= int.MaxValue && games >= 0 ? (int?)games : null;
    }

    internal bool TryGetValue(string key, out object value)
    {
        switch (key)
        {
            case "status": value = Status; return true;
            case "action": value = Action; return true;
            // Custom text must not retain a previous ladder translation key.
            case "statusKey": case "actionKey": value = null; return true;
            case "statusArgs": case "actionArgs": value = Array.Empty<object>(); return true;
            case "games": value = Games; return true;
            case "wins": value = Wins; return true;
            case "losses": value = Losses; return true;
            default: value = null; return false;
        }
    }
}

/// <summary>
/// Accepts only native server observations. Local submission and Box advice
/// have no entry point, so neither can become an executed-action label.
/// </summary>
internal sealed class ArenaActivityEvidence
{
    private string _context = "";
    private bool _enabled;
    private bool? _friendlyCurrentPlayer;
    internal string Action { get; private set; } = "尚无已确认动作";

    internal void SetContext(string context, bool enabled)
    {
        context = context ?? "";
        if (_context == context && _enabled == enabled) return;
        _context = context; _enabled = enabled;
        _friendlyCurrentPlayer = null;
        Action = "尚无已确认动作";
    }

    private bool Matches(string context) => _enabled && !string.IsNullOrEmpty(_context) && _context == context;

    internal static bool CanReadPowerOwners(bool processorAvailable, bool pendingHistory, bool identityChanges)
        => processorAvailable && !pendingHistory && !identityChanges;

    // Native tag values: CONTROLLER, FAKE_CONTROLLER, CARDTYPE. These
    // identity changes invalidate ownership read from the pre-packet map.
    internal static bool IsOwnershipOrIdentityTag(int tag) => tag == 50 || tag == 2032 || tag == 202;

    internal void ObservePower(string context, string blockType, bool friendly, bool heroPower,
        string source, string target)
    {
        if (!Matches(context) || !friendly || string.IsNullOrEmpty(source)) return;
        var suffix = string.IsNullOrEmpty(target) ? "" : " → " + target;
        if (blockType == "PLAY") Action = "服务器已确认出牌：" + source + suffix;
        else if (blockType == "ATTACK") Action = "服务器已确认攻击：" + source + suffix;
        else if (blockType == "POWER" && heroPower) Action = "服务器已确认英雄技能：" + source + suffix;
    }

    internal void ObserveChoice(string context, int expectedId, int receivedId, bool friendly,
        bool entitiesMatch, bool mulligan, string label)
    {
        if (!Matches(context) || expectedId < 0 || expectedId != receivedId || !friendly || !entitiesMatch) return;
        Action = mulligan ? "服务器已确认起手留牌" : "服务器已确认选择：" + (label ?? "卡牌");
    }

    internal void ObserveCurrentPlayer(string context, int value)
    {
        if (!Matches(context) || (value != 0 && value != 1)) return;
        if (_friendlyCurrentPlayer == true && value == 0) Action = "回合已结束";
        _friendlyCurrentPlayer = value == 1;
    }
}

internal static class ArenaModeMetadata
{
    internal const string ShortcutKey = "keyWildLadderModeToggle";
    internal const string ShortcutDescription = "循环切换脚本模式：标准→狂野→休闲→酒馆战棋→竞技场（默认 F11）";

    internal static void UpdateShortcutDescription(JObject groups)
    {
        if (groups == null) return;
        foreach (var group in groups.Properties())
        {
            if (!(group.Value is JArray items)) continue;
            foreach (var item in items)
                if (item is JObject entry && string.Equals((string)entry["key"], ShortcutKey, StringComparison.Ordinal))
                    entry["description"] = ShortcutDescription;
        }
    }
}
