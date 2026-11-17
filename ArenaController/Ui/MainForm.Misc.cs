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
    private static string ControllerDataDirectory
    {
        get
        {
            // A host can isolate controller data per process. Regression tests
            // use this before constructing forms, so synthetic UI events never
            // append to a real task's logs or save its settings.
            if (AppContext.GetData("ArenaController.DataDirectory") is string isolated &&
                !string.IsNullOrWhiteSpace(isolated)) return Path.GetFullPath(isolated);
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Never fall back to the process working directory: a portable
            // launch from D:\ (or another protected folder) must not leave
            // controller state beside the executable.
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            return Path.Combine(root, "ArenaBoxController");
        }
    }

    private string ControllerLogPath => Path.Combine(ControllerDataDirectory, "controller.log");

    private string EnabledPath => HsModPath(EnabledFileName);

    private string StopAfterGamePath => HsModPath(StopAfterGameFileName);

    private string InternalStopPath => HsModPath(InternalStopFileName);

    private string PreferredHsModLogPath => Path.Combine(_settings.HsModDirectory, "Instances", "auto-1", "LogOutput.log");

    private string ConstructedControlPath => HsModPath(ConstructedControlFileName);

    private string ConstructedStopRequestPath => HsModPath(ConstructedStopContract.RequestFile);

    private string ConstructedStopReceiptPath => HsModPath(ConstructedStopContract.ReceiptFile);


    // Runtime decisions use the applied snapshot, never the staged UI value.
    private bool IsArenaMode => string.Equals(_settings.Mode, "竞技场", StringComparison.Ordinal);

    // UI preview of the staged selection: the arena stop button follows the
    // combo box, while runtime checks still use the applied mode.
    private bool IsArenaSelectedInUi => string.Equals(_mode.SelectedItem?.ToString(), "竞技场", StringComparison.Ordinal);

    private string CurrentLadderMode => _settings.Mode switch
    {
        "狂野" => "wild",
        "休闲" => "casual",
        "酒馆战棋" => "battlegrounds",
        _ => "standard"
    };


    private static IEnumerable<(DirectoryInfo Directory, DateTime StartTime)> FindRecentHearthstoneLogSessions(string logsRoot, DateTime since)
    {
        // Directory names have one-second resolution; allow only that rounding
        // margin. Old-session writes during shutdown do not bind to a new PID.
        var threshold = since.AddSeconds(-1);
        foreach (var path in Directory.EnumerateDirectories(logsRoot, "Hearthstone_*", SearchOption.TopDirectoryOnly))
        {
            var directory = new DirectoryInfo(path);
            var start = directory.CreationTime;
            var name = directory.Name;
            if (name.StartsWith("Hearthstone_", StringComparison.OrdinalIgnoreCase) &&
                DateTime.TryParseExact(name.Substring("Hearthstone_".Length), "yyyy_MM_dd_HH_mm_ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                start = parsed;

            if (start >= threshold)
                yield return (directory, start);
        }
    }


    private async Task RecoverArenaMatchmakingAsync()
    {
        if (!_running || _stopRequested || _reconnecting) { _arenaNetworkRecovery = false; return; }
        _reconnecting = true;
        _recoveryPending = true;
        var taskGeneration = _taskGeneration;
        var recovered = false;
        _lastArenaNetworkRecoveryAt = DateTime.Now;
        try
        {
            Append("竞技场匹配连续两次握手超时；当前不在对局中，安全重启炉石和盒子网络会话。");
            SetStatus("匹配超时，正在安全重连");
            DisableConstructedScript();
            SetWatchdogAutoResume(false, strict: true);
            await CloseHearthstoneAsync(taskGeneration);
            StopBoxForConfiguration();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline &&
                   (IsHearthstoneRunning() || Process.GetProcessesByName("HSAExternal").Any()))
                await Task.Delay(250);

            ThrowIfTaskInterrupted(taskGeneration);
            LaunchHearthstoneViaBattleNet();
            EnsureBoxRunning();
            if (!await WaitForHearthstoneAfterLaunchAsync(TimeSpan.FromSeconds(30), 3))
                throw new TimeoutException("匹配超时恢复后，90 秒内未重新检测到 Hearthstone.exe");
            ThrowIfTaskInterrupted(taskGeneration);
            ApplyConfigurationWithBoxRetry();
            if (_settings.RequireBoxHeartbeat && !await WaitForBoxReadyAsync(TimeSpan.FromSeconds(90)))
                throw new TimeoutException("匹配超时恢复后，90 秒内未收到盒子心跳/推荐文件");
            await ScriptActivationGate.EnableAsync(EnsureConfiguredConstructedDeckAsync,
                () => ThrowIfTaskInterrupted(taskGeneration), EnableConstructedScript);
            _clientRecovery.Reset(); _arenaProgress.Reset(); _startedAt = DateTime.Now;
            recovered = true;
            _recoveryPending = false;
            _arenaMatchmakingTimeouts.Clear();
            Append("炉石和盒子网络会话已重置，竞技场脚本已恢复；将继续当前未完成轮次。");
        }
        catch (OperationCanceledException) { Append("已按停止请求取消匹配恢复。"); }
        catch (Exception ex)
        {
            Append("竞技场匹配超时自动恢复失败：" + ex.GetType().Name + "：" + ex.Message);
            NativeInstallService.WriteControllerDeveloperLog("RecoverArenaMatchmakingAsync", ex);
        }
        finally
        {
            _arenaNetworkRecovery = false;
            _reconnecting = false;
            if (_running && !_stopRequested) SetStatus(recovered ? "运行中（" + _settings.Mode + "）" : "恢复未完成，等待重试");
        }
    }


    private ArenaStats ReadStats()
    {
        try
        {
            if (!File.Exists(StatsPath)) return new ArenaStats { SnapshotKnown = true };
            using var stream = new FileStream(StatsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("RunCount", out var count) ||
                !count.TryGetInt32(out _) || !root.TryGetProperty("RunActive", out var active) ||
                active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return new ArenaStats();
            var stats = root.Deserialize<ArenaStats>();
            if (stats == null) return new ArenaStats();
            stats.SnapshotKnown = true;
            return stats;
        }
        catch { return new ArenaStats(); }
    }


    private void ReadMatchLogSummary(string mode, LadderSummary summary)
    {
        var path = HsModPath("match.log");
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (!MatchLogLineBelongsToMode(line, mode)) continue;
            summary.Games++;
            var fields = line.Split(',');
            var result = fields.Length > 1 ? fields[1].Trim() : "";
            ApplyResult(summary, result);
            var comma = line.IndexOf(',');
            if (comma > 0) summary.LastTime = line[..comma].Replace("<br />", " ").Trim();
        }
    }


    private void ReadBattlegroundSessionSummary(LadderSummary summary)
    {
        var path = HsModPath("bg_session.csv");
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) throw new InvalidDataException("战棋结算文件正在更新。");
        var header = lines[0].Split(';');
        var placeIndex = Array.FindIndex(header, x => x.Trim().Equals("place", StringComparison.OrdinalIgnoreCase));
        var modeIndex = Array.FindIndex(header, x => x.Trim().Equals("mode", StringComparison.OrdinalIgnoreCase));
        var timeIndex = Array.FindIndex(header, x => x.Trim().Equals("time", StringComparison.OrdinalIgnoreCase));
        var noteIndex = Array.FindIndex(header, x => x.Trim().Equals("note", StringComparison.OrdinalIgnoreCase));
        if (placeIndex < 0 || modeIndex < 0 || timeIndex < 0) throw new InvalidDataException("战棋结算表头不完整。");
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(';');
            if (fields.Length < header.Length || !int.TryParse(fields[placeIndex].Trim(), out var place))
                throw new InvalidDataException("战棋结算记录尚未完整写入。");
            // Native rating reconciliation writes place=0;note=synced. It
            // is neither a game result nor a corrupt/unfinished settlement.
            if (place == 0 && noteIndex >= 0 && fields[noteIndex].Trim().Equals("synced", StringComparison.OrdinalIgnoreCase)) continue;
            if (place < 1 || place > 8) throw new InvalidDataException("战棋结算名次无效。");
            if (fields[modeIndex].Trim() != "solo" && fields[modeIndex].Trim() != "duo") continue;
            summary.Games++;
            summary.LastResult = "第 " + place + " 名";
            summary.LastTime = fields[timeIndex].Trim();
        }
    }


    private static bool MatchLogLineBelongsToMode(string line, string mode)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return mode.ToLowerInvariant() switch
        {
            "standard" => line.Contains("FT_STANDARD", StringComparison.OrdinalIgnoreCase),
            "wild" => line.Contains("FT_WILD", StringComparison.OrdinalIgnoreCase),
            "casual" => line.Contains("GT_CASUAL", StringComparison.OrdinalIgnoreCase) || line.Contains("FT_CASUAL", StringComparison.OrdinalIgnoreCase),
            "battlegrounds" => line.Contains("GT_BATTLEGROUNDS", StringComparison.OrdinalIgnoreCase) || line.Contains("BATTLEGROUNDS", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }


    private static void ApplyResult(LadderSummary summary, string result)
    {
        summary.LastResult = string.IsNullOrWhiteSpace(result) ? "未知" : result;
        if (int.TryParse(result, out var numericResult))
        {
            if (numericResult == 1) summary.Wins++;
            else if (numericResult == 0) summary.Losses++;
            else if (numericResult == 2) summary.Draws++;
        }
        else if (result.Contains("win", StringComparison.OrdinalIgnoreCase) || result.Contains("胜")) summary.Wins++;
        else if (result.Contains("loss", StringComparison.OrdinalIgnoreCase) || result.Contains("lose", StringComparison.OrdinalIgnoreCase) || result.Contains("负") || result.Contains("失败")) summary.Losses++;
        else if (result.Contains("draw", StringComparison.OrdinalIgnoreCase) || result.Contains("平")) summary.Draws++;
    }


    private string ReadBattlegroundMmr()
    {
        try
        {
            var path = HsModPath("bg_last_mmr.txt");
            if (!File.Exists(path)) return "未读取";
            var values = new List<string>();
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                var match = Regex.Match(line, @"\.(?<kind>solo|duo)\s*=\s*(?<value>\d+)", RegexOptions.IgnoreCase);
                if (match.Success) values.Add((match.Groups["kind"].Value.Equals("solo", StringComparison.OrdinalIgnoreCase) ? "单人" : "双人") + " " + match.Groups["value"].Value);
            }
            return values.Count == 0 ? "未读取" : string.Join(" / ", values);
        }
        catch { return "未读取"; }
    }


    private bool IsHearthstoneRunning()
    {
        try { return HearthstoneProcessDiscovery.HasLaunchCandidate(_settings.HearthstoneDirectory); } catch { return true; }
    }


    private static string? GetExecutableDirectory(string executablePath)
    {
        try { return Path.GetDirectoryName(Path.GetFullPath(executablePath)); }
        catch { return null; }
    }


    private static bool IsProcessFromDirectory(Process process, string? expectedDirectory)
    {
        if (string.IsNullOrWhiteSpace(expectedDirectory)) return false;
        try
        {
            var actual = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actual)) return false;
            var actualDirectory = Path.GetDirectoryName(Path.GetFullPath(actual));
            return string.Equals(actualDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }


    private static string FileAgeText(string path)
    {
        try
        {
            if (!File.Exists(path)) return "未找到";
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            if (age.TotalSeconds < 60) return $"{Math.Max(0, (int)age.TotalSeconds)} 秒前";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} 分钟前";
            return $"{(int)age.TotalHours} 小时前";
        }
        catch { return "读取失败"; }
    }


    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }


    private static bool IsNormalBoxProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || !string.Equals(process.ProcessName, "HSAng", StringComparison.OrdinalIgnoreCase)) return false;
            // The heartbeat producer can be a hidden 32-bit Qt helper and its
            // main window may be reported as Dialog or HWND 0 from a 64-bit
            // controller. Fresh timestamp + live HSAng PID is the reliable
            // readiness signal; requiring a particular HWND caused repeated
            // false restarts and hid an actually working Box.
            return true;
        }
        catch { return false; }
    }


    private void RefreshRuntimeStatus()
    {
        ApplyPendingProbeResult();
        StartHsModStatusProbe();
        var gameRunning = IsHearthstoneRunning();
        var boxRunning = IsBoxRunning();
        var heartbeatPath = HsModPath(BoxHeartbeatFileName);
        var recommendationPath = HsModPath(BoxRecommendationFileName);
        var heartbeatStatus = ReadHeartbeatStatus(heartbeatPath, recommendationPath);
        var heartbeatFresh = heartbeatStatus.Fresh;
        var heartbeatAge = heartbeatStatus.Age;
        var script = DetectScriptRuntime(gameRunning);
        var apiText = !gameRunning ? "未连接" : _hsModProbeUnavailable || DateTime.UtcNow - _lastHsModSuccessAt > TimeSpan.FromSeconds(30) ? "状态暂不可读" : _hsModStatus == null ? "未连接" : !_hsModStatus.Ready ? "未就绪" : !_hsModStatus.PluginEnabled ? "插件关闭" : (_hsModStatus.ScriptEnabled ? "脚本开启" : "脚本关闭");
        var control = _starting ? "启动中" : (_reconnecting ? "重连中" : (_running ? "中控接管" : "未接管"));
        // The plugin publishes its own mode/version; the controller only
        // displays it and never infers the mode from local settings.
        var pluginRuntime = TryReadArenaRuntimeState(out var pluginState) ? pluginState : null;
        var pluginText = pluginRuntime == null
            ? "未上报"
            : string.IsNullOrWhiteSpace(pluginRuntime.mode)
                ? (string.IsNullOrWhiteSpace(pluginRuntime.pluginVersion) ? "已连接" : "v" + pluginRuntime.pluginVersion)
                : pluginRuntime.mode;
        _runtime.Text = $"炉石：{(gameRunning ? "运行中" : "未运行")}　盒子：{(boxRunning ? "运行中" : "未运行")}\r\n脚本：{script}　HsMod：{apiText}\r\n插件：{pluginText}　心跳：{(heartbeatFresh ? "正常" : "超时/未找到")}（{heartbeatAge}）　{control}　{DateTime.Now:HH:mm:ss}";
        _runtime.ForeColor = gameRunning && (heartbeatFresh || !_settings.RequireBoxHeartbeat) && !script.Contains("未", StringComparison.Ordinal) ? Color.FromArgb(27, 105, 73) : Color.FromArgb(139, 67, 56);

        var snapshot = $"炉石={(gameRunning ? "运行" : "未运行")};盒子={(boxRunning ? "运行" : "未运行")};脚本={script};心跳={(heartbeatFresh ? "正常" : "异常")}";
        if (_lastRuntimeSnapshot.Length > 0 && !string.Equals(snapshot, _lastRuntimeSnapshot, StringComparison.Ordinal)) AppendDeveloper("运行监控状态变化：" + snapshot);
        _lastRuntimeSnapshot = snapshot;
        RefreshDashboard();
        HostChanged?.Invoke();
    }


    /// <summary>
    /// Polls HsMod's read-only status endpoint in the background.  The
    /// endpoint is protected by the same short-lived page token used by the
    /// local web panel, so obtain the token from the home page first.  No POST
    /// or runtime toggle is performed here; opening the control center can
    /// therefore never start or stop a script by accident.
    /// </summary>
    private void StartHsModStatusProbe()
    {
        if (DateTime.Now - _lastHsModProbeAt < TimeSpan.FromSeconds(8)) return;
        _lastHsModProbeAt = DateTime.Now;
        if (Interlocked.Exchange(ref _hsModProbeRunning, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            HsModStatusSnapshot? snapshot = null;
            try { snapshot = await ProbeHsModStatusAsync().ConfigureAwait(false); }
            catch { }
            // Publish the result and release the single-flight latch on the
            // background thread. The UI consumption below is opportunistic: if
            // BeginInvoke is never pumped, the next RefreshRuntimeStatus tick
            // still applies the result, so a status probe can never wedge.
            _pendingProbeSnapshot = snapshot;
            _pendingProbeReady = true;
            Interlocked.Exchange(ref _hsModProbeRunning, 0);
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    ApplyPendingProbeResult();
                    RefreshRuntimeStatusFromProbe();
                }));
            }
            catch { }
        });
    }


    /// <summary>Consumes the latest background probe result on the UI thread.</summary>
    private void ApplyPendingProbeResult()
    {
        if (!_pendingProbeReady) return;
        _pendingProbeReady = false;
        var snapshot = _pendingProbeSnapshot;
        _pendingProbeSnapshot = null;
        _hsModProbeUnavailable = snapshot == null;
        if (snapshot != null) { _hsModStatus = snapshot; _lastHsModSuccessAt = DateTime.UtcNow; }
    }


    private bool TryReadGameProcessIdentitySafe(out bool running, out int pid, out DateTime startedAtUtc)
    {
        // Process discovery can throw transiently while the client is booting
        // (unverified/multiple processes). A failed identity read must not make
        // the whole status probe return null, otherwise the UI keeps showing
        // "HsMod 状态暂不可读" even though the local API is healthy.
        try { return TryReadGameProcessIdentity(out running, out pid, out startedAtUtc); }
        catch { running = false; pid = 0; startedAtUtc = DateTime.MinValue; return false; }
    }


    private async Task RefreshHsModStatusForStartAsync()
    {
        HsModStatusSnapshot? snapshot = null;
        try { snapshot = await ProbeHsModStatusAsync().ConfigureAwait(true); }
        catch { }
        _hsModProbeUnavailable = snapshot == null;
        if (snapshot != null)
        {
            _hsModStatus = snapshot;
            _lastHsModSuccessAt = DateTime.UtcNow;
            AppendDeveloper("启动前已刷新 HsMod 状态：ready=" + snapshot.Ready +
                ";loggedIn=" + snapshot.LoggedIn +
                ";scriptEnabled=" + snapshot.ScriptEnabled +
                ";activity=" + snapshot.Activity +
                ";gamePid=" + snapshot.GameProcessId);
        }
        else
        {
            AppendDeveloper("启动前 HsMod 状态刷新失败；不会在未知状态下执行盒子重启。");
        }
    }


    private void ApplyConfiguration()
    {
        // Keep HsMod's persisted locale and its first-start locale aligned.
        // The latter is what the native settings renderer uses after an old
        // English session has already created HsMod.cfg.
        NativeInstallService.EnsureChineseLanguage(_settings.HsModConfigPath);
        EnsureSectionKey(_settings.HsModConfigPath, "自动脚本", "推荐来源", "盒子文件");
        if (IsArenaMode)
        {
            // The 1004 HsMod enum has no Arena value. Keep its persisted
            // value valid (Standard) while the plugin-owned Arena config and
            // sidecar carry the virtual mode selection.
            EnsureSectionKey(_settings.HsModConfigPath, "自动脚本", "脚本模式", HsModModeMapping.ToNative("标准"));
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "脚本模式", "竞技场");
            // Keep a previously selected 普通竞技场/地下竞技场 subtype from
            // the native HsMod settings page. The controller changes the
            // top-level mode only and must not silently reset this choice.
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "竞技场模式", _settings.ArenaMode);
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "本轮最多对局数", _settings.MaxGamesPerRun.ToString());
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "门票不足自动停止", _settings.StopOnTicketShortage ? "true" : "false");
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "金币不足自动停止", _settings.StopOnGoldShortage ? "true" : "false");
        }
        else
        {
            EnsureSectionKey(_settings.HsModConfigPath, "自动脚本", "脚本模式", HsModModeMapping.ToNative(_settings.Mode));
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "脚本模式", _settings.Mode);
            // An empty controller field means "keep the deck already selected
            // in HsMod". Do not erase that native selection while preparing a
            // task; a specified name is resolved against the live collection
            // after the game finishes loading.
            if (!string.Equals(CurrentLadderMode, "battlegrounds", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_settings.DeckName))
            {
                if (!string.IsNullOrWhiteSpace(_settings.DeckName)) EnsureSectionKey(_settings.HsModConfigPath, "自动脚本", "卡组名称", _settings.DeckName.Trim());
                EnsureSectionKey(_settings.HsModConfigPath, "自动脚本", "卡组ID", "0");
            }
            EnsureSectionKey(_settings.HsModConfigPath, "自动脚本", "场数限制", _settings.MaxTotalGames.ToString());
            EnsureSectionKey(_settings.HsModConfigPath, "自动脚本", "开启或关闭脚本", "F12");
        }
        // 操作节奏由玩家在 HsMod 里自行选择（打牌 HumanLike、酒馆固定 Fastest）；
        // 中控不得改写此项。
    }


    private void ApplyConfigurationWithBoxRetry()
    {
        // Permission failures are not sharing locks. Try the requested write
        // first; only a real Win32 sharing/lock violation can restart Box,
        // after proving the current logged-in client is safely idle.
        var result = ConfigurationApplyGuard.Apply(ApplyConfiguration, ConfigurationAlreadyApplied,
            CanRestartBoxForConfiguration, StopBoxForConfiguration,
            error => Append("已确认配置文件共享锁，当前处于安全边界，重启盒子后重试：" +
                error.Message + "（HRESULT=0x" + error.HResult.ToString("X8") + "）"));
        if (result == ConfigurationApplyResult.ExistingVerified)
            Append("配置写入受限，但已逐项核验现有配置与本次选择一致，使用现有配置继续检查启动条件。");
        EnsureBoxRunning();
    }


    private bool ConfigurationAlreadyApplied()
    {
        try
        {
            return ConfigurationApplyGuard.MatchesConfiguration(
                File.ReadAllText(_settings.HsModConfigPath, Encoding.UTF8), File.ReadAllText(_settings.ArenaConfigPath, Encoding.UTF8),
                _settings.Mode, _settings.ArenaMode,
                _settings.MaxGamesPerRun, _settings.MaxTotalGames, _settings.StopOnTicketShortage, _settings.StopOnGoldShortage, _settings.DeckName);
        }
        catch { return false; }
    }


    private bool TryReadGameProcessIdentity(out bool running, out int pid, out DateTime startedAtUtc)
    {
        running = false; pid = 0; startedAtUtc = DateTime.MinValue;
        try
        {
            running = IsHearthstoneRunning();
            using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
            if (game == null) return !running;
            if (game.HasExited) return false;
            pid = game.Id;
            startedAtUtc = game.StartTime.ToUniversalTime();
            return true;
        }
        catch { return false; }
    }


    private bool CanRestartBoxForConfiguration()
    {
        if (HearthstoneProcessDiscovery.HasUnverifiedProcess(_settings.HearthstoneDirectory)) return false;
        if (!TryReadGameProcessIdentity(out var running, out var pid, out var startedAt)) return false;
        if (!running) return true;
        var api = _hsModStatus;
        var currentSession = api != null && !_hsModProbeUnavailable && api.Ready && api.LoggedIn &&
            api.GameProcessId == pid && api.GameStartedAtUtc == startedAt &&
            ConfigurationApplyGuard.IsFreshSessionTime(DateTime.UtcNow, startedAt, _lastHsModSuccessAt,
                api.UpdatedAt?.ToUniversalTime());
        if (!currentSession) return false;

        // Unlike the ordinary status display, this action gate requires each
        // native field to be present and correctly typed. Missing defaults
        // and stale snapshots cannot authorize a disruptive recovery action.
        var nativeBoundaryAvailable = TryReadConfigurationArenaBoundary(startedAt, out var nativeGameplay, out var nativeSafe);
        // Constructed mode does not continuously publish arena-runtime-state.
        // After HsMod stops itself on repeated matchmaking timeouts the file
        // can be stale even though the authoritative API already says the
        // session is logged in, script-disabled, and idle.  That combination
        // is a safe Box configuration boundary; still reject the restart if
        // the native activity is unknown or says gameplay is active.
        if (!nativeBoundaryAvailable)
        {
            nativeGameplay = ConfigurationApplyGuard.IsGameplayActivity(api!.Activity);
            nativeSafe = !nativeGameplay && ConstructedStopBoundary.IsKnownSafe(api.Activity, api.ScriptEnabled);
            if (!nativeSafe) return false;
        }
        var gameplay = nativeGameplay || ConfigurationApplyGuard.IsGameplayActivity(api!.Activity);
        try
        {
            if (File.Exists(PreferredHsModLogPath) && File.GetLastWriteTimeUtc(PreferredHsModLogPath) >= startedAt &&
                TryReadConstructedGameplayActive(out var active)) gameplay |= active;
            if (File.Exists(ArenaLogPath) && File.GetLastWriteTimeUtc(ArenaLogPath) >= startedAt)
            {
                bool? arenaGameplay = null;
                using var stream = new FileStream(ArenaLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Contains("entered Arena gameplay", StringComparison.OrdinalIgnoreCase)) arenaGameplay = true;
                    if (line.Contains("returned from Arena gameplay", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Arena state=REWARDS", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Arena state=IN_REWARDS", StringComparison.OrdinalIgnoreCase)) arenaGameplay = false;
                }
                gameplay |= arenaGameplay == true;
            }
        }
        catch { return false; }
        // A running independent script may start a game during the retry.
        // Require its authoritative switch to be off; this gate never turns
        // that script off merely to obtain permission to restart the Box.
        return ConfigurationApplyGuard.MayRestartBox(true, true, currentSession, !api!.ScriptEnabled,
            gameplay, nativeSafe);
    }


    private bool TryReadConfigurationArenaBoundary(DateTime gameStartedUtc, out bool gameplay, out bool safe)
    {
        gameplay = false; safe = false;
        try
        {
            using var stream = new FileStream(ArenaRuntimeStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return ConfigurationApplyGuard.TryClassifyNativeBoundary(reader.ReadToEnd(), DateTime.UtcNow, gameStartedUtc, out gameplay, out safe);
        }
        catch { return false; }
    }


    private void EnsureHsModConfigPresent()
    {
        if (File.Exists(_settings.HsModConfigPath)) return;
        var template = Path.Combine(AppContext.BaseDirectory, "BepInEx", "config", "HsMod.cfg.template");
        if (!File.Exists(template)) return;
        var directory = Path.GetDirectoryName(_settings.HsModConfigPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var pending = _settings.HsModConfigPath + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(template, pending, false);
            File.Move(pending, _settings.HsModConfigPath, true);
            Append("已从安装包模板创建 HsMod.cfg。");
        }
        finally
        {
            try { if (File.Exists(pending)) File.Delete(pending); } catch { }
        }
    }


    private (bool Success, string Message) VerifyInstalledBundle()
    {
        var root = _settings.HearthstoneDirectory;
        var required = new[]
        {
            (Path.Combine(root, "winhttp.dll"), "炉石加载器 winhttp.dll"),
            (Path.Combine(root, "doorstop_config.ini"), "炉石加载器配置"),
            (Path.Combine(root, "BepInEx", "core", "BepInEx.dll"), "BepInEx 核心"),
            (Path.Combine(root, "BepInEx", "plugins", "HsMod.dll"), "HsMod.dll"),
            (Path.Combine(root, "BepInEx", "plugins", "ArenaBoxBot.dll"), "ArenaBoxBot.dll"),
            (Path.Combine(root, "BepInEx", "plugins", "UniverseLib.Mono.dll"), "UniverseLib.Mono.dll")
        };
        var missing = required.Where(x => !File.Exists(x.Item1)).Select(x => x.Item2).ToArray();
        if (missing.Length != 0)
            return (false, "插件安装不完整，缺少：" + string.Join("、", missing) + "。请先在路径配置中点击“安装 / 修复插件”。");
        try
        {
            var box = ResolveBoxExecutable();
            var version = Path.Combine(Path.GetDirectoryName(box)!, "version.dll");
            if (!File.Exists(version)) return (false, "盒子目录缺少 version.dll。请先在路径配置中点击“安装 / 修复插件”。");

            // A release package carries reference hashes next to the
            // controller.  They are informational only: the operator may
            // replace HsMod.dll / version.dll / ArenaBoxBot.dll with any newer
            // build at any time, and a version swap must never stop the control
            // center from loading, monitoring or starting.  Hash drift is
            // logged for diagnosis; the controller keeps working as long as
            // the plugin files exist and the plugin itself is running.
            var packageFiles = new[]
            {
                ("HsMod.dll", Path.Combine(root, "BepInEx", "plugins", "HsMod.dll")),
                ("ArenaBoxBot.dll", Path.Combine(root, "BepInEx", "plugins", "ArenaBoxBot.dll")),
                ("UniverseLib.Mono.dll", Path.Combine(root, "BepInEx", "plugins", "UniverseLib.Mono.dll")),
                ("version.dll", version)
            };
            foreach (var item in packageFiles)
            {
                var expectedPath = Path.Combine(AppContext.BaseDirectory, item.Item1);
                if (!File.Exists(expectedPath)) continue;
                var expected = ComputeSha256(expectedPath);
                var actual = ComputeSha256(item.Item2);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    AppendDeveloper($"{item.Item1} 与中控包内版本不同（{expected[..8]}… vs {actual[..8]}…）；按外部升级处理，不阻止中控运行。");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        return (true, "");
    }


    private void DerivePathsFromHearthstoneRoot(string root)
    {
        _settings.HsModDirectory = Path.Combine(root, "BepInEx", "HsMod");
        _settings.ArenaConfigPath = Path.Combine(root, "BepInEx", "config", "local.hsmod.arenaboxbot.cfg");
        _settings.HsModConfigPath = Path.Combine(root, "BepInEx", "config", "HsMod.cfg");
    }


    private static void SetConfigValue(string path, string key, string value)
    {
        // BepInEx may create the Arena config only after Hearthstone starts;
        // treat a missing file as an empty config and let the normal write
        // create it instead of failing the controller's startup sequence.
        var text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
        var pattern = @"(?m)^(?<prefix>\s*)" + Regex.Escape(key) + @"\s*=.*$";
        if (Regex.IsMatch(text, pattern)) text = Regex.Replace(text, pattern, m => m.Groups["prefix"].Value + key + " = " + value);
        else text += Environment.NewLine + key + " = " + value + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }


    private async Task CompleteNormalGameExitAfterStopAsync(int generation)
    {
        try { await CloseHearthstoneAsync(generation); }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing) Append("脚本已停止；正常退出炉石未完成：" + ex.Message);
        }
        finally
        {
            if (!IsDisposed && !Disposing && generation == _taskGeneration)
            {
                _starting = false;
                LockTaskInputs(false);
                _start.Enabled = true;
                _stop.Enabled = false;
                SetStatus(IsHearthstoneRunning() ? "脚本已停止（炉石尚未退出）" : "已停止（炉石已退出）");
            }
        }
    }


    private static string ReadTail(string path, int lines)
    { try { return string.Join("\n", File.ReadLines(path).TakeLast(lines)); } catch { return ""; } }

    private DurableLogWriter UserLogWriter => _userLogWriter ??= new DurableLogWriter(ControllerLogPath,
        Path.Combine(Path.GetTempPath(), "hsmodPlus", "logs", "controller-" + Environment.ProcessId + "-user.log"));

    private DurableLogWriter DeveloperLogWriter => _developerLogWriter ??= new DurableLogWriter(
        Path.Combine(ControllerDataDirectory, "controller-developer.log"),
        Path.Combine(Path.GetTempPath(), "hsmodPlus", "logs", "controller-" + Environment.ProcessId + "-developer.log"));

}
