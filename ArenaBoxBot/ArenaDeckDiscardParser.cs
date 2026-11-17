using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ArenaBoxBot;

internal sealed class ArenaDeckDiscardRecommendation
{
    internal int BoxPid;
    internal DateTime LogTimeLocal;
    internal string ScriptHash = "";
    internal string Source = "";
    internal Dictionary<string, int> RemoveCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    internal int TotalCopies;
}

/// <summary>
/// Parses the Box's observed CaptureCallback diagnostic envelope without
/// executing JavaScript. Freshness, producer identity and native deck binding
/// belong to the reader: the actual upstream payload has no deck/request ID.
/// </summary>
internal static class ArenaDeckDiscardParser
{
    internal const int ScriptCaptureLimit = 262144;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Regex Wrapper = new Regex(
        @"\AonUpdateArenaRecommend\)\s*!==\s*'undefined'\)\s*window\.onUpdateArenaRecommend\(new\s+TextDecoder\('utf-8'\)\.decode\(Uint8Array\.from\(atob\('(?<payload>[A-Za-z0-9+/]*={0,2})'\),\s*c\s*=>\s*c\.charCodeAt\(0\)\)\)\)\s*;?\s*\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex CardId = new Regex(@"\A[A-Za-z0-9_]{1,128}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new Regex(@"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static bool TryParse(string line, out ArenaDeckDiscardRecommendation result, out string reason)
    {
        result = null;
        reason = "盒子弃牌记录格式不完整";
        // JSON escaping can expand a byte to six ASCII characters. Bound the
        // envelope before parsing, then apply the actual collector byte cap.
        if (string.IsNullOrEmpty(line) || line.Length > ScriptCaptureLimit * 6 + 4096 ||
            line.Length < 25 || line[23] != ' ') return false;
        try
        {
            if (!DateTime.TryParseExact(line.Substring(0, 23), "yyyy-MM-dd'T'HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var loggedAt))
            { reason = "盒子弃牌记录时间无效"; return false; }
            var envelope = ReadObject(line.Substring(24));
            if (!HasOnly(envelope, "type", "pid", "name", "source", "script") ||
                Text(envelope["type"]) != "capture_debug" || Text(envelope["name"]) != "onUpdateArenaRecommend")
            { reason = "不是盒子竞技场专用采集记录"; return false; }
            var source = Text(envelope["source"]);
            if (source != "upstream" && source != "execute")
            { reason = "盒子弃牌记录来源不受支持"; return false; }
            if (envelope["pid"].Type != JTokenType.Integer ||
                !int.TryParse(envelope["pid"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            { reason = "盒子弃牌记录进程号无效"; return false; }
            var script = Text(envelope["script"]);
            if (script == null || StrictUtf8.GetByteCount(script) >= ScriptCaptureLimit)
            { reason = "盒子弃牌记录可能已截断"; return false; }
            var match = Wrapper.Match(script);
            if (!match.Success)
            { reason = "盒子弃牌脚本封装不完整或不受支持"; return false; }
            var encoded = match.Groups["payload"].Value;
            var bytes = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(bytes) != encoded)
            { reason = "盒子弃牌编码不规范"; return false; }
            var payload = ReadObject(StrictUtf8.GetString(bytes));
            if (!HasOnly(payload, "status", "error", "data") || payload["status"].Type != JTokenType.Integer ||
                payload["status"].ToString() != "2" || Text(payload["error"]) != "")
            { reason = "盒子未返回成功的弃牌推荐"; return false; }
            var actions = payload["data"] as JArray;
            if (actions == null || actions.Count == 0 || actions.Count > 2)
            { reason = "盒子弃牌动作列表不受支持"; return false; }
            JArray referenceCards = null;
            var seenTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in actions)
            {
                var action = token as JObject;
                if (!HasOnly(action, "actionName", "type", "card") || Text(action["actionName"]) != "discard")
                { reason = "盒子未返回明确的参考弃牌动作"; return false; }
                var type = Text(action["type"]);
                if ((type != "reference" && type != "custom_based") || !seenTypes.Add(type))
                { reason = "盒子弃牌方案类型未知或重复"; return false; }
                var cards = action["card"] as JArray;
                if (cards == null || cards.Count == 0 || cards.Count > 40)
                { reason = "盒子弃牌列表为空或份数无效"; return false; }
                foreach (var card in cards)
                {
                    var item = card as JObject;
                    if (!HasOnly(item, "cardId"))
                    { reason = "盒子弃牌卡牌结构或数量语义不受支持"; return false; }
                    var id = Text(item["cardId"]);
                    if (id == null || !CardId.IsMatch(id))
                    { reason = "盒子弃牌卡牌编号无效"; return false; }
                }
                // Real Box records can contain A (reference) and a shorter B
                // (custom_based). B is a separate alternative, never a source
                // of extra cards or a fallback when A is absent/incomplete.
                if (type == "reference") referenceCards = cards;
            }
            if (referenceCards == null)
            { reason = "盒子未返回唯一的参考弃牌方案 A"; return false; }
            var parsed = new ArenaDeckDiscardRecommendation
            {
                BoxPid = pid,
                LogTimeLocal = DateTime.SpecifyKind(loggedAt, DateTimeKind.Unspecified),
                Source = source,
                TotalCopies = referenceCards.Count
            };
            foreach (var card in referenceCards)
            {
                var id = Text(((JObject)card)["cardId"]);
                parsed.RemoveCounts.TryGetValue(id, out var count);
                parsed.RemoveCounts[id] = count + 1;
            }
            using (var sha = SHA256.Create())
                parsed.ScriptHash = BitConverter.ToString(sha.ComputeHash(StrictUtf8.GetBytes(script))).Replace("-", "");
            result = parsed;
            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is DecoderFallbackException ||
                                   ex is EncoderFallbackException || ex is RegexMatchTimeoutException || ex is ArgumentException)
        {
            // Do not include raw script/JSON in user-facing diagnostics.
            reason = "盒子弃牌记录无法完整解析：" + ex.GetType().Name;
            return false;
        }
    }

    internal static bool IsCardId(string value) => value != null && CardId.IsMatch(value);

    internal static bool TryReadEnvelope(string line, out JObject payload, out int pid,
        out DateTime loggedAt, out string source, out string script, out string reason)
    {
        payload = null; pid = 0; loggedAt = default; source = script = null;
        reason = "盒子竞技场建议封套不完整";
        if (string.IsNullOrEmpty(line) || line.Length > ScriptCaptureLimit * 6 + 4096 ||
            line.Length < 25 || line[23] != ' ') return false;
        try
        {
            if (!DateTime.TryParseExact(line.Substring(0, 23), "yyyy-MM-dd'T'HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out loggedAt)) return false;
            var envelope = ReadObject(line.Substring(24));
            if (!HasOnly(envelope, "type", "pid", "name", "source", "script") ||
                Text(envelope["type"]) != "capture_debug" || Text(envelope["name"]) != "onUpdateArenaRecommend")
                return false;
            source = Text(envelope["source"]);
            if (source != "upstream" && source != "execute") return false;
            if (envelope["pid"].Type != JTokenType.Integer ||
                !int.TryParse(envelope["pid"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0)
                return false;
            script = Text(envelope["script"]);
            if (script == null || StrictUtf8.GetByteCount(script) >= ScriptCaptureLimit) return false;
            var match = Wrapper.Match(script);
            if (!match.Success) return false;
            var encoded = match.Groups["payload"].Value;
            var bytes = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(bytes) != encoded) return false;
            payload = ReadObject(StrictUtf8.GetString(bytes));
            if (!HasOnly(payload, "status", "error", "data") || payload["status"].Type != JTokenType.Integer ||
                payload["status"].ToString() != "2" || Text(payload["error"]) != "") return false;
            loggedAt = DateTime.SpecifyKind(loggedAt, DateTimeKind.Unspecified);
            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is DecoderFallbackException ||
                                   ex is EncoderFallbackException || ex is RegexMatchTimeoutException || ex is ArgumentException)
        {
            reason = "盒子竞技场建议无法完整解析：" + ex.GetType().Name;
            return false;
        }
    }

    internal static string Text(JToken value) => value != null && value.Type == JTokenType.String ? (string)value : null;

    internal static bool HasOnly(JObject value, params string[] keys)
    {
        if (value == null || value.Count != keys.Length) return false;
        foreach (var key in keys) if (value[key] == null) return false;
        return true;
    }

    private static JObject ReadObject(string json)
    {
        // Validate independently of JObject's last-property-wins behavior.
        // Duplicate keys, comments, constructors and permissive JS quoting
        // must never turn an ambiguous wire record into an accepted plan.
        ValidateJsonLexemes(json);
        var objects = new Stack<HashSet<string>>();
        using (var text = new StringReader(json))
        using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 })
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonToken.StartObject: objects.Push(new HashSet<string>(StringComparer.Ordinal)); break;
                    case JsonToken.EndObject: objects.Pop(); break;
                    case JsonToken.PropertyName:
                        if (objects.Count == 0 || !objects.Peek().Add((string)reader.Value))
                            throw new FormatException("Ambiguous JSON property");
                        break;
                    case JsonToken.Comment:
                    case JsonToken.Undefined:
                    case JsonToken.StartConstructor:
                    case JsonToken.EndConstructor:
                    case JsonToken.Raw:
                        throw new FormatException("Unsupported JSON token");
                }
            }
        }
        return JObject.Parse(json);
    }

    private static void ValidateJsonLexemes(string json)
    {
        // The game's stripped JsonTextReader omits QuoteChar. Validate JSON
        // string/literal syntax before using its structural/token reader.
        for (var i = 0; i < json.Length;)
        {
            var c = json[i++];
            if (c == '}' || c == ']')
            {
                var previous = i - 2;
                while (previous >= 0 && char.IsWhiteSpace(json[previous])) previous--;
                if (previous >= 0 && json[previous] == ',') throw new FormatException("Trailing JSON comma");
            }
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' ||
                c == '{' || c == '}' || c == '[' || c == ']' || c == ':' || c == ',') continue;
            if (c == '"')
            {
                var closed = false;
                while (i < json.Length)
                {
                    c = json[i++];
                    if (c == '"') { closed = true; break; }
                    if (c < 0x20) throw new FormatException("Control character in JSON string");
                    if (c != '\\') continue;
                    if (i == json.Length) throw new FormatException("Incomplete JSON escape");
                    c = json[i++];
                    if (c == 'u')
                    {
                        for (var digit = 0; digit < 4; digit++)
                        {
                            if (i == json.Length) throw new FormatException("Incomplete Unicode escape");
                            c = json[i++];
                            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F'))
                                throw new FormatException("Invalid Unicode escape");
                        }
                    }
                    else if (c != '"' && c != '\\' && c != '/' && c != 'b' && c != 'f' && c != 'n' && c != 'r' && c != 't')
                        throw new FormatException("Invalid JSON escape");
                }
                if (!closed) throw new FormatException("Incomplete JSON string");
                continue;
            }
            var start = i - 1;
            while (i < json.Length && json[i] != ' ' && json[i] != '\t' && json[i] != '\r' && json[i] != '\n' &&
                   json[i] != '{' && json[i] != '}' && json[i] != '[' && json[i] != ']' && json[i] != ':' && json[i] != ',') i++;
            var literal = json.Substring(start, i - start);
            if (literal == "true" || literal == "false" || literal == "null") continue;
            if (!Number.IsMatch(literal)) throw new FormatException("Unsupported JSON literal");
        }
    }
}
