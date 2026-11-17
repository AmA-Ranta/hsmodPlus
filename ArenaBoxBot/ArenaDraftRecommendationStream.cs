using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ArenaBoxBot;

/// <summary>
/// The Box callback has no deck/request ID. Its only safe use is a newly
/// observed callback after an authoritative native bucket packet. Never
/// search historical log entries for a matching card or use card scores.
/// </summary>
internal static class ArenaDraftRecommendationStream
{
    private const int MaxReadBytes = 1024 * 1024;
    private static string LogPath => ArenaRuntimePaths.File("hsang-recommend-call.log");
    private static string HeartbeatPath => ArenaRuntimePaths.File("boxai-heartbeat.json");
    private static bool s_initialized;
    private static long s_position;
    private static byte[] s_prefix;
    private static DateTime s_created;
    private static string s_fragment = "";
    private static string s_window;
    private static DateTime s_windowOpened;
    private static string s_producer;
    private static int s_boxPid;
    private static int s_gamePid;
    private static ArenaDraftRecommendation s_recommendation;
    private static long s_recommendationOffset;
    private static string s_diagnostic = "尚未观察本轮原生竞技场阶段";

    internal static string Diagnostic => s_diagnostic;
    internal static string Bucket(bool underground, long deck, long redraft, bool redrafting,
        int slot, string slotType, IList<string> cards)
    {
        if (deck <= 0 || (redrafting && redraft <= 0) || slot < 0 || cards == null || cards.Count == 0 ||
            string.IsNullOrEmpty(slotType) || cards.Any(id => !ArenaDeckDiscardParser.IsCardId(id))) return null;
        return (underground ? "U:" : "N:") + deck + ":" + (redrafting ? redraft : 0) +
            (redrafting ? ":R:" : ":D:") + slot + ":" + slotType + ":" + string.Join(",", cards);
    }

    internal static void InitializeBoundary()
    {
        if (s_initialized) return;
        try { ResetCursorToEnd(); s_initialized = true; }
        catch (Exception ex) { s_diagnostic = "无法建立盒子采集边界：" + ex.GetType().Name; }
    }

    internal static void BeginNativeWindow(string window, string arenaState)
    {
        InitializeBoundary();
        if (!s_initialized) return;
        if (arenaState != "REDRAFTING" && arenaState != "DRAFTING")
        {
            s_window = null;
            s_recommendation = null;
            return;
        }
        if (string.IsNullOrEmpty(window)) { s_window = null; s_recommendation = null; return; }
        if (s_window == window) return;
        // This is called from the packet getter postfix, before DraftDisplay
        // receives it, and before native page-entry actions. The one-shot Box
        // event can then arrive before the first automated choice tick.
        s_window = window;
        var opened = DateTime.Now;
        s_windowOpened = new DateTime(opened.Ticks - opened.Ticks % TimeSpan.TicksPerMillisecond);
        s_recommendation = null;
        ResetCursorToEnd();
        BindProducer();
        s_diagnostic = "已在原生阶段入口布置本轮盒子采集游标";
        ArenaBoxBotPlugin.LogSource?.LogInfo("Arena draft recommendation capture window opened before native UI: " + window +
            "; offset=" + s_position + "; producer=" + s_producer + "; BoxPid=" + s_boxPid);
    }

    private static bool ReadProducer(out string producer, out int boxPid, out int gamePid)
    {
        producer = ""; boxPid = 0; gamePid = 0;
        try
        {
            var heartbeat = JObject.Parse(File.ReadAllText(HeartbeatPath));
            var stamp = (long?)heartbeat["ts"] ?? 0;
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp;
            producer = (string)heartbeat["producerInstanceId"];
            boxPid = (int?)heartbeat["pid"] ?? 0;
            gamePid = (int?)heartbeat["gamePid"] ?? 0;
            return age >= -5000 && age <= 30000 && boxPid > 0 &&
                gamePid == Process.GetCurrentProcess().Id && !string.IsNullOrEmpty(producer);
        }
        catch { return false; }
    }

    private static void BindProducer()
    {
        if (!ReadProducer(out s_producer, out s_boxPid, out s_gamePid))
        { s_producer = null; s_boxPid = s_gamePid = 0; }
    }

    private static void ResetCursorToEnd()
    {
        s_fragment = "";
        s_prefix = null;
        s_position = 0;
        s_created = default;
        if (!File.Exists(LogPath)) return;
        using (var file = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            s_created = File.GetCreationTimeUtc(LogPath);
            s_position = file.Length;
            if (file.Length >= 64)
            { s_prefix = new byte[64]; file.Read(s_prefix, 0, s_prefix.Length); }
        }
    }

