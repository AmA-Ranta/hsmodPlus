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

    private async Task StartTaskAsync(string reason)
    {
        if (_starting) return;
        if (_running && HasStaleRunState())
        {
            // The window can be left "running" after the plugin already stopped:
            // script off, no match, no pending stop. Every later start would be
            // treated as a queued switch and never run, so recover here instead.
            Append("检测到残留运行状态（脚本已关闭且无进行中对局），已重置任务状态并直接开始。");
            _running = false;
            _activeRunMode = "";
            _stopRequested = false;
            _stopWaitingForPostGame = false;
            _arenaSettledOffSinceUtc = null;
            RemoveControlFile(StopAfterGamePath);
            SetStatus("就绪");
        }
        if (ControllerModeSwitchPolicy.ShouldQueueStart(_running, _starting))
        {
            // Never drop the request: queue a mode switch to the latest settings.
            QueueModeSwitchStart(reason);
            return;
        }
        // 每日计划开启且当前不在计划窗口时，手动点击开始应直接拒绝；否则会
        // 先开脚本、几秒后又被计划停掉，表现为“点了开始立刻停”。
        if (_settings.AutoSchedule && !IsWithinSchedule() &&
            !string.Equals(reason, "每日计划", StringComparison.Ordinal) &&
            !string.Equals(reason, "战网会话重建", StringComparison.Ordinal))
        {
            Append($"当前时间不在每日计划窗口（{_settings.StartTime}-{_settings.StopTime}），未启动任务；请调整计划或关闭每日计划。");
            SetStatus("不在每日计划窗口");
            return;
        }
        // Starting from the stopped state applies staged edits first: no live
        // match can be affected here.
        if (_settingsDirty) CommitSettings(CaptureUiSettings(), false);
        CancelArenaRunStop();
        RemoveControlFile(ArenaRunStopReceiptPath);
        if (!string.Equals(reason, "每日计划", StringComparison.Ordinal))
            _scheduleStopLatched = false;
        _starting = true; _stopRequested = false;
        _awaitingBattleNetLogin = false;
        _battleNetLoginWait.Reset();
        // Settings stay editable while the task runs; edits only take effect
        // after 应用设置 (and after the current match ends).
        LockTaskInputs(false);
        _start.Enabled = false; _stop.Enabled = true;
        var taskGeneration = ++_taskGeneration;
        SaveSettings();
        var scriptEnabledByController = false;
        try
        {
            if (!_settings.Mode.Equals("标准", StringComparison.Ordinal) &&
                !_settings.Mode.Equals("狂野", StringComparison.Ordinal) &&
                !_settings.Mode.Equals("休闲", StringComparison.Ordinal) &&
                !_settings.Mode.Equals("酒馆战棋", StringComparison.Ordinal) &&
                !_settings.Mode.Equals("竞技场", StringComparison.Ordinal))
                throw new InvalidOperationException("请选择标准、狂野、休闲、酒馆战棋或竞技场模式。");
            var resolvedGameRoot = NativeInstallService.ResolveGameRootPath(_settings.HearthstoneDirectory);
            if (resolvedGameRoot == null)
            {
                // Re-run the full discovery pipeline for a failed start so the
                // support file contains the same evidence as installation:
                // normalized input, parent/child probes, access errors and
                // running-process paths.  The old generic exception log only
                // recorded the input string and could not explain why a real
                // Hearthstone directory was rejected.
                var discovery = new NativeInstallService().Discover(new NativeInstallRequest
                {
                    HearthstoneDirectory = _settings.HearthstoneDirectory,
                    BoxExecutablePath = _settings.BoxExecutablePath
                });
                var diagnostic = discovery.DeveloperLogPath ?? NativeInstallService.WriteControllerDeveloperLog(
                    "StartTaskAsync/ResolveGameRoot",
                    new DirectoryNotFoundException("未找到 Hearthstone.exe；输入=" + _settings.HearthstoneDirectory));
                var failureReason = discovery.PathFailureReason ?? "请检查路径拼写和目录权限。";
                throw new InvalidOperationException("找不到炉石安装目录。" + failureReason +
                    (string.IsNullOrWhiteSpace(diagnostic) ? "" : " 详细诊断已保存到：" + diagnostic));
            }
            _settings.HearthstoneDirectory = resolvedGameRoot;
            DerivePathsFromHearthstoneRoot(_settings.HearthstoneDirectory);
            EnsureHsModConfigPresent();
            if (!File.Exists(_settings.HsModConfigPath)) throw new InvalidOperationException("HsMod 配置不存在，安装包中也没有 HsMod.cfg.template；请重新解压完整安装包后再试。");
            var bundle = VerifyInstalledBundle();
            if (!bundle.Success) throw new InvalidOperationException(bundle.Message);
            if (IsArenaMode && !HasNativeArenaPatch())
                throw new InvalidOperationException("竞技场原生模式尚未安装，请先在“路径配置”点击“安装 / 修复插件”。");
            // The controller owns the launch decision.  HsMod must never be
            // asked to launch or resurrect Hearthstone through its
            // `-hsmodinstance` watchdog path: that path can start the client
            // before Battle.net has established a valid session, which leaves
            // the game stuck offline.  Disable its lease before every launch
            // and let Hearthstone/Battle.net start normally.
            SetWatchdogAutoResume(false, strict: true);
            // HsMod may hold its config exclusively even while the authenticated
            // client is safely idle.  Refresh the authoritative API snapshot
            // before deciding whether a Box restart is safe; otherwise startup
            // sees an unknown status and blocks the recovery that would release
            // the lock.
            await RefreshHsModStatusForStartAsync().ConfigureAwait(true);
            ApplyConfigurationWithBoxRetry();
            var arena = ReadStats(); var ladder = ReadLadderSummary(CurrentLadderMode);
            _baseline = arena; _baselineGames = arena.TotalWins + arena.TotalLosses; _baselineRuns = arena.RunCount;
            if (IsArenaMode && _settings.MaxRuns > 0 && !arena.SnapshotKnown)
                throw new InvalidOperationException("竞技场轮数记录暂不可读，无法确认本任务的新入场起点；脚本尚未开启。");
            if (IsArenaMode && _settings.MaxRuns > 0)
            {
                Append($"竞技场新开轮次上限={_settings.MaxRuns}；完成最后一个新轮的全部对战和领奖后停止。");
                AppendDeveloper($"arena-round-quota baselineRunCount={_baselineRuns}; maxNewRuns={_settings.MaxRuns}; startingRunKey={arena.RunKey}; snapshotKnown={arena.SnapshotKnown}");
            }
            _arenaRunQuotaReached = _arenaRoundLimitStopRequested = false;
            _timer.Interval = 5000;
            _baselineLadderGames = ladder.Reliable ? ladder.Games : null;
            if (!IsArenaMode && _settings.MaxTotalGames > 0 && !_baselineLadderGames.HasValue)
                throw new InvalidOperationException("当前战绩文件暂不可读或正在更新，无法确认任务局数起点；请稍后重试。脚本尚未开启。");
            RemoveControlFile(StopAfterGamePath); RemoveControlFile(InternalStopPath); RemoveControlFile(EnabledPath);
            RemoveControlFile(ConstructedStopRequestPath); RemoveControlFile(ConstructedStopReceiptPath);
            _constructedStopRequest = null;
            using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
            if (game == null)
            {
                if (IsHearthstoneRunning())
                    throw new InvalidOperationException("炉石窗口或进程身份暂不可确认，已保留当前进程；未重复启动或关闭它。");
                // Do not repeatedly send launch commands to a Battle.net
                // login window.  The command is harmless, but it creates a
                // misleading retry loop and can look like a failed game
                // restart.  MonitorTick will resume this same official
                // launch path as soon as the user finishes authentication.
                if (HasBattleNetLoginWindow())
                    throw new BattleNetAuthenticationRequiredException();
                LaunchHearthstoneViaBattleNet();
                Append("已发出官方启动请求，等待当前炉石进程完成登录…");
                // Do not treat a process handle as proof of a usable client.
                // WaitForHearthstoneProcessAsync also checks the current
                // session's login log and reports a stale Battle.net token.
                if (!await WaitForHearthstoneAfterLaunchAsync(TimeSpan.FromSeconds(45), 3))
                    throw new TimeoutException("启动请求和重试后仍未确认炉石登录成功，未开启脚本。");
            }
            else
            {
                // A raw Hearthstone.exe can be alive while its Battle.net
                // session is absent.  Attaching to that process makes later
                // reconnects ineffective, so reject it before HsMod/script
                // activation and require an official launch chain.
                if (!ProcessLaunchChain.TryGetAuthorizedOwner(game.Id, out var owner, out var ownerPid))
                    throw new InvalidOperationException(
                        "检测到炉石不是由 Battle.net/盒子启动（当前父进程=" +
                        (string.IsNullOrWhiteSpace(owner) ? "未知" : owner) +
                        ", PID=" + ownerPid + "）。已拒绝接管；请退出炉石后通过战网或炉石盒子启动。\n" +
                        "中控不会直接启动 Hearthstone.exe。" );
                Append($"检测到已运行的炉石进程，父进程为 {owner} (PID {ownerPid})；复用官方启动链。");
            }

            // HsMod reads the ladder mode when the Hearthstone session is
            // created.  Reusing a live process after changing HsMod.cfg leaves
            // the native runtime in the old mode forever (the config says
            // Standard while /api/status still says Wild), so the deck gate
            // waits until its timeout.  At a proven non-gameplay boundary,
            // restart the client through the authenticated Battle.net/Box
            // chain so the requested mode is applied by a fresh HsMod session.
            await EnsureNativeModeSessionAsync(taskGeneration).ConfigureAwait(true);
            // Do not send any HsMod/script command until the game process is
            // present. This keeps manual starts and controller starts on the
            // same path and avoids racing Battle.net authentication.
            EnsureBoxRunning();
            if (_settings.RequireBoxHeartbeat)
            {
                SetStatus("等待盒子信息");
                if (!await WaitForBoxReadyAsync(TimeSpan.FromMinutes(3))) throw new InvalidOperationException("3 分钟内未检测到盒子心跳/推荐文件，未开启脚本。");
            }
            // Configuration is written before the game and box are launched.
            // Once HsMod is loaded it may keep HsMod.cfg open, so writing the
            // same file again after the heartbeat can fail with a misleading
            // sharing violation.  The pre-launch write above is authoritative
            // for both native HsMod settings and the ArenaBoxBot sidecar.
            // Arena is a native HsMod mode after installation.  The marker is
            // kept as a controller lease for stop-after-game/reconnect, while
            // the actual script switch remains HsMod's own F12/API state.
            // HsMod's native mode and scriptRuntimeEnabled are authoritative
            // for every mode, including Arena.  The old arena-bot.enabled
            // lease was a controller-only switch and made Arena stop as soon
            // as the controller closed; do not create it for new runs.
            // Any start path must begin from the main lobby: never enable the script
            // on a settlement page, a draft screen or in the middle of a mode switch.
            await EnsureAtMainLobbyAsync(taskGeneration).ConfigureAwait(true);
            scriptEnabledByController = await ScriptActivationGate.EnableAsync(EnsureConfiguredConstructedDeckAsync,
                () => ThrowIfTaskInterrupted(taskGeneration), EnableConstructedScript);
            _clientRecovery.Reset();
            _arenaProgress.Reset();
            _nativeDisabledSince = null;
            _startupRetryCount = 0;
            _lastStartupRetryAt = DateTime.MinValue;
            _battleNetSessionRestartCount = 0;
            _lastBattleNetSessionRestartAt = DateTime.MinValue;
            _recoveryPending = false;
            _running = true; _stopRequested = false; _activeRunMode = _settings.Mode; _startedAt = DateTime.Now;
            try { using var startedGame = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory); if (startedGame != null) WriteSessionModeBinding(_settings.Mode, startedGame); } catch { } _start.Enabled = true; _stop.Enabled = true; SetStatus("运行中（" + _settings.Mode + "）"); Append(reason + "：已开启" + _settings.Mode + "脚本");
        }
        catch (OperationCanceledException)
        {
            if (scriptEnabledByController) DisableConstructedScript();
            Append("启动已取消，未继续开启脚本。");
            _scheduleStopLatched = true;
            SetStatus("已停止");
        }
        catch (BattleNetAuthenticationRequiredException ex)
        {
            // This is a prerequisite wait, not a startup failure.  Keep the
            // bounded retry threshold reached so MonitorTick can resume
            // automatically when the Battle.net window becomes authenticated.
            if (scriptEnabledByController) DisableConstructedScript();
            PauseForBattleNetPrerequisite(ex.Message);
            _scheduleStopLatched = false;
        }
        catch (Exception ex)
        {
            if (scriptEnabledByController) DisableConstructedScript();
            Append("启动失败：" + ex.Message);
            var diagnosticPath = NativeInstallService.WriteControllerDeveloperLog("StartTaskAsync", ex);
            if (!string.IsNullOrWhiteSpace(diagnosticPath)) Append("开发者诊断日志：" + diagnosticPath);
            SetStatus("启动失败");
            RemoveControlFile(EnabledPath);
            // A scheduled failure is usually a stable prerequisite problem
            // such as an uninstalled or mismatched plugin.  Do not retry it
            // every five seconds and flood the log; a manual start clears the
            // latch, and leaving the daily window clears it for the next run.
            if (string.Equals(reason, "每日计划", StringComparison.Ordinal))
            {
                _scheduleStopLatched = true;
                Append("本次每日计划已暂停；修复安装后可手动开始，或等待下一计划窗口。");
            }
            else if (ShouldRetryTransientStartup(ex) && _settings.AutoReconnect)
            {
                ScheduleTransientStartupRetry(taskGeneration, ex.Message);
            }
        }
        finally
        {
            _starting = false;
            if (!_running) LockTaskInputs(false);
            // While a task runs, 开始任务 means "switch to the applied mode after
            // this match" - it must stay clickable instead of silently disabled.
            if (_running && !_starting) _start.Enabled = true;
            else _start.Enabled = !_starting;
            _start.Text = _running ? "开始 / 切换" : "开始任务";
            _stop.Enabled = _running || _awaitingBattleNetLogin; RefreshStats();
            if (!_running) _ = RefreshDeckSuggestionsAsync();
        }
    }


    private void ThrowIfTaskInterrupted(int generation)
    {
        if (generation != _taskGeneration || _stopRequested)
            throw new OperationCanceledException("任务已请求停止，取消尚未完成的启动或恢复。");
    }


    /// <summary>Reads the plugin heartbeat and says whether it is fresh.</summary>
    private bool TryReadHeartbeatState(out string scene, out bool fresh)
    {
        scene = "";
        fresh = false;
        if (!TryReadSceneHeartbeat(out var heartbeatScene, out _, out var updatedAtUnixMs)) return false;
        scene = heartbeatScene;
        fresh = ControllerLobbyPolicy.IsHeartbeatFresh(updatedAtUnixMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return true;
    }

    private async Task RestartClientForModeSwitchAsync(int taskGeneration, string mode)
    {
        // Never skip the second wait when the heartbeat says a match is live (or was
        // seen in the last few seconds): that shortcut closed the client mid-game.
        if (ControllerModeSwitchPolicy.ShouldSkipSecondMatchWait(_switchBoundaryConfirmed) &&
            !HeartbeatBlocksStopBoundary())
        {
            // The queued switch was released by the stop contract at a proven
            // post-game boundary. Waiting again would leave the script off
            // while HsMod auto-queues the next match.
            _switchBoundaryConfirmed = false;
        }
        else
        {
            await WaitForLiveMatchToEndAsync(taskGeneration).ConfigureAwait(true);
        }
        ThrowIfTaskInterrupted(taskGeneration);
        await CloseHearthstoneAsync(taskGeneration).ConfigureAwait(true);
        ThrowIfTaskInterrupted(taskGeneration);
        await Task.Delay(3000).ConfigureAwait(true);
        ThrowIfTaskInterrupted(taskGeneration);
        SetWatchdogAutoResume(false, strict: true);
        EnsureBoxRunning();
        LaunchHearthstoneViaBattleNet();
        Append("已按新模式重新启动炉石，等待官方登录会话…");
        var launchedAfterSwitch = await WaitForHearthstoneAfterLaunchAsync(TimeSpan.FromSeconds(90), 3).ConfigureAwait(true);
        if (!launchedAfterSwitch)
        {
            // A client that crashed on boot sits on the fatal-error screen; a slow
            // Battle.net hand-off can leave the launch chain unverified while the
            // client is healthy. Both used to fail the whole task.
            var bootHeartbeatAvailable = TryReadHeartbeatState(out var bootScene, out var bootHeartbeatFresh);
            var recovery = ControllerLaunchRecoveryPolicy.Decide(bootHeartbeatAvailable, bootHeartbeatFresh, bootScene);
            if (recovery == LaunchRecoveryAction.RestartClient)
            {
                Append("客户端启动后停在致命错误页（FATAL_ERROR），重启客户端后重试。");
                await CloseHearthstoneAsync(taskGeneration).ConfigureAwait(true);
                ThrowIfTaskInterrupted(taskGeneration);
                await Task.Delay(3000).ConfigureAwait(true);
                EnsureBoxRunning();
                LaunchHearthstoneViaBattleNet();
                Append("已重新启动炉石，等待官方登录会话…");
                if (!await WaitForHearthstoneAfterLaunchAsync(TimeSpan.FromSeconds(90), 3).ConfigureAwait(true))
                    throw new TimeoutException("模式切换后炉石两次都未完成登录；未开启脚本，避免在错误模式下运行。");
            }
            else if (recovery == LaunchRecoveryAction.Continue)
            {
                Append("启动链未核实，但场景心跳正常（客户端已运行），继续核验模式。");
            }
            else
            {
                throw new TimeoutException("模式切换后炉石未在预期时间内完成登录；未开启脚本，避免在错误模式下运行。");
            }
        }
        ThrowIfTaskInterrupted(taskGeneration);
        using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
        if (game != null) WriteSessionModeBinding(mode, game);
        // Never enable the script on an unverified session: poll HsMod until it
        // reports the expected mode (Arena is projected by the plugin bridge).
        var expectedRuntime = HsModModeMapping.ToRuntime(mode);
        var verified = false;
        for (var attempt = 0; attempt < 24 && !verified; attempt++)
        {
            ThrowIfTaskInterrupted(taskGeneration);
            var status = await ProbeHsModStatusAsync().ConfigureAwait(true);
            if (status != null && status.Ready && status.LoggedIn && !string.IsNullOrWhiteSpace(status.Mode))
            {
                verified = string.Equals(status.Mode, expectedRuntime, StringComparison.OrdinalIgnoreCase) ||
                    (string.Equals(mode, "竞技场", StringComparison.Ordinal) &&
                     (string.Equals(status.Mode, "Arena", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(status.Mode, "Standard", StringComparison.OrdinalIgnoreCase)));
                if (verified) Append("模式切换已核验：HsMod 会话=" + status.Mode + "（目标=" + mode + "）");
            }
            if (!verified) await Task.Delay(2500).ConfigureAwait(true);
        }
        if (!verified)
            throw new InvalidOperationException("模式切换后未能确认 HsMod 会话已进入 " + mode + "；为避免在错误模式下运行，未开启脚本。");
        Append("模式切换完成：客户端已按 " + mode + " 重建会话。");
    }


    private async Task EnsureNativeModeSessionAsync(int taskGeneration)
    {
        // HsMod reads the ladder mode when the Hearthstone session is created, and
        // its /api/status mode is a projection (Arena is reported as soon as the
        // plugin owns the session), so it cannot prove which mode the live session
        // is really running. Each session is therefore bound to the mode it was
        // started with: same process + start time + mode means no restart; anything
        // else is rebuilt here, at a proven non-gameplay boundary.
        var expectedMode = _settings.Mode;
        using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
        if (game == null) return; // The launch below creates a fresh session.
        var binding = TryReadSessionModeBinding();
        if (binding != null &&
            string.Equals(binding.Mode, expectedMode, StringComparison.Ordinal) &&
            binding.GameProcessId == game.Id &&
            Math.Abs((binding.GameStartedAtUtc - game.StartTime.ToUniversalTime()).TotalSeconds) < 5)
            return;
        var nativeStatus = await ProbeHsModStatusAsync().ConfigureAwait(true);
        var configWrite = SafeGetLastWriteTime(_settings.HsModConfigPath);
        var startedAfterConfig = !configWrite.HasValue ||
            game.StartTime.ToUniversalTime() > configWrite.Value.ToUniversalTime().AddSeconds(1);
        if (nativeStatus != null && nativeStatus.Ready && nativeStatus.LoggedIn &&
            string.Equals(nativeStatus.Mode, HsModModeMapping.ToRuntime(expectedMode), StringComparison.OrdinalIgnoreCase) &&
            startedAfterConfig)
        {
            WriteSessionModeBinding(expectedMode, game);
            return;
        }
        ThrowIfTaskInterrupted(taskGeneration);
        Append("会话模式与目标不一致（目标=" + expectedMode + "）；按边界自动重启客户端以应用新模式…");
        await RestartClientForModeSwitchAsync(taskGeneration, expectedMode).ConfigureAwait(true);
    }


    private void EnsureBoxRunning()
    {
        var box = ResolveBoxExecutable();
        if (FindNormalBoxProcesses(box).Count > 0)
        {
            Append("检测到炉石传说盒子进程，复用当前盒子。");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(box) { WorkingDirectory = Path.GetDirectoryName(box) ?? AppContext.BaseDirectory, UseShellExecute = true });
            Append("已启动炉石传说盒子，等待盒子心跳…");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"盒子启动失败：{box}\n{ex.GetType().Name}: {ex.Message}", ex);
        }
    }


    private string ResolveBoxExecutable()
    {
        var candidates = new[]
        {
            _settings.BoxExecutablePath,
            Path.Combine(AppContext.BaseDirectory, "HSAng.exe"),
            @"D:\炉石传说盒子\HSAng.exe",
            @"C:\炉石传说盒子\HSAng.exe",
            @"C:\Program Files\炉石传说盒子\HSAng.exe",
            @"C:\Program Files (x86)\炉石传说盒子\HSAng.exe"
        };
        string? path = null;
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) { path = candidate; break; }
            if (Directory.Exists(candidate))
            {
                var inDirectory = Path.Combine(candidate, "HSAng.exe");
                if (File.Exists(inDirectory)) { path = inDirectory; break; }
            }
        }
        if (path == null)
            throw new InvalidOperationException("未找到炉石传说盒子 HSAng.exe。请在“盒子位置”中指定 HSAng.exe 文件或其所在目录。");
        _settings.BoxExecutablePath = Path.GetFullPath(path);
        return _settings.BoxExecutablePath;
    }


    private void RequestStop(string reason)
    {
        // An explicit user stop must beat a queued mode switch even though the
        // queue already set _stopRequested (so the early return below would
        // otherwise swallow this click and keep the switch armed).
        if (_pendingModeSwitchStart && !ControllerModeSwitchPolicy.IsSwitchDrivenStop(reason))
        {
            _pendingModeSwitchStart = false;
            _pendingModeSwitchReason = "";
            Append("已取消排队中的模式切换；本次只停止任务，不会自动切换模式。");
        }
        if ((!_running && !_starting && !_awaitingBattleNetLogin) || _stopRequested) return;
        CancelArenaRunStop();
        var wasWaitingForLogin = _awaitingBattleNetLogin;
        _awaitingBattleNetLogin = false;
        _battleNetLoginWait.Reset();
        _stopRequested = true;
        _stopRequestedAt = DateTime.Now;
        _recoveryPending = false;
        ++_taskGeneration;
        if (wasWaitingForLogin && !_running && !_starting)
        {
            _stopRequested = false;
            _scheduleStopLatched = true;
            LockTaskInputs(false);
            _start.Enabled = true; _stop.Enabled = false;
            SetStatus("已停止");
            Append(reason + "：已取消等待战网登录，不会自动继续启动。");
            return;
        }
        _clientRecovery.Reset();
        _arenaProgress.Reset();
        _arenaStopLogEvidence = null;
        _arenaStopLogReader = null;
        if (IsArenaMode)
        {
            try
            {
                using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
                if (game != null)
                {
                    _arenaStopLogEvidence = new ArenaStopLogEvidence(_stopRequestedAt, game.StartTime, game.Id);
                    _arenaStopLogReader = new NativeLogReader();
                    _arenaStopLogReader.Read(ArenaLogPath); // Start after existing records, including older task completions.
                }
            }
            catch { /* No session identity means no log-only stop authorization. */ }
        }
        if (_starting && !_running)
        {
            _scheduleStopLatched = true;
            SetStatus("正在取消启动");
            Append(reason + "：取消启动，等待当前检查结束。");
            return;
        }
        if (!IsArenaMode)
        {
            if (!IsHearthstoneRunning())
            {
                FinishStop(reason);
                return;
            }
            _stopWaitingForPostGame = true;
            PublishConstructedStopRequest();
            var stopSummary = ReadLadderSummary(CurrentLadderMode);
            _stopRequestedLadderGames = stopSummary.Reliable ? stopSummary.Games : null;
            SetStatus("等待对局结算或空闲边界");
            Append(reason + "：保留当前状态，等待对局结算或明确空闲状态后停止");
            return;
        }
        // A budget stop can arrive during the post-game reward page, after
        // the gameplay log has already closed. Keep the marker alive through
        // REWARDS/IN_REWARDS as well as GAMEPLAY.
        var active = IsArenaGameplayActive() || IsArenaRewardsPending();
        if (active)
        {
            // Keep the enable marker during gameplay. ArenaBoxBot consumes
            // this marker and removes the enable file only after returning
            // from GAMEPLAY, so a stop request never abandons the current
            // game or leaves the plugin without its final result event.
            _stopWaitingForPostGame = true;
            PublishControllerCommand(ControllerCommandContract.ActionStop, ControllerCommandContract.BoundaryAfterMatch, reason); SetStatus("等待本局结束"); Append(reason + "：当前对局结束后停止");
        }
        else if (IsArenaStopBoundaryConfirmed())
        {
            _stopWaitingForPostGame = false;
            RemoveControlFile(EnabledPath);
            FinishStop(reason);
        }
        else
        {
            // A missing or stale runtime snapshot is not evidence that no
            // game exists. Keep the stop request and let the plugin publish a
            // fresh safe boundary before closing the script or Hearthstone.
            _stopWaitingForPostGame = true;
            PublishControllerCommand(ControllerCommandContract.ActionStop, ControllerCommandContract.BoundaryAfterMatch, reason);
            SetStatus("等待竞技场状态确认");
            Append(reason + "：暂时无法确认竞技场安全边界，保持等待，不会强行停止");
        }
    }


    private void FinishStop(string reason)
    {
        // NO_RUN can be published just after the server reward ACK, before
        // the native receipt observer has persisted the round completion.
        // Every Arena stop path must retain that observer and the entry hold
        // until the durable reward/purchase/entry facts are settled. A page
        // transition alone must not release the switch or close the client.
        if (IsArenaMode && IsHearthstoneRunning())
        {
            var factsKnown = TryReadArenaStopTransactionFacts(out var rewardsPending,
                out var purchasePending, out var entryPhase);
            if (!NativeArenaStopPolicy.CanFinish(true, factsKnown, rewardsPending, purchasePending, entryPhase))
            {
                _stopRequested = true;
                _stopWaitingForPostGame = true;
                PublishControllerCommand(ControllerCommandContract.ActionStop, ControllerCommandContract.BoundaryAfterMatch, reason);
                SetStatus("等待竞技场领奖与交易登记");
                return;
            }
        }
        // Always release HsMod's real runtime switch before clearing the
        // controller lease.  This is required for Arena, too: the plugin
        // uses the same native switch and may have requested a stop itself.
        if (IsHearthstoneRunning() && !DisableConstructedScript())
        {
            _stopRequested = true;
            SetStatus("等待确认脚本停止");
            return;
        }
        // arena-runtime-state.json belongs to the plugin: it is the plugin's
        // own live status feed and must never be rewritten by the control
        // center.  The controller only removes its own command markers here;
        // the stopped state is observed from the plugin's next publication (or
        // from the absent plugin), never fabricated locally.
        _arenaRunQuotaReached = _arenaRoundLimitStopRequested = false;
        _timer.Interval = 5000;
        RemoveControlFile(ConstructedStopRequestPath); RemoveControlFile(ConstructedStopReceiptPath);
        _constructedStopRequest = null;
        RemoveControlFile(StopAfterGamePath); RemoveControlFile(InternalStopPath); RemoveControlFile(EnabledPath); _lastStopCompletedAt = DateTime.Now; _running = false; _stopRequested = false; _stopWaitingForPostGame = false; _activeRunMode = ""; _awaitingBattleNetLogin = false; _battleNetLoginWait.Reset(); LockTaskInputs(false); _start.Enabled = true; _stop.Enabled = false; SetStatus("已停止");
        Append(reason + (_settings.CloseGameAfterStop ? "：脚本已停止；等待炉石正常退出。" : "：脚本已停止；现在可以安全切换模式后重新开始"));
        _scheduleStopLatched = true;
        if (_pendingModeSwitchStart)
        {
            // The boundary arrived: start again with whatever mode is applied now.
            _pendingModeSwitchStart = false;
            if (_settingsDirty) CommitSettings(CaptureUiSettings(), false);
            else if (_pendingSettings != null) CommitSettings(_pendingSettings, false);
            var queuedReason = _pendingModeSwitchReason;
            _pendingModeSwitchReason = "";
            _switchBoundaryConfirmed = true;
            Append("模式切换排队完成，准备以最新设置重新开始：" + _settings.Mode);
            _ = StartTaskAsync(queuedReason + "（模式切换）");
            return;
        }
        if (_settings.CloseGameAfterStop)
        {
            // Keep a delayed normal shutdown from blocking the UI or allowing
            // a new task to race the still-exiting client.
            _starting = true;
            LockTaskInputs(true);
            _start.Enabled = false;
            SetStatus("脚本已停止，等待炉石正常退出");
            _ = CompleteNormalGameExitAfterStopAsync(_taskGeneration);
        }
    }


    private async Task<bool> WaitForHearthstoneProcessAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !_stopRequested)
        {
            using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
            if (game != null)
            {
                // A raw Hearthstone.exe can look alive while Battle.net has
                // not authenticated it. Never count that process as a
                // successful recovery; the same owner check is required
                // before script activation in StartTaskAsync.
                if (!ProcessLaunchChain.TryGetAuthorizedOwner(game.Id, out var launchOwner, out var launchOwnerPid))
                {
                    AppendDeveloper("launch-chain.pending;gamePid=" + game.Id + ";owner=" +
                        (string.IsNullOrWhiteSpace(launchOwner) ? "unknown" : launchOwner) +
                        ";ownerPid=" + launchOwnerPid);
                    await Task.Delay(1000);
                    continue;
                }
                // Bind login evidence to the actual process creation time.
                // A retry window may start after Login.log was written.
                var launchStarted = game.StartTime;
                var failure = ReadHearthstoneStartupFailure(launchStarted);
                if (!string.IsNullOrWhiteSpace(failure))
                    throw new InvalidOperationException("炉石启动后无法登录战网：" + failure + "。请先在战网完成登录，再重试；未开启脚本。 ");

                // Login.log is written asynchronously and can lag behind the
                // actual authenticated session. Prefer the HsMod status
                // endpoint when it already proves that this exact Hearthstone
                // PID is ready and logged in; otherwise a healthy Battle.net
                // launch was incorrectly timed out during mode switches.
                var native = await ProbeHsModStatusAsync().ConfigureAwait(true);
                if (native != null && native.Ready && native.LoggedIn && native.GameProcessId == game.Id)
                    return true;

                // Elapsed time and a process handle cannot prove login.
                if (HasHearthstoneLoggedIn(launchStarted))
                    return true;
            }
            if (game == null && !IsHearthstoneRunning() && HasBattleNetLoginWindow())
                throw new BattleNetAuthenticationRequiredException();
            await Task.Delay(1000);
        }
        return false;
    }


    private void LaunchHearthstoneViaBattleNet()
    {
        if (!IsHearthstoneRunning() && HasBattleNetLoginWindow())
            throw new BattleNetAuthenticationRequiredException();
        var battleNet = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net", "Battle.net.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battle.net", "Battle.net.exe")
        }.FirstOrDefault(File.Exists);
        if (battleNet != null)
        {
            // Let the official client establish the Blizzard session before
            // Hearthstone starts. Directly launching Hearthstone.exe can show
            // a false login failure even when the desktop client is logged in;
            // this is the same path as clicking the game's Start button.
            Process.Start(new ProcessStartInfo(battleNet, "--exec=\"launch WTCG\"")
            {
                WorkingDirectory = Path.GetDirectoryName(battleNet)!,
                UseShellExecute = true
            });
            Append("已通过战网请求启动炉石，等待官方登录会话…");
            return;
        }

        // Never start Hearthstone.exe (or the old Beta Launcher) directly.
        // Those processes bypass the authenticated Battle.net/Box launch
        // chain and can leave the controller unable to recover the session.
        // EnsureBoxRunning is called by every recovery path before this
        // method; if Battle.net is unavailable, let the Box own the launch
        // and let WaitForHearthstoneProcessAsync verify it.
        var box = ResolveBoxExecutable();
        if (FindNormalBoxProcesses(box).Count > 0)
        {
            Append("未找到 Battle.net.exe，交由已运行的炉石盒子启动游戏；不会直接启动 Hearthstone.exe。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(box)
            {
                WorkingDirectory = Path.GetDirectoryName(box) ?? AppContext.BaseDirectory,
                UseShellExecute = true
            });
            Append("未找到 Battle.net.exe，已启动炉石盒子请求启动游戏；不会直接启动 Hearthstone.exe。");
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "未找到 Battle.net.exe，且炉石盒子无法启动；已阻止直接启动 Hearthstone.exe。请先登录战网或检查盒子路径。",
                ex);
        }
    }


    private async Task<bool> WaitForHearthstoneAfterLaunchAsync(TimeSpan attemptWindow, int attempts)
    {
        for (var attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            if (await WaitForHearthstoneProcessAsync(attemptWindow).ConfigureAwait(true))
            {
                using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
                if (game != null && ProcessLaunchChain.TryGetAuthorizedOwner(game.Id, out var owner, out var ownerPid))
                {
                    Append($"启动链已核实：Hearthstone PID {game.Id} 由 {owner} PID {ownerPid} 启动。");
                    return true;
                }
                Append("启动请求创建了炉石进程，但未核实 Battle.net/盒子父进程；拒绝开启脚本。");
            }
            if (attempt >= attempts) break;
            if (_stopRequested) return false;
            if (!IsHearthstoneRunning())
            {
                Append($"官方启动请求尚未创建炉石进程，第 {attempt} 次重试。");
                LaunchHearthstoneViaBattleNet();
            }
            else Append("炉石进程已创建，继续等待本次登录确认；尚未恢复脚本。");
        }
        if (!_stopRequested && !IsHearthstoneRunning())
            throw new BattleNetAuthenticationRequiredException("官方启动与本轮重试仍未创建炉石进程，请检查战网登录或连接。");
        return false;
    }


    private bool IsBoxRunning()
    {
        try
        {
            return FindNormalBoxProcesses(_settings.BoxExecutablePath).Count > 0;
        }
        catch { return false; }
    }


    private static List<Process> FindNormalBoxProcesses(string executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(name)) name = "HSAng";
        var expected = Path.GetFullPath(executablePath);
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                // Presence is separate from bridge readiness. Qt may expose
                // HWND 0 or the title Dialog even while collecting normally.
                if (process.HasExited) continue;
                // MainModule can be inaccessible when a 32-bit Qt process is
                // inspected from this 64-bit controller. The visible main
                // process name is sufficient here; an inaccessible
                // path must not make the controller spawn a new Box every poll.
                try
                {
                    var actual = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(actual) &&
                        !string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                catch { /* keep the process when Windows denies MainModule */ }
                result.Add(process);
            }
            catch { }
        }
        return result;
    }


    private void StopBoxForConfiguration()
    {
        var name = Path.GetFileNameWithoutExtension(_settings.BoxExecutablePath);
        var expectedDirectory = GetExecutableDirectory(_settings.BoxExecutablePath);
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                if (!IsProcessFromDirectory(process, expectedDirectory)) continue;
                process.CloseMainWindow();
                if (!process.WaitForExit(10000)) process.Kill(true);
            }
            catch (Exception ex) { Append("重启盒子前关闭旧进程失败：" + ex.Message); }
        }
        // HSAng can leave its helper alive after the window closes. It owns
        // the old heartbeat and can make the next Box instance look stale.
        foreach (var process in Process.GetProcessesByName("HSAExternal"))
        {
            try { if (IsProcessFromDirectory(process, expectedDirectory) && !process.HasExited) process.Kill(true); }
            catch (Exception ex) { Append("关闭盒子辅助进程失败：" + ex.Message); }
        }
    }


    private async Task CloseHearthstoneAsync(int generation)
    {
        // HsMod's watchdog reads these sidecars after the game exits.  Turn
        // that lease off before closing so a scheduled stop is not followed
        // by an unexpected automatic relaunch.
        SetWatchdogAutoResume(false);
        using var process = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
        if (process == null)
        {
            if (IsHearthstoneRunning())
                throw new InvalidOperationException("无法确认炉石进程身份，未关闭或强制结束任何同名进程。");
            return;
        }
        var fatalScene = IsFatalRuntimeScene();
        if (!process.CloseMainWindow() && !process.HasExited)
        {
            // A crashed client can sit on the FATAL_ERROR screen whose window
            // does not accept WM_CLOSE.  The match/run is already lost in that
            // state, so a forced end is the only way to recover unattended.
            if (fatalScene || IsFatalRuntimeScene())
            {
                Append("炉石停在错误界面且不接受正常退出，强制结束进程（PID " + process.Id + "）…");
                try { process.Kill(true); } catch { /* fall through to the normal wait */ }
            }
            else
            {
                throw new InvalidOperationException("炉石未接受正常退出请求，已保留进程并停止本次重启。");
            }
        }
        Append("已发送炉石退出请求，等待确认进程退出（PID " + process.Id + "）…");
        try
        {
            await NormalProcessExit.WaitAsync(() => process.HasExited,
                () => generation != _taskGeneration || _stopRequested, TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException) when (!process.HasExited && (fatalScene || IsFatalRuntimeScene()))
        {
            // The crashed client can accept WM_CLOSE yet never finish exiting.
            // That used to stall recovery for the full two minutes and then
            // fail, leaving the FATAL_ERROR client alive and the bot idle.
            Append("炉石停在错误界面且 2 分钟内未退出，强制结束进程（PID " + process.Id + "）…");
            try { process.Kill(true); } catch { /* verified below */ }
            await NormalProcessExit.WaitAsync(() => process.HasExited,
                () => generation != _taskGeneration, TimeSpan.FromSeconds(20));
        }
        ThrowIfTaskInterrupted(generation);
        Append("炉石已正常退出（PID " + process.Id + "）。");
    }

}
