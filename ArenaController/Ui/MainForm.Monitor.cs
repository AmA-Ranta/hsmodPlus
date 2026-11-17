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

    private void ScheduleTransientStartupRetry(int generation, string reason)
    {
        var now = DateTime.Now;
        if (_startupRetryCount >= 3 || now - _lastStartupRetryAt < TimeSpan.FromSeconds(30))
        {
            AppendDeveloper("startup-retry.suppressed;attempts=" + _startupRetryCount + ";reason=" + reason);
            return;
        }
        _startupRetryCount++;
        _lastStartupRetryAt = now;
        var attempt = _startupRetryCount;
        // Repeating the Hearthstone launch request against the same stale
        // Battle.net session cannot refresh its SSO token.  After one normal
        // retry, rebuild the official Battle.net session itself at a safe
        // non-gameplay boundary.  This still starts Hearthstone only through
        // Battle.net/Box; it never falls back to Hearthstone.exe directly.
        if (attempt >= 2 && IsAuthenticationStartupFailure(reason) &&
            _battleNetSessionRestartCount < 1 &&
            now - _lastBattleNetSessionRestartAt >= TimeSpan.FromMinutes(2))
        {
            _battleNetSessionRestartCount++;
            _lastBattleNetSessionRestartAt = now;
            Append("连续登录令牌失败，准备在安全边界重建 Battle.net 会话；不会直接启动 Hearthstone.exe。" +
                   "（第 " + _battleNetSessionRestartCount + " 次）");
            _ = RestartBattleNetSessionAsync(generation);
            return;
        }
        Append("登录/初始化暂时失败，保留战网启动链；将在 45 秒后自动重试（第 " + attempt + "/3 次）。");
        _ = RetryTransientStartupAsync(generation);
    }


    private static string ChineseModeName(string mode) => mode switch
    {
        "Standard" => "标准",
        "Wild" => "狂野",
        "Casual" => "休闲",
        "Battlegrounds" => "酒馆战棋",
        "Arena" => "竞技场",
        _ => ""
    };

    /// <summary>
    /// Adopts an externally enabled HsMod script (for example after the controller
    /// was restarted while a task kept running). Without this the UI and the test
    /// harness see an idle window while the game is still being automated.
    /// </summary>
    private bool TryAdoptExternallyEnabledScript()
    {
        if (!IsHearthstoneRunning()) return false;
        if (!IsHsModStatusFresh() || _hsModProbeUnavailable) return false;
        // Right after a controlled stop HsMod can keep reporting the previous
        // enabled state for a few seconds.  Adopting on that stale probe made the
        // window flip between "stopped" and "taken over" and then close the script
        // again, so require a cooldown plus two consecutive fresh confirmations.
        if (DateTime.Now - _lastStopCompletedAt < TimeSpan.FromSeconds(25)) { _externalEnabledSince = DateTime.MinValue; return false; }
        if (_hsModStatus?.ScriptEnabled != true) { _externalEnabledSince = DateTime.MinValue; return false; }
        if (_externalEnabledSince == DateTime.MinValue) { _externalEnabledSince = DateTime.Now; return false; }
        if (DateTime.Now - _externalEnabledSince < TimeSpan.FromSeconds(4)) return false;
        var mode = ChineseModeName(_hsModStatus.Mode);
        if (string.IsNullOrEmpty(mode)) mode = _settings.Mode;
        _running = true;
        _stopRequested = false;
        _stopWaitingForPostGame = false;
        _activeRunMode = mode;
        _baseline = ReadStats();
        _baselineGames = _baseline.TotalWins + _baseline.TotalLosses;
        _baselineRuns = _baseline.RunCount;
        _timer.Interval = 5000;
        _start.Enabled = true;
        _stop.Enabled = true;
        SetStatus("已接管外部开启的脚本（" + mode + "）");
        Append("检测到 HsMod 脚本已在运行但中控没有对应任务（通常是中控重启过），已接管；需要停止请点“停止并等待本局”。");
        return true;
    }

    private void MonitorTick()
    {
        try
        {
            if (++_monitorTickCount == 1)
                Append($"监控已开始；当前时间={DateTime.Now:HH:mm:ss}；计划窗口={IsWithinSchedule()}；启动中={_starting}");
            RefreshStats();
            if (_running && !_stopRequested) ObserveInvalidDeck();
            EnsureBoxOverlayClickThrough();
            if (TryFinishPendingStop()) return;
            // A staged settings change waits for the current match to end so a
            // wrong edit can never alter a live game.
            if (_pendingSettings != null && !_starting && !_reconnecting && !IsMatchInProgress())
                CommitSettings(_pendingSettings, true);
            // A restarted controller must adopt a script the plugin still has on,
            // otherwise the window looks idle ("开始任务" enabled, "停止" disabled)
            // and neither the user nor the harness can stop the running script.
            if (!_running && !_starting && !_awaitingBattleNetLogin && TryAdoptExternallyEnabledScript()) return;
            var withinSchedule = IsWithinSchedule();
            if (!withinSchedule) _scheduleStopLatched = false;
            if (!_running)
            {
                AppendLatestLog();
                if (_awaitingBattleNetLogin && _settings.AutoSchedule && !withinSchedule)
                {
                    RequestStop("到达每日停止时间");
                    return;
                }
                if (_settings.AutoSchedule && withinSchedule && !_scheduleStopLatched && !_starting && !_awaitingBattleNetLogin)
                {
                    _ = StartTaskAsync("每日计划");
                    return;
                }
                // After the bounded startup retries, wait quietly for the
                // official Battle.net UI to finish authentication.  Once its
                // login window changes to an authenticated state, continue
                // through the same Battle.net/Box launch chain automatically;
                // never poll or enter credentials and never launch the game
                // executable directly.
                if (_settings.AutoReconnect && !_starting && !_reconnecting &&
                    !_stopRequested && _awaitingBattleNetLogin &&
                    DateTime.Now - _lastStartupRetryAt >= TimeSpan.FromSeconds(30) &&
                    CanResumeBattleNetPrerequisite())
                {
                    _startupRetryCount = 0;
                    _awaitingBattleNetLogin = false;
                    _lastStartupRetryAt = DateTime.Now;
                    Append("已观察到战网启动条件变化，继续官方启动链并核实炉石实际登录。");
                    _ = StartTaskAsync("Battle.net 登录完成后自动恢复");
                }
                return;
            }
            // The native protocol is also used by external test/maintenance
            // stops. Adopt it before recovery: OFF debounce followed by a
            // quick normal client exit must not resurrect that stopped task.
            if (!IsArenaMode && !_stopRequested && TryObserveExternalConstructedStop()) return;
            if (PollArenaRunStop()) return;
            if (IsArenaMode && File.Exists(InternalStopPath) && !_stopRequested)
            {
                RemoveControlFile(InternalStopPath);
                RequestStop("竞技场插件请求停止");
            }
            if (IsArenaMode && File.Exists(StopAfterGamePath) && !_stopRequested)
                RequestStop("收到外部局后停止请求");
            if (!_starting && !_reconnecting && !_recoveryPending && !_stopRequested && !_awaitingBattleNetLogin && ObserveNativeStop()) return;
            if (_settings.AutoSchedule && !IsWithinSchedule() && !_stopRequested) RequestStop("到达每日停止时间");
            // Evaluate the stop contract before any login/launch wait branch:
            // a pending stop during a client boot ("等待收藏加载") must complete
            // as soon as the script is off, not wait for a login probe.
            if (_stopRequested && (!_stopWaitingForPostGame ||
                (IsArenaMode ? IsPostGameStopComplete() : IsConstructedStopComplete())))
            {
                FinishStop(!IsHearthstoneRunning() ? "炉石已退出，停止任务" :
                    _arenaRoundLimitStopRequested ? "已确认本轮停止边界" : "已确认局后停止边界");
                AppendLatestLog();
                return;
            }
            if (_awaitingBattleNetLogin)
            {
                if (!_stopRequested && _settings.AutoReconnect && !_starting && !_reconnecting &&
                    DateTime.Now - _lastStartupRetryAt >= TimeSpan.FromSeconds(30) && CanResumeBattleNetPrerequisite())
                {
                    _awaitingBattleNetLogin = false;
                    _lastStartupRetryAt = DateTime.Now;
                    Append("战网启动条件已变化，恢复原任务；原场数起点与额度保持不变。");
                    _ = ReconnectHearthstoneAsync();
                }
                return;
            }
            if (!_stopRequested && _settings.AutoReconnect && !_starting && !_reconnecting && DateTime.Now - _startedAt > TimeSpan.FromSeconds(15) && DateTime.Now - _lastReconnectAt > TimeSpan.FromSeconds(30) && (_recoveryPending || !IsHearthstoneRunning()))
                _ = ReconnectHearthstoneAsync();
            if (!_stopRequested && _settings.AutoReconnect && !_starting && !_reconnecting &&
                DateTime.Now - _startedAt > StuckLaunchGrace &&
                DateTime.Now - _lastStuckRecoveryAt > StuckRecoveryCooldown)
            {
                var stuckReason = DetectStuckClient();
                if (!string.IsNullOrWhiteSpace(stuckReason))
                    _ = RecoverStuckClientAsync(stuckReason);
            }
            // A crashed/relaunched client can sit on the FATAL_ERROR screen
            // forever while the process is still alive.  Treat that as a stuck
            // client and restart it through the official launch chain; never
            // interrupt a live match.
            if (!_stopRequested && _settings.AutoReconnect && !_starting && !_reconnecting &&
                DateTime.Now - _lastStuckRecoveryAt > StuckRecoveryCooldown &&
                IsFatalRuntimeScene())
                _ = RecoverStuckClientAsync("炉石停在错误界面");
            // A logged-in client blocked on an intro/announcement popup never
            // reaches the hub; recover it the same way as a stuck client.
            if (!_stopRequested && _settings.AutoReconnect && !_starting && !_reconnecting &&
                DateTime.Now - _lastStuckRecoveryAt > StuckRecoveryCooldown)
            {
                var loginStall = DetectLoginStall();
                if (!string.IsNullOrWhiteSpace(loginStall))
                    _ = RecoverStuckClientAsync(loginStall);
            }
            // A Box overlay that desyncs (for example after a reconnect) keeps
            // its process alive but stops publishing fresh recommendations;
            // every action then waits for advice that never arrives and the
            // turn burns the rope.  Restart only the Box, never the game.
            if (!_stopRequested && _settings.AutoReconnect && _running && !_starting && !_reconnecting &&
                !_awaitingBattleNetLogin && IsHearthstoneRunning() &&
                DateTime.Now - _startedAt > TimeSpan.FromMinutes(2) &&
                DateTime.Now - _lastBoxWatchdogRestartAt > TimeSpan.FromMinutes(2))
            {
                var (heartbeatFresh, heartbeatAge) = ReadHeartbeatStatus(
                    HsModPath(BoxHeartbeatFileName), HsModPath(BoxRecommendationFileName));
                var stateStale = false;
                try
                {
                    if (IsArenaMode && File.Exists(ArenaRuntimeStatePath))
                        stateStale = DateTime.Now - File.GetLastWriteTime(ArenaRuntimeStatePath) > TimeSpan.FromMinutes(2);
                    var recommendationPath = HsModPath(BoxRecommendationFileName);
                    if (!stateStale && File.Exists(recommendationPath))
                        stateStale = DateTime.Now - File.GetLastWriteTime(recommendationPath) > TimeSpan.FromMinutes(2);
                }
                catch { }
                if (_hsModStatus?.ScriptEnabled == true && !heartbeatFresh && stateStale)
                {
                    _lastBoxWatchdogRestartAt = DateTime.Now;
                    Append("盒子推荐已 " + heartbeatAge + " 未更新，正在重启盒子以恢复出牌建议（不影响当前对局）。");
                    AppendDeveloper("box-watchdog.restart;heartbeatFresh=" + heartbeatFresh +
                                    ";heartbeatAge=" + heartbeatAge + ";stateStale=" + stateStale);
                    StopBoxForConfiguration();
                    EnsureBoxRunning();
                }
            }
            var arena = ReadStats();
            var ladder = IsArenaMode ? null : ReadLadderSummary(CurrentLadderMode);
            var games = IsArenaMode ? arena.TotalWins + arena.TotalLosses - _baselineGames :
                ladder?.Reliable == true && _baselineLadderGames.HasValue ? Math.Max(0, ladder.Games - _baselineLadderGames.Value) : 0;
            var runs = arena.RunCount - _baselineRuns;
            // The result counter is written after a game returns from the
            // gameplay scene.  ArenaBoxBot publishes gamesThisRun as soon as
            // gameplay starts, so include an in-flight game in the budget
            // calculation.  This lets the controller write the stop marker
            // during the game and closes the race where the plugin could
            // briefly queue the next match before the five-second monitor
            // tick saw the final result.
            if (IsArenaMode && _settings.MaxTotalGames > 0 &&
                TryReadArenaRuntimeState(out var runtimeBudget) && runtimeBudget.arenaSelected &&
                runtimeBudget.runActive && runtimeBudget.gamesThisRun > 0)
            {
                var completedInCurrentRun = Math.Max(0, arena.CurrentRunWins + arena.CurrentRunLosses);
                var inFlight = Math.Max(0, runtimeBudget.gamesThisRun - completedInCurrentRun);
                games = Math.Max(games, games + inFlight);
            }
            if (!_stopRequested && !_arenaRunStopPending && _settings.MaxTotalGames > 0 && games >= _settings.MaxTotalGames) RequestStop("达到任务总局数");
            if (!_stopRequested && IsArenaMode && _settings.MaxRuns > 0)
            {
                // RunCount is credited on admission, before hero/draft/play.
                // Do not stop that paid round at its draft/MIDRUN boundary.
                // Latch the quota across transient statistics read failures.
                var quotaWasReached = _arenaRunQuotaReached;
                _arenaRunQuotaReached |= arena.SnapshotKnown && runs >= _settings.MaxRuns;
                if (_arenaRunQuotaReached && !quotaWasReached)
                {
                    Append($"已达到新开轮次上限（{runs}/{_settings.MaxRuns}）；继续完成当前轮的全部对战和领奖。");
                    AppendDeveloper($"arena-round-quota reached; baselineRunCount={_baselineRuns}; currentRunCount={arena.RunCount}; runKey={arena.RunKey}; rewardPending={arena.RewardPending}; purchasePending={arena.PurchasePending}");
                }
                if (_arenaRunQuotaReached)
                {
                    _timer.Interval = ArenaRoundLimitStopPolicy.CriticalPollMilliseconds;
                    if (TryReadArenaRuntimeState(out var roundRuntime) &&
                        ArenaRoundLimitStopPolicy.ShouldRequestStop(true, roundRuntime.arenaSelected,
                            arena.RunKey, roundRuntime.runKey,
                            (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - roundRuntime.updatedAtUnixMs) / 1000d,
                            roundRuntime.inGameplay, roundRuntime.arenaState, arena.RunActive,
                            arena.RewardPending, arena.PurchasePending, arena.CompletedRunKey))
                    {
                        _arenaRoundLimitStopRequested = true;
                        RequestStop("达到新开轮数上限，保留本轮完整领奖");
                    }
                }
            }
            if (_stopRequested && (!_stopWaitingForPostGame || (IsArenaMode ? IsPostGameStopComplete() : IsConstructedStopComplete())))
            {
                // Process exit also satisfies the stop policy. It cannot prove
                // that a disconnected match reached its natural result.
                FinishStop(!IsHearthstoneRunning() ? "炉石已退出，停止任务" :
                    _arenaRoundLimitStopRequested ? "已确认本轮停止边界" : "已确认局后停止边界");
            }
            else if (_stopRequested && !IsArenaMode) PublishConstructedStopRequest();
            AppendLatestLog();
        }
        catch (Exception ex)
        {
            // A polling error must not kill the WinForms timer. Keep the
            // controller alive and expose the exact failure in its log.
            Append("监控轮询失败：" + ex.GetType().Name + "：" + ex.Message);
            NativeInstallService.WriteControllerDeveloperLog("MonitorTick", ex);
        }
    }


    /// <summary>
    /// A client can sit in GAMEPLAY with the process still responsive while the
    /// match never progresses (for example a frozen mulligan after a lost
    /// server connection).  The ordinary stuck monitor only reacts to process
    /// or heartbeat failures, so a live-but-frozen match needs its own signal:
    /// the native Power.log must keep growing.  Ten idle minutes is far beyond
    /// any legal rope/mulligan window and never seen in a healthy match.
    /// </summary>
    private string? DetectGameplayStall(Process game)
    {
        try
        {
            if (_stopRequested) { _gameplayStallSince = DateTime.MinValue; return null; }
            var logsRoot = Path.Combine(_settings.HearthstoneDirectory, "Logs");
            if (!Directory.Exists(logsRoot)) return null;
            var session = FindRecentHearthstoneLogSessions(logsRoot, game.StartTime.AddMinutes(-2))
                .OrderByDescending(p => p.StartTime).FirstOrDefault();
            if (session.Directory == null) return null;
            var power = Path.Combine(session.Directory.FullName, "Power.log");
            if (!File.Exists(power)) { _gameplayStallSince = DateTime.MinValue; return null; }
            var idle = DateTime.UtcNow - File.GetLastWriteTimeUtc(power);
            if (idle < TimeSpan.FromMinutes(10)) { _gameplayStallSince = DateTime.MinValue; return null; }
            if (_gameplayStallSince == DateTime.MinValue) { _gameplayStallSince = DateTime.UtcNow; return null; }
            if (DateTime.UtcNow - _gameplayStallSince < TimeSpan.FromSeconds(30)) return null;
            return "对局内超过 10 分钟无游戏进度（Power.log 最后写入于 " +
                   Math.Round(idle.TotalMinutes) + " 分钟前）";
        }
        catch { return null; }
    }


    /// <summary>
    /// A logged-in client can be held on an intro/announcement popup and never
    /// reach the hub.  The script stays enabled and every other signal looks
    /// healthy, so without this check the whole task idles forever at LOGIN.
    /// </summary>
    private string? DetectLoginStall()
    {
        try
        {
            if (_stopRequested || _starting || _reconnecting)
            {
                _loginStallSince = DateTime.MinValue;
                return null;
            }
            if (!TryReadRuntimeSceneFresh(out var scene) ||
                !scene.Equals("LOGIN", StringComparison.OrdinalIgnoreCase))
            {
                _loginStallSince = DateTime.MinValue;
                _loginStallGameId = "";
                return null;
            }
            // The timer belongs to one game process: a client relaunched by a
            // mode switch starts a fresh login window, otherwise the previous
            // client's wait would immediately kill the new one (observed
            // 2026-11-03 23:24, right after a switch restart).
            using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
            var gameId = game == null ? "" : game.Id.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(gameId, _loginStallGameId, StringComparison.Ordinal))
            {
                _loginStallGameId = gameId;
                _loginStallSince = DateTime.UtcNow;
                return null;
            }
            if (_loginStallSince == DateTime.MinValue)
            {
                _loginStallSince = DateTime.UtcNow;
                return null;
            }
            if (DateTime.UtcNow - _loginStallSince < TimeSpan.FromMinutes(8)) return null;
            return "登录后超过 8 分钟未进入大厅（可能被弹窗阻塞）";
        }
        catch { return null; }
    }


    private string? DetectStuckClient()
    {
        try
        {
            using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
            if (game == null) return null;
            var boxRunning = IsBoxRunning();
            var heartbeat = ReadHeartbeatStatus(HsModPath(BoxHeartbeatFileName), HsModPath(BoxRecommendationFileName));
            var runtimeFresh = IsArenaMode ? IsArenaRuntimeFresh() : IsHsModStatusFresh();
            if (!_networkHistoryPrimed)
            {
                _networkHistoryPrimed = true;
                if (TryReadUnrecoveredNetworkFailure() &&
                    !(IsArenaMode ? IsArenaGameplayActive() : IsConstructedGameplayActive()))
                {
                    _connectionRecovery.PrimeNetworkFailure(DateTime.Now);
                    AppendDeveloper("connection-recovery.primed-from-log-history;boundary=safe");
                }
            }
            if (IsHsModStatusFresh() && !_hsModProbeUnavailable && _hsModStatus?.ScriptEnabled == false)
            {
                // A failed automatic recovery can leave a fresh HsMod snapshot
                // logged in with the script disabled.  That is different from
                // a user turning the script off: _recoveryPending is retained
                // by RecoverStuckClientAsync when startup/login failed.  Do not
                // let this state become a permanent "waiting" condition; retry
                // the official launch chain at the normal recovery cooldown,
                // while still preserving the manual-stop behavior otherwise.
                if (_recoveryPending &&
                    (!IsArenaMode && !IsConstructedGameplayActive() ||
                     IsArenaMode && !IsArenaGameplayActive()))
                {
                    var startupFailure = ReadHearthstoneStartupFailure(game.StartTime);
                    if (!string.IsNullOrWhiteSpace(startupFailure))
                        return "恢复后脚本未开启：" + startupFailure;
                    var pendingNetwork = _connectionRecovery.RecoveryReason(DateTime.Now);
                    return "恢复后脚本未开启，当前不在对局中" +
                           (string.IsNullOrWhiteSpace(pendingNetwork) ? "" : "：" + pendingNetwork);
                }
                _clientRecovery.Reset(); _arenaProgress.Reset(); _connectionRecovery.Reset(); return null;
            }
            var recoveryScene = ReadFreshRecoveryScene(game);
            var disconnected = _connectionRecovery.RecoveryReason(DateTime.Now,
                string.Equals(recoveryScene, "FATAL_ERROR", StringComparison.OrdinalIgnoreCase),
                !IsArenaMode && (string.Equals(recoveryScene, "TOURNAMENT", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(recoveryScene, "HUB", StringComparison.OrdinalIgnoreCase)));
            if (disconnected != null)
            {
                // Never close a live match just because its server session is
                // unhealthy. Hold the evidence until the native gameplay
                // boundary is over, then recover from the fatal/rewards scene.
                var gameplayActive = IsArenaMode ? IsArenaGameplayActive() : IsConstructedGameplayActive();
                if (gameplayActive)
                {
                    if (DateTime.Now - _lastConnectionRecoveryPendingNotice > TimeSpan.FromSeconds(30))
                    {
                        _lastConnectionRecoveryPendingNotice = DateTime.Now;
                        AppendDeveloper("connection-recovery.pending;reason=" + disconnected + ";boundary=gameplay");
                    }
                    return null;
                }
                return disconnected;
            }
            if (IsArenaMode && IsArenaGameplayActive() || !IsArenaMode && IsConstructedGameplayActive())
            {
                var gameplayStall = DetectGameplayStall(game);
                if (gameplayStall != null) return gameplayStall;
                return _clientRecovery.ObserveGameplay(DateTime.Now, game.Responding, runtimeFresh);
            }
            // Fresh API/heartbeat only proves the local bridge is alive. Arena
            // scene/slot progress provides a separate signal outside gameplay.
            if (IsArenaMode && TryReadArenaRuntimeState(out var runtime))
            {
                if (!runtime.runtimeEnabled)
                { _clientRecovery.Reset(); _arenaProgress.Reset(); return null; }
                var progress = _arenaProgress.Observe(DateTime.Now, _running, _stopRequested,
                    !_arenaRuntimeReadUnavailable, runtime.runtimeEnabled, runtime.inGameplay,
                    runtime.scene, runtime.arenaState, runtime.slot, runtime.maxSlot, runtime.gamesThisRun, runtime.runKey + "|" + runtime.progressKey);
                if (progress != null) return progress;
                // Do not kill a live game for a missing local read/Qt window.
                if (runtime.inGameplay || runtime.scene.Equals("GAMEPLAY", StringComparison.OrdinalIgnoreCase))
                {
                    var gameplayStall = DetectGameplayStall(game);
                    if (gameplayStall != null) return gameplayStall;
                    return _clientRecovery.ObserveGameplay(DateTime.Now, game.Responding, runtimeFresh);
                }
            }
            else if (!IsArenaMode && IsHsModStatusFresh() && !_hsModProbeUnavailable && _hsModStatus?.ScriptEnabled == false)
            { _clientRecovery.Reset(); return null; }
            return _clientRecovery.Observe(DateTime.Now, game.Responding, boxRunning,
                heartbeat.Fresh, runtimeFresh, IsArenaMode, IsNativeLogFreshlyActive());
        }
        catch (Exception ex)
        {
            NativeInstallService.WriteControllerDeveloperLog("DetectStuckClient", ex);
        }
        return null;
    }


    private bool IsArenaRuntimeFresh()
    {
        try
        {
            return File.Exists(ArenaRuntimeStatePath) &&
                   DateTime.UtcNow - File.GetLastWriteTimeUtc(ArenaRuntimeStatePath) < StuckRuntimeWindow;
        }
        catch { return false; }
    }


    private bool IsHsModStatusFresh()
    {
        var status = _hsModStatus;
        return status != null && status.Ready && DateTime.UtcNow - _lastHsModSuccessAt <= StuckRuntimeWindow &&
               (!status.UpdatedAt.HasValue || DateTime.UtcNow - status.UpdatedAt.Value.ToUniversalTime() <= StuckRuntimeWindow);
    }


    /// <summary>
    /// Reads the Box bridge timestamp without touching or changing any
    /// runtime state.  LastWriteTime is only a fallback for old bridge files;
    /// current heartbeat files carry an explicit Unix-millisecond timestamp.
    /// </summary>
    private static (bool Fresh, string Age) ReadHeartbeatStatus(string heartbeatPath, string recommendationPath)
    {
        try
        {
            if (File.Exists(heartbeatPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(heartbeatPath, Encoding.UTF8));
                if (document.RootElement.TryGetProperty("ts", out var value))
                {
                    long timestamp = 0;
                    if (value.ValueKind == JsonValueKind.Number) value.TryGetInt64(out timestamp);
                    else if (value.ValueKind == JsonValueKind.String) long.TryParse(value.GetString(), out timestamp);
                    // A few legacy producers wrote seconds instead of ms.
                    if (timestamp > 0 && timestamp < 100000000000L) timestamp *= 1000;
                    if (timestamp > 0)
                    {
                        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
                        var seconds = Math.Max(0, (int)age.TotalSeconds);
                        var text = age.TotalSeconds < 60 ? $"{seconds} 秒前" : $"{(int)age.TotalMinutes} 分钟前";
                        var fresh = age.TotalSeconds >= -5 && age.TotalSeconds <= 45;
                        if (fresh && document.RootElement.TryGetProperty("pid", out var boxPid) && boxPid.TryGetInt32(out var producerPid) && producerPid > 0)
                            fresh = IsNormalBoxProcess(producerPid);
                        if (fresh && document.RootElement.TryGetProperty("gamePid", out var gamePid) && gamePid.TryGetInt32(out var producerGamePid) && producerGamePid > 0)
                            fresh = Process.GetProcessesByName("Hearthstone").Any(p => p.Id == producerGamePid && !p.HasExited);
                        return (fresh, text);
                    }
                }
            }
        }
        catch { }

        // Recommendation files from older Box versions do not always carry a
        // timestamp.  Their write time still gives a useful, conservative
        // fallback, while a missing file remains clearly visible.
        if (File.Exists(recommendationPath))
            return (IsFresh(recommendationPath, 45), FileAgeText(recommendationPath));
        if (File.Exists(heartbeatPath))
            return (IsFresh(heartbeatPath, 45), FileAgeText(heartbeatPath));
        return (false, "未找到");
    }


    private string DetectScriptRuntime(bool gameRunning)
    {
        if (_reconnecting) return "正在重连";
        if (IsArenaMode)
        {
            var arena = ReadStats();
            if (TryReadArenaRuntimeState(out var runtime))
            {
                if (!runtime.runtimeEnabled && !runtime.inGameplay) return "竞技场脚本已关闭";
                if (runtime.inGameplay) return "竞技场对局中";
                if (!string.IsNullOrWhiteSpace(runtime.arenaState)) return "竞技场：" + runtime.arenaState;
                if (runtime.scene == "DRAFT") return "竞技场：等待页面状态";
                if (runtime.scene == "GAMEPLAY") return "竞技场：等待对局识别";
                if (runtime.runtimeEnabled) return "竞技场脚本运行中";
            }
            if (_lastArenaRuntime != null && _lastArenaRuntimePath == ArenaRuntimeStatePath &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastArenaRuntime.updatedAtUnixMs < 45000)
                return "竞技场状态暂不可读（最近：" + (_lastArenaRuntime.runtimeEnabled ? "运行中" : "已关闭") + "）";
            if (File.Exists(EnabledPath) && gameRunning) return _running ? "竞技场插件运行中" : "竞技场插件已开启";
            return gameRunning ? "等待插件状态确认" : "未运行";
        }

        // /api/status is the authoritative HsMod runtime signal.  The log
        // and watchdog sidecars below remain as fallbacks for older builds or
        // while the game is still loading and the web endpoint is unavailable.
        var apiStatus = _hsModStatus;
        var apiStatusFresh = apiStatus != null && apiStatus.Ready && gameRunning && !_hsModProbeUnavailable &&
            DateTime.UtcNow - _lastHsModSuccessAt < TimeSpan.FromSeconds(30) &&
            (!apiStatus.UpdatedAt.HasValue || DateTime.UtcNow - apiStatus.UpdatedAt.Value.ToUniversalTime() < TimeSpan.FromSeconds(30));
        if (apiStatusFresh && apiStatus != null)
        {
            if (!apiStatus.PluginEnabled) return "HsMod 未启用";
            if (apiStatus.ScriptEnabled) return _running ? "HsMod 脚本运行中" : "HsMod 已开启";
            return _running ? "HsMod 已连接，脚本未开启" : "HsMod 已连接，脚本未开启";
        }
        if (gameRunning && _hsModProbeUnavailable) return "HsMod 状态暂不可读";
        if (IsConstructedScriptEnabled()) return _running ? "HsMod 脚本运行中" : "HsMod 已开启";
        return _running ? (gameRunning ? "等待 HsMod 状态" : "等待炉石恢复") : "未检测到开启";
    }


    private void RefreshRuntimeStatusFromProbe()
    {
        if (IsDisposed || Disposing) return;
        // A probe may arrive between timer ticks.  Reuse the same renderer so
        // the three-line monitor card does not lose the heartbeat line.
        RefreshRuntimeStatus();
    }


    private void SetWatchdogAutoResume(bool enabled, bool strict = false)
    {
        try
        {
            if (!strict && !Directory.Exists(_settings.HsModDirectory)) return;
            string[] files;
            try { files = Directory.GetFiles(_settings.HsModDirectory, "autoresume*.json", SearchOption.TopDirectoryOnly); }
            catch (DirectoryNotFoundException) when (TryReadGameProcessIdentity(out var running, out _, out _) && !running) { return; }
            var paths = files.Where(p => Path.GetFileName(p).Contains("auto-1", StringComparison.OrdinalIgnoreCase));
            foreach (var path in paths)
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                if (strict)
                {
                    try
                    {
                        ConfigurationApplyGuard.WriteWatchdogLease(json, enabled,
                            value => File.WriteAllText(path, value, new UTF8Encoding(false)),
                            () => File.ReadAllText(path, Encoding.UTF8));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("看门狗控制文件未能写入并校验：" + path + "。" + ex.Message, ex);
                    }
                }
                else
                {
                    json = Regex.Replace(json, @"(""restartEnabled""\s*:\s*)(true|false)", "$1" + (enabled ? "true" : "false"), RegexOptions.IgnoreCase);
                    json = Regex.Replace(json, @"(""scriptActive""\s*:\s*)(true|false)", "$1" + (enabled ? "true" : "false"), RegexOptions.IgnoreCase);
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                }
            }
        }
        catch (Exception ex)
        {
            if (strict) throw new InvalidOperationException("看门狗控制状态未能写入并确认，启动或恢复未完成。" + ex.Message, ex);
            Append("看门狗状态写入失败：" + ex.Message);
        }
    }

}
