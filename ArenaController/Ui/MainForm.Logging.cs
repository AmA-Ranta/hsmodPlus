using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArenaControl.Contracts;

using ArenaController.Ui.Design;

namespace ArenaController;

internal sealed partial class MainForm : Form
{

    private static string DescribeSettings(ControllerSettings s) =>
        s.Mode + (string.Equals(s.Mode, "竞技场", StringComparison.Ordinal) ? "/" + s.ArenaMode : "") +
        "；卡组=" + (string.IsNullOrWhiteSpace(s.DeckName) ? "未指定" : s.DeckName) +
        "；每轮对局=" + s.MaxGamesPerRun + "；任务总局=" + s.MaxTotalGames +
        "；最多轮数=" + s.MaxRuns +
        "；计划=" + (s.AutoSchedule ? s.StartTime + "-" + s.StopTime : "关闭") +
        "；自动恢复=" + (s.AutoReconnect ? "开" : "关");


    private void PublishConstructedStopRequest()
    {
        try
        {
            if (!TryReadGameProcessIdentity(out var running, out var pid, out var started) || !running) return;
            if (_constructedStopRequest != null &&
                (_constructedStopRequest.GameProcessId != pid || _constructedStopRequest.GameStartedAtUtcTicks != started.Ticks))
            {
                // A user/official launcher can replace the process while the
                // stop is pending. Old requests must not strand the new client.
                _constructedStopRequest = null;
                RemoveControlFile(ConstructedStopRequestPath);
                RemoveControlFile(ConstructedStopReceiptPath);
            }
            _constructedStopRequest ??= new ConstructedStopRequest
            {
                RequestId = Guid.NewGuid().ToString("N"), GameProcessId = pid,
                GameStartedAtUtcTicks = started.Ticks, RequestedAtUtcTicks = DateTime.UtcNow.Ticks,
                Mode = _settings.Mode ?? ""
            };
            // Receipt delivery is independent of the slower API status poll.
            // Do not republish an already acknowledged request while waiting
            // for fresh OFF; completion still requires that fresh OFF below.
            if (File.Exists(ConstructedStopRequestPath) || HasMatchingConstructedStopReceipt(pid, started)) return;
            Directory.CreateDirectory(_settings.HsModDirectory);
            var pending = ConstructedStopRequestPath + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(pending, JsonSerializer.Serialize(_constructedStopRequest,
                    new JsonSerializerOptions { IncludeFields = true }), Encoding.UTF8);
                File.Move(pending, ConstructedStopRequestPath, true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            AppendDeveloper("局后停止请求已发布：requestId=" + _constructedStopRequest.RequestId + ";pid=" + pid);
        }
        catch (Exception ex) { AppendDeveloper("局后停止请求未确认，将重试：" + ex.GetType().Name); }
    }


    private bool HasConstructedStopReceipt()
    {
        try
        {
            if (_constructedStopRequest == null || !IsHsModStatusFresh() || _hsModProbeUnavailable ||
                _hsModStatus?.ScriptEnabled != false ||
                !TryReadGameProcessIdentity(out var running, out var pid, out var started) || !running ||
                _hsModStatus.GameProcessId != pid) return false;
            // HsMod publishes start time at millisecond precision while
            // Process.StartTime keeps 100ns ticks; exact equality never holds.
            if (Math.Abs((_hsModStatus.GameStartedAtUtc - started).TotalMilliseconds) > 2000) return false;
            return HasMatchingConstructedStopReceipt(pid, started);
        }
        catch { return false; }
    }


    private void AppendLatestLog()
    {
        try
        {
            if (!_running || _stopRequested || _reconnecting) _connectionRecovery.Reset();
            var gameLines = _connectionLogReader.Read(PreferredHsModLogPath);
            if (_connectionLogReader.SessionChanged)
            {
                _connectionRecovery.Reset();
                _nativeActionDiagnostics.Reset();
                _networkHistoryPrimed = false;
            }
            foreach (var line in gameLines)
            {
                var actionSummary = _nativeActionDiagnostics.Observe(line);
                if (actionSummary != null) AppendDeveloper("game-action-diagnostics=" + actionSummary);
                if (_running && !_stopRequested && !_reconnecting) _connectionRecovery.Observe(DateTime.Now, line, requireProgressAfterLogin: !IsArenaMode);
                if (_running && !_stopRequested && !_reconnecting)
                {
                    var timeoutNotice = _connectionRecovery.ObserveRecommendationTimeout(DateTime.Now, line);
                    if (timeoutNotice != null) AppendDeveloper("box-timeout=" + timeoutNotice + "; raw=" + line);
                }
                if (IsArenaMode) AppendDeveloper("game=" + line);
            }
        }
        catch (Exception ex) { AppendDeveloper("connection-log.read=" + ex.GetType().Name + ": " + ex.Message); }
        var path = IsArenaMode ? ArenaLogPath : (File.Exists(PreferredHsModLogPath) ? PreferredHsModLogPath : (Directory.Exists(_settings.HsModDirectory) ? Directory.EnumerateFiles(_settings.HsModDirectory, "LogOutput.log", SearchOption.AllDirectories).FirstOrDefault() : null));
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var lines = _nativeLogReader.Read(path);
            foreach (var line in lines)
            {
                AppendDeveloper("native=" + line);
                if (!IsBenignNativeNotice(line) &&
                    Regex.IsMatch(line, @"\b(error|exception|fatal)\b|自动重启失败|停止原因", RegexOptions.IgnoreCase) &&
                    DateTime.Now - _lastNativeErrorNotice > TimeSpan.FromMinutes(1))
                {
                    _lastNativeErrorNotice = DateTime.Now;
                    Append("插件记录了异常，详细原因已保存到开发者日志：" + DeveloperLogWriter.CurrentPath);
                }
                if (IsArenaMode && line.Contains("Arena matchmaking handshake timed out", StringComparison.OrdinalIgnoreCase))
                    ObserveArenaMatchmakingTimeout();
                if (!IsArenaMode && MatchmakingTimeoutPolicy.IsConstructedTimeout(line))
                    ObserveConstructedMatchmakingTimeout();
            }
        }
        catch (Exception ex) { AppendDeveloper("native-log.read=" + ex.GetType().Name + ": " + ex.Message); }
    }


    private static void RemoveControlFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private void SetStatus(string value) { if (InvokeRequired) BeginInvoke(() => _status.Text = value); else _status.Text = value; }

    private void AppendDeveloper(string value)
    {
        var warning = DeveloperLogWriter.Write("[" + DateTimeOffset.Now.ToString("O") + "] " + value);
        if (warning != null) Append(warning);
    }

    private void Append(string value)
    {
        if (InvokeRequired) { BeginInvoke(() => Append(value)); return; }
        var line = $"[{DateTime.Now:HH:mm:ss}] {value}";
        AddRecentEvent(value);
        RefreshDashboard();
        HostChanged?.Invoke();
        _log.AppendText(line + "\r\n");
        var warning = UserLogWriter.Write(line);
        var developerWarning = DeveloperLogWriter.Write("[" + DateTimeOffset.Now.ToString("O") + "] user=" + value);
        if (warning != null) _log.AppendText(warning + "\r\n");
        if (developerWarning != null) _log.AppendText(developerWarning + "\r\n");
        // The durable files keep the full history; cap only the visible recent log.
        if (_log.TextLength > 50000)
        {
            var keep = _log.Text[^30000..];
            var firstLine = keep.IndexOf('\n');
            _log.Text = firstLine >= 0 ? keep[(firstLine + 1)..] : keep;
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }

}
