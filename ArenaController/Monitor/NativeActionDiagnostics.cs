using System.Globalization;
using System.Text.RegularExpressions;

namespace ArenaController;

/// <summary>Counts observed log records, never recommendations or assumed server actions.</summary>
public sealed class NativeActionDiagnostics
{
    private readonly HashSet<string> _submissions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _choices = new(StringComparer.Ordinal);
    private bool _observedStart;

    public void Reset()
    {
        _submissions.Clear();
        _choices.Clear();
        _observedStart = false;
    }

    public string? Observe(string line)
    {
        if (line.Contains("Gameplay.Awake", StringComparison.Ordinal))
        {
            Reset();
            _observedStart = true;
            return null;
        }
        if (line.Contains("BoxAI链路提交:", StringComparison.Ordinal))
        {
            var request = Value(line, "requestId");
            var options = Value(line, "optionsId");
            if (UsableRequest(request) && int.TryParse(options, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var id) && id >= 0)
                _submissions.Add(request + ":" + id);
        }
        else if (line.Contains("BoxAI自动执行选择:", StringComparison.Ordinal))
        {
            var request = Value(line, "requestId");
            var entity = Regex.Match(line, @"\bentity=(.+)$", RegexOptions.CultureInvariant).Groups[1].Value;
            if (UsableRequest(request) && !string.IsNullOrWhiteSpace(entity)) _choices.Add(request + ":" + entity);
        }
        if (!line.Contains("WildLadderBot对局总结:", StringComparison.Ordinal)) return null;

        var raw = Value(line, "actions");
        var reported = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? count.ToString(CultureInfo.InvariantCulture) : "unknown";
        var result = "observedSubmitRecords=" + _submissions.Count +
            ";observedChoiceRecords=" + _choices.Count + ";nativeReportedActions=" + reported +
            ";coverage=" + (_observedStart ? "since-game-start" : "partial-after-attachment") +
            ";zeroSummaryConflict=" + (reported == "0" && (_submissions.Count > 0 || _choices.Count > 0)) +
            ";meaning=去重提交记录数（含结束回合），不代表服务端已确认动作数";
        Reset();
        return result;
    }

    private static bool UsableRequest(string value)
        => !string.IsNullOrEmpty(value) && value is not "none" and not "null";

    private static string Value(string line, string name)
        => Regex.Match(line, @"\b" + Regex.Escape(name) + @"=([^,\s;]+)",
            RegexOptions.CultureInvariant).Groups[1].Value;
}
