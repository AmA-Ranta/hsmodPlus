using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ArenaBoxBot;

internal sealed class ArenaDraftRecommendation
{
    internal int BoxPid;
    internal DateTime LogTimeLocal;
    internal string ScriptHash;
    internal string Source;
    internal List<string> CardIds = new List<string>();
}

// Reads the original dedicated Arena callback. Scores and popularity are
// deliberately absent: they are not the Box's recommended drafting action.
internal static class ArenaDraftRecommendationParser
{
    internal static bool TryParse(string line, out ArenaDraftRecommendation result, out string reason)
    {
        result = null;
        if (!ArenaDeckDiscardParser.TryReadEnvelope(line, out var payload, out var pid,
            out var loggedAt, out var source, out var script, out reason)) return false;
        var actions = payload["data"] as JArray;
        if (actions == null || actions.Count < 1 || actions.Count > 2)
        { reason = "推荐打法选卡列表为空或不受支持"; return false; }
        var parsed = new ArenaDraftRecommendation
            { BoxPid = pid, LogTimeLocal = loggedAt, Source = source };
        foreach (var item in actions)
        {
            var action = item as JObject;
            var card = action?["card"] as JObject;
            if (!ArenaDeckDiscardParser.HasOnly(action, "actionName", "card") ||
                ArenaDeckDiscardParser.Text(action["actionName"]) != "choose" ||
                !ArenaDeckDiscardParser.HasOnly(card, "cardId"))
            { reason = "等待推荐打法的明确选卡动作"; return false; }
            var id = ArenaDeckDiscardParser.Text(card["cardId"]);
            if (!ArenaDeckDiscardParser.IsCardId(id) || parsed.CardIds.Contains(id))
            { reason = "推荐选卡编号无效或重复"; return false; }
            parsed.CardIds.Add(id);
        }
        using (var sha = SHA256.Create())
            parsed.ScriptHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(script))).Replace("-", "");
        result = parsed;
        reason = "";
        return true;
    }

    internal static int Choice(ArenaDraftRecommendation recommendation, IList<string> candidates, out string reason)
    {
        reason = "等待当前选项的推荐打法";
        if (recommendation == null || candidates == null || candidates.Count < 1 ||
            recommendation.CardIds.Count < 1) return 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in candidates)
            if (!ArenaDeckDiscardParser.IsCardId(id) || !seen.Add(id))
            { reason = "当前选项标识不完整或不唯一"; return 0; }
        // Do not search later actions to compensate for an invalid primary
        // recommendation. A foreign alternative invalidates the whole record.
        foreach (var id in recommendation.CardIds)
            if (!seen.Contains(id))
            { reason = "推荐选卡不属于当前完整候选组"; return 0; }
        for (var i = 0; i < candidates.Count; i++)
            if (candidates[i] == recommendation.CardIds[0])
            { reason = "推荐打法首条选卡=" + candidates[i]; return i + 1; }
        return 0;
    }
}