    internal static void Poll()
    {
        if (string.IsNullOrEmpty(s_window)) return;
        try
        {
            if (!ReadProducer(out var producer, out var boxPid, out var gamePid))
            { s_diagnostic = "盒子心跳未绑定当前炉石进程"; return; }
            if (s_producer != producer || s_boxPid != boxPid || s_gamePid != gamePid)
            {
                // A first valid heartbeat must not skip bytes captured after
                // the native window opened: the callback can be one-shot.
                // A genuinely new Box PID is distinguishable in each record.
                // Only same-PID generation replacement is ambiguous because
                // capture_debug has no producer GUID; exclude that old tail.
                var ambiguousGeneration = !string.IsNullOrEmpty(s_producer) &&
                    s_boxPid == boxPid && (s_producer != producer || s_gamePid != gamePid);
                s_recommendation = null;
                if (ambiguousGeneration) ResetCursorToEnd();
                s_producer = producer; s_boxPid = boxPid; s_gamePid = gamePid;
                s_diagnostic = ambiguousGeneration
                    ? "盒子同进程采集代次改变，旧封套无法区分代次；等待边界后的新建议"
                    : "已绑定当前盒子进程，继续读取本轮原生入口之后的记录";
                if (ambiguousGeneration) return;
            }
            if (!File.Exists(LogPath)) { s_diagnostic = "缺少盒子原始建议采集日志"; return; }
            using (var file = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var prefix = new byte[Math.Min(64, (int)Math.Min(file.Length, 64))];
                file.Read(prefix, 0, prefix.Length);
                if (s_created == default) s_created = File.GetCreationTimeUtc(LogPath);
                if (file.Length < s_position || File.GetCreationTimeUtc(LogPath) != s_created ||
                    (s_prefix != null && (prefix.Length < 64 || !s_prefix.SequenceEqual(prefix))))
                {
                    s_recommendation = null;
                    // Rotation can occur immediately before the only draft recommendation
                    // event. Read the new generation from its first byte;
                    // the original native-window time and current producer
                    // PID still reject historical/foreign records below.
                    s_position = 0;
                    s_fragment = "";
                    s_created = File.GetCreationTimeUtc(LogPath);
                    s_prefix = prefix.Length == 64 ? prefix : null;
                    s_diagnostic = "盒子日志已轮换，按本轮原生边界读取新文件首条记录";
                }
                if (s_prefix == null && prefix.Length == 64) s_prefix = prefix;
                if (file.Length - s_position > MaxReadBytes)
                {
                    s_recommendation = null;
                    ResetCursorToEnd();
                    s_diagnostic = "盒子建议日志积压超过安全读取上限，等待新回调";
                    return;
                }
                file.Position = s_position;
                var bytes = new byte[(int)(file.Length - s_position)];
                var read = file.Read(bytes, 0, bytes.Length);
                s_position += read;
                // The observed script wrapper is ASCII (base64). UTF-8 is
                // decoded only after a full line is available by the parser.
                s_fragment += Encoding.UTF8.GetString(bytes, 0, read);
                if (s_fragment.Length > MaxReadBytes)
                {
                    s_recommendation = null;
                    ResetCursorToEnd();
                    s_diagnostic = "盒子推荐记录超出完整封套上限，等待新回调";
                    return;
                }
                var end = s_fragment.LastIndexOf('\n');
                if (end < 0) return;
                var complete = s_fragment.Substring(0, end + 1);
                s_fragment = s_fragment.Substring(end + 1);
                foreach (var line in complete.Split('\n'))
                {
                    if (line.IndexOf("capture_debug", StringComparison.Ordinal) < 0 ||
                        line.IndexOf("onUpdateArenaRecommend", StringComparison.Ordinal) < 0) continue;
                    if (!ArenaDraftRecommendationParser.TryParse(line.TrimEnd('\r'), out var parsed, out var reason))
                    { s_recommendation = null; s_diagnostic = "等待明确推荐选卡回调：" + reason; continue; }
                    if (parsed.BoxPid != s_boxPid || parsed.LogTimeLocal < s_windowOpened ||
                        parsed.LogTimeLocal > DateTime.Now.AddSeconds(5))
                    { s_diagnostic = "推荐选卡回调不属于当前盒子实例或本轮采集窗口"; continue; }
                    s_recommendation = parsed;
                    s_recommendationOffset = s_position - Encoding.UTF8.GetByteCount(s_fragment);
                    s_diagnostic = "已收到当前原生候选组的明确推荐选卡回调";
                }
            }
        }
        catch (Exception ex) { s_diagnostic = "读取盒子推荐选卡建议失败：" + ex.GetType().Name + ": " + ex.Message; }
    }

    internal static bool CaptureReady(out string reason)
    {
        reason = "等待绑定当前炉石的盒子采集心跳与原建议日志";
        return ReadProducer(out _, out _, out _) && File.Exists(LogPath);
    }

    internal static int Read(string window, IList<string> candidates, out string detail)
    {
        Poll();
        detail = s_diagnostic;
        if (window != s_window || s_recommendation == null) return 0;
        if (!ReadProducer(out var producer, out var boxPid, out var gamePid) ||
            producer != s_producer || boxPid != s_boxPid || gamePid != s_gamePid) return 0;
        var age = DateTime.Now - s_recommendation.LogTimeLocal;
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(120))
        { detail = "当前推荐选卡已超过120秒，等待新建议"; return 0; }
        var choice = ArenaDraftRecommendationParser.Choice(s_recommendation, candidates, out detail);
        if (choice <= 0) return 0;
        detail += "; Box:onUpdateArenaRecommend; producer=" + producer + "; gamePid=" + gamePid +
            "; BoxPid=" + boxPid + "; nativeBucket=" + window + "; offset=" + s_recommendationOffset +
            "; callbackAt=" + s_recommendation.LogTimeLocal.ToString("O") +
            "; scriptSHA256=" + s_recommendation.ScriptHash;
        return choice;
    }
}
