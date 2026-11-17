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
/// observed callback inside a native Arena phase that we opened and whose
/// entire server deck is known. Never search the log for the last discard.
/// </summary>
internal static class ArenaDeckDiscardStream
{
    internal sealed class BoundRecommendation
    {
        internal string ContextKey;
        internal string Evidence;
        internal Dictionary<string, int> RemoveCounts;
    }
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
    private static ArenaDeckDiscardRecommendation s_recommendation;
    private static long s_recommendationOffset;
    private static string s_diagnostic = "尚未观察本轮原生竞技场阶段";

    internal static string Diagnostic => s_diagnostic;
    internal static string NativeWindow(bool underground, long deck, long redraft, int wins, int losses) =>
        (underground ? "U:" : "N:") + deck + ":" + redraft + ":" + wins + ":" + losses;

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
        if (arenaState != "MIDRUN_REDRAFT_PENDING" && arenaState != "REDRAFTING" &&
            arenaState != "DRAFTING" && arenaState != "EDITING_DECK")
        {
            s_window = null;
            s_recommendation = null;
            return;
        }
        if (string.IsNullOrEmpty(window) || s_window == window) return;
        // This is called from the packet getter postfix, before DraftDisplay
        // receives it, and before native page-entry actions. The one-shot Box
        // event can then arrive before the first HandleArenaDeckEditing tick.
        s_window = window;
        var opened = DateTime.Now;
        s_windowOpened = new DateTime(opened.Ticks - opened.Ticks % TimeSpan.TicksPerMillisecond);
        s_recommendation = null;
        ResetCursorToEnd();
        BindProducer();
        s_diagnostic = "已在原生阶段入口布置本轮盒子采集游标";
        ArenaBoxBotPlugin.LogSource?.LogInfo("Arena discard capture window opened before native UI: " + window +
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
                    // Rotation can occur immediately before the only discard
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
                var end = s_fragment.LastIndexOf('\n');
                if (end < 0) return;
                var complete = s_fragment.Substring(0, end + 1);
                s_fragment = s_fragment.Substring(end + 1);
                foreach (var line in complete.Split('\n'))
                {
                    if (line.IndexOf("capture_debug", StringComparison.Ordinal) < 0 ||
                        line.IndexOf("onUpdateArenaRecommend", StringComparison.Ordinal) < 0) continue;
                    if (!ArenaDeckDiscardParser.TryParse(line.TrimEnd('\r'), out var parsed, out var reason))
                    { s_diagnostic = "等待明确弃牌回调：" + reason; continue; }
                    if (parsed.BoxPid != s_boxPid || parsed.LogTimeLocal < s_windowOpened ||
                        parsed.LogTimeLocal > DateTime.Now.AddSeconds(5))
                    { s_diagnostic = "弃牌回调不属于当前盒子实例或本轮采集窗口"; continue; }
                    s_recommendation = parsed;
                    s_recommendationOffset = s_position - Encoding.UTF8.GetByteCount(s_fragment);
                    s_diagnostic = "已收到本轮盒子明确弃牌回调，等待完整套牌一致性校验";
                }
            }
        }
        catch (Exception ex) { s_diagnostic = "读取盒子弃牌建议失败：" + ex.GetType().Name + ": " + ex.Message; }
    }

    internal static BoundRecommendation Read(string window, string contextKey,
        IList<ArenaDeckEditCard> current, IList<ArenaDeckEditCard> authoritativeMerged, int targetCount)
    {
        Poll();
        if (window != s_window || s_recommendation == null) return null;
        if (!ReadProducer(out var producer, out var boxPid, out var gamePid) ||
            producer != s_producer || boxPid != s_boxPid || gamePid != s_gamePid) return null;
        if (DateTime.Now - s_recommendation.LogTimeLocal > TimeSpan.FromSeconds(120))
        { s_diagnostic = "本轮弃牌建议已超过 120 秒，等待新建议"; return null; }
        if (!ArenaDeckEditPolicy.IsValidCards(authoritativeMerged) ||
            ArenaDeckEditPolicy.Fingerprint(current) != ArenaDeckEditPolicy.Fingerprint(authoritativeMerged))
        { s_diagnostic = "当前编辑套牌与服务器主套牌加重选套牌不完全一致"; return null; }
        if (!ArenaDeckEditPolicy.TryPlan(current, targetCount, s_recommendation.RemoveCounts, out _, out var invalid))
        { s_diagnostic = invalid; return null; }
        var evidence = "Box:onUpdateArenaRecommend; producer=" + producer + "; gamePid=" + gamePid +
            "; BoxPid=" + boxPid + "; nativeWindow=" + window + "; offset=" + s_recommendationOffset +
            "; scriptSHA256=" + s_recommendation.ScriptHash;
        return new BoundRecommendation { ContextKey = contextKey, Evidence = evidence,
            RemoveCounts = new Dictionary<string, int>(s_recommendation.RemoveCounts, StringComparer.Ordinal) };
    }
}
