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

    private static bool ShouldRetryTransientStartup(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("等待炉石收藏卡组超过", StringComparison.Ordinal) ||
               message.Contains("无法通过暴雪战网服务进行登录", StringComparison.Ordinal) ||
               message.Contains("启动请求和重试后仍未确认炉石登录成功", StringComparison.Ordinal) ||
               message.Contains("登录令牌", StringComparison.Ordinal) ||
               message.Contains("GenerateSSOToken", StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsAuthenticationStartupFailure(string reason)
    {
        return reason.Contains("等待炉石收藏卡组超过", StringComparison.Ordinal) ||
               reason.Contains("暴雪战网服务进行登录", StringComparison.Ordinal) ||
               reason.Contains("登录令牌", StringComparison.Ordinal) ||
               reason.Contains("GenerateSSOToken", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("Failed to get token", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("repeated token", StringComparison.OrdinalIgnoreCase);
    }


    private async Task RestartBattleNetSessionAsync(int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            if (generation != _taskGeneration || _stopRequested || _running || _starting) return;
            var gameplay = IsArenaMode ? IsArenaGameplayActive() : IsConstructedGameplayActive();
            // Constructed log files can retain a historical Gameplay.Awake
            // marker after the client has returned to the lobby. Prefer the
            // fresh HsMod activity snapshot for this recovery gate; it is the
            // same authoritative signal used by the stop-boundary checks.
            if (!IsArenaMode && IsHsModStatusFresh() && !_hsModProbeUnavailable &&
                ConstructedStopBoundary.IsKnownSafe(_hsModStatus?.Activity, _hsModStatus?.ScriptEnabled == true))
                gameplay = false;
            if (gameplay)
            {
                Append("战网会话重建已延后：当前检测到对局，不能中断本局。");
                return;
            }

            _recoveryPending = true;
            SetStatus("重建战网登录会话");
            Append("已确认不在对局中，关闭旧炉石/盒子和 Battle.net 主会话，随后只通过官方战网重新启动。 ");
            SetWatchdogAutoResume(false, strict: true);
            await CloseHearthstoneAsync(generation);
            StopBoxForConfiguration();
            StopStaleBoxAtSafeBoundary();
            CloseBattleNetForSessionReset();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline &&
                   (IsHearthstoneRunning() ||
                    Process.GetProcessesByName("HSAng").Any() ||
                    Process.GetProcessesByName("HSAExternal").Any() ||
                    Process.GetProcessesByName("Battle.net").Any()))
            {
                if (generation != _taskGeneration || _stopRequested) return;
                await Task.Delay(250).ConfigureAwait(true);
            }

            if (Process.GetProcessesByName("Battle.net").Any())
                throw new TimeoutException("旧 Battle.net 会话未退出，未继续启动，避免复用失效登录令牌。");
            Append("旧 Battle.net 会话已退出，重新调用官方启动链。");
            await StartTaskAsync("战网会话重建").ConfigureAwait(true);
        }
        catch (OperationCanceledException) { Append("战网会话重建已取消。"); }
        catch (Exception ex)
        {
            Append("战网会话重建失败：" + ex.GetType().Name + "：" + ex.Message);
            NativeInstallService.WriteControllerDeveloperLog("RestartBattleNetSessionAsync", ex);
        }
    }


    private void CloseBattleNetForSessionReset()
    {
        var battleNetRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battle.net")
        }.Where(Directory.Exists).ToArray();
        foreach (var process in Process.GetProcessesByName("Battle.net"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path) ||
                    !battleNetRoots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                    continue;
                process.CloseMainWindow();
                if (process.WaitForExit(10000)) continue;
                // This is only reached after Hearthstone/Box have been closed
                // and the caller has proven a non-gameplay boundary.  A stale
                // hidden Battle.net process otherwise prevents the next
                // official launch from obtaining a fresh token.
                if (!Process.GetProcessesByName("Hearthstone").Any())
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                    Append("旧 Battle.net 窗口未自行退出，已在非对局安全边界结束旧会话。");
                }
            }
            catch (Exception ex) { Append("关闭旧 Battle.net 会话失败：" + ex.Message); }
        }
    }


    private void StopStaleBoxAtSafeBoundary()
    {
        // StopBoxForConfiguration intentionally keeps a same-named process
        // when Windows denies MainModule access. During this recovery path we
        // have already closed Hearthstone and verified a non-gameplay state,
        // so those stale Box processes must not block Battle.net teardown.
        if (Process.GetProcessesByName("Hearthstone").Any()) return;
        foreach (var processName in new[] { "HSAng", "HSAExternal" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.HasExited) continue;
                    process.Kill(true);
                    process.WaitForExit(5000);
                    Append("已在非对局安全边界结束残留炉石盒子进程：" + processName + "。");
                }
                catch (Exception ex) { Append("结束残留炉石盒子进程失败：" + ex.Message); }
            }
        }
    }


    private async Task RetryTransientStartupAsync(int generation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45)).ConfigureAwait(true);
            if (generation != _taskGeneration || _stopRequested || _running || _starting) return;
            await StartTaskAsync("启动重试").ConfigureAwait(true);
        }
        catch (Exception retryEx)
        {
            AppendDeveloper("startup-retry.exception=" + retryEx.GetType().Name + ":" + retryEx.Message);
        }
    }


    private static bool HasBattleNetLoginWindow()
        => BattleNetLoginProbe.Read() == BattleNetLoginObservation.Required;


    private bool CanResumeBattleNetPrerequisite()
        => _battleNetLoginWait.TryResume(BattleNetLoginProbe.Read(), IsHearthstoneRunning());


    private void PauseForBattleNetPrerequisite(string reason)
    {
        // Prerequisite wait is not a native stop and does not reset an active
        // task's baseline/quota. A concurrently created game still goes through
        // the original process-owner and real login checks on resume.
        _startupRetryCount = Math.Max(_startupRetryCount, 3);
        _awaitingBattleNetLogin = true;
        _battleNetLoginWait.Pause(BattleNetLoginProbe.Read(), BattleNetLoginProbe.LastKnown);
        _lastStartupRetryAt = DateTime.Now;
        SetStatus("等待战网登录 / 连接");
        Append(reason + " 已暂停重复启动请求；完成登录或连接恢复后继续原启动链，不会代为操作登录窗口。");
    }


    private async Task ReconnectHearthstoneAsync()
    {
        if (_reconnecting || !_running || _stopRequested) return;
        _reconnecting = true;
        _recoveryPending = true;
        var taskGeneration = _taskGeneration;
        var recovered = false;
        var requestBattleNetSessionReset = false;
        _lastReconnectAt = DateTime.Now;
        SetStatus("炉石掉线，正在重连");
        var processAlive = IsHearthstoneRunning();
        Append(processAlive
            ? "检测到炉石会话不可用（进程仍在），准备重启炉石并继续任务…"
            : "检测到 Hearthstone.exe 已退出，开始自动重连…");
        try
        {
            // Reconnect is also controller-owned.  Never re-enable the HsMod
            // watchdog or pass its instance flag here; doing so can bypass the
            // Battle.net session and reproduce the offline launch failure.
            SetWatchdogAutoResume(false, strict: true);
            if (processAlive && IsFatalRuntimeScene() &&
                !(IsArenaMode ? IsArenaGameplayActive() : IsConstructedGameplayActive()))
            {
                Append("炉石停在错误界面，先正常退出再重连…");
                await CloseHearthstoneAsync(taskGeneration);
            }
            if (!IsHearthstoneRunning()) LaunchHearthstoneViaBattleNet();
            // Battle.net/Box is the launch owner.  Start/attach the Box after
            // requesting the official game launch so recovery cannot race two
            // independent game starters.
            EnsureBoxRunning();
            if (!await WaitForHearthstoneAfterLaunchAsync(TimeSpan.FromSeconds(30), 3))
                throw new TimeoutException("90 秒内未重新检测到 Hearthstone.exe");
            ThrowIfTaskInterrupted(taskGeneration);
            await WaitForRecoveredNativeSceneAsync(taskGeneration, TimeSpan.FromSeconds(45));
            Append("炉石进程已恢复，等待盒子信息…");
            if (_settings.RequireBoxHeartbeat && !await WaitForBoxReadyAsync(TimeSpan.FromSeconds(90)))
                Append("炉石已重连，但暂未收到新的盒子心跳；脚本保持等待，不会盲目运行。");
            else
            {
                // A new Hearthstone process can appear before the HsMod API
                // snapshot has caught up. Refresh it before the config-lock
                // guard decides whether a Box restart is safe; otherwise a
                // valid lobby recovery is reported as an unsafe sharing lock
                // and a second reconnect is triggered unnecessarily.
                await RefreshHsModStatusForStartAsync().ConfigureAwait(true);
                ApplyConfigurationWithBoxRetry();
                // Recheck readiness after applying configuration. A confirmed
                // sharing lock may have required a safe Box restart.
                if (_settings.RequireBoxHeartbeat && !await WaitForBoxReadyAsync(TimeSpan.FromSeconds(90)))
                    throw new TimeoutException("盒子已重启，但 90 秒内未收到新的盒子心跳/推荐文件，脚本保持关闭。");
                await ScriptActivationGate.EnableAsync(EnsureConfiguredConstructedDeckAsync,
                    () => ThrowIfTaskInterrupted(taskGeneration), EnableConstructedScript);
                _clientRecovery.Reset(); _arenaProgress.Reset(); _startedAt = DateTime.Now;
                recovered = true;
                _recoveryPending = false;
                Append("炉石与盒子已恢复，脚本继续运行。");
            }
        }
        catch (OperationCanceledException) { Append("已按停止请求取消自动重连。"); }
        catch (BattleNetAuthenticationRequiredException ex) { PauseForBattleNetPrerequisite(ex.Message); }
        catch (Exception ex)
        {
            Append("炉石自动重连失败：" + ex.GetType().Name + "：" + ex.Message);
            NativeInstallService.WriteControllerDeveloperLog("ReconnectHearthstoneAsync", ex);
            // A collection/login timeout means the current Battle.net SSO
            // session is unusable.  Re-launching Hearthstone alone just
            // reuses that stale token and repeats the same timeout forever.
            // Defer the full session rebuild until this reconnect finally
            // releases its guard; the rebuild itself runs only at a fresh
            // non-gameplay boundary.
            requestBattleNetSessionReset = _settings.AutoReconnect &&
                IsAuthenticationStartupFailure(ex.ToString());
        }
        finally
        {
            _reconnecting = false;
            if (_running && !_stopRequested && !_awaitingBattleNetLogin) SetStatus(recovered ? "运行中（" + _settings.Mode + "）" : "恢复未完成，等待重试");
            if (requestBattleNetSessionReset && _running && !_stopRequested)
                _ = RebuildBattleNetSessionWhileRunningAsync(taskGeneration, "炉石自动重连登录/收藏初始化失败");
        }
    }


    private async Task RebuildBattleNetSessionWhileRunningAsync(int generation, string reason)
    {
        var now = DateTime.Now;
        if (_reconnecting || !_running || _stopRequested || generation != _taskGeneration) return;
        if (_battleNetSessionRestartCount >= 2 || now - _lastBattleNetSessionRestartAt < TimeSpan.FromMinutes(2))
        {
            AppendDeveloper("battle-net-session-reset.suppressed;attempts=" + _battleNetSessionRestartCount + ";reason=" + reason);
            return;
        }

        _battleNetSessionRestartCount++;
        _lastBattleNetSessionRestartAt = now;
        _reconnecting = true;
        _recoveryPending = true;
        var recovered = false;
        try
        {
            Append("检测到战网登录/收藏初始化失败，准备在非对局边界重建 Battle.net 会话；不会直接启动 Hearthstone.exe。");
            SetStatus("重建战网登录会话");
            SetWatchdogAutoResume(false, strict: true);
            DisableConstructedScript();

            if (IsConstructedGameplayActive())
            {
                Append("战网会话重建已延后：当前重新检测到对局，不能中断本局。");
                return;
            }

            await CloseHearthstoneAsync(generation);
            StopBoxForConfiguration();
            StopStaleBoxAtSafeBoundary();
            if (BattleNetLoginProbe.Read() != BattleNetLoginObservation.Ready)
                throw new BattleNetAuthenticationRequiredException("未能确认战网已登录可启动，已保留当前战网窗口，不重建正在等待的登录会话。");
            CloseBattleNetForSessionReset();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(35);
            while (DateTime.UtcNow < deadline &&
                   (IsHearthstoneRunning() ||
                    Process.GetProcessesByName("HSAng").Any() ||
                    Process.GetProcessesByName("HSAExternal").Any() ||
                    Process.GetProcessesByName("Battle.net").Any()))
            {
                ThrowIfTaskInterrupted(generation);
                await Task.Delay(250).ConfigureAwait(true);
            }

            if (Process.GetProcessesByName("Battle.net").Any())
                throw new TimeoutException("旧 Battle.net 会话未退出，未继续启动，避免复用失效登录令牌。");

            Append("旧 Battle.net 会话已退出，重新调用官方启动链。");
            LaunchHearthstoneViaBattleNet();
            EnsureBoxRunning();
            if (!await WaitForHearthstoneAfterLaunchAsync(TimeSpan.FromSeconds(45), 3))
                throw new TimeoutException("战网会话重建后 135 秒内未确认炉石登录成功。");
            ThrowIfTaskInterrupted(generation);
            await RefreshHsModStatusForStartAsync().ConfigureAwait(true);
            ApplyConfigurationWithBoxRetry();
            if (_settings.RequireBoxHeartbeat && !await WaitForBoxReadyAsync(TimeSpan.FromSeconds(90)))
                throw new TimeoutException("战网会话重建后 90 秒内未收到盒子心跳/推荐文件。");
            await ScriptActivationGate.EnableAsync(EnsureConfiguredConstructedDeckAsync,
                () => ThrowIfTaskInterrupted(generation), EnableConstructedScript);
            _clientRecovery.Reset();
            _arenaProgress.Reset();
            _startedAt = DateTime.Now;
            _recoveryPending = false;
            recovered = true;
            Append("Battle.net 会话已重建，炉石由官方启动链重新登录，脚本继续运行。");
        }
        catch (OperationCanceledException)
        {
            Append("已按停止请求取消 Battle.net 会话重建。");
        }
        catch (BattleNetAuthenticationRequiredException ex) { PauseForBattleNetPrerequisite(ex.Message); }
        catch (Exception ex)
        {
            Append("Battle.net 会话重建失败：" + ex.GetType().Name + "：" + ex.Message);
            NativeInstallService.WriteControllerDeveloperLog("RebuildBattleNetSessionWhileRunningAsync", ex);
        }
        finally
        {
            _reconnecting = false;
            if (_running && !_stopRequested && !_awaitingBattleNetLogin)
                SetStatus(recovered ? "运行中（" + _settings.Mode + "）" : "恢复未完成，等待重试");
        }
    }


    /// <summary>
    /// Cheap "the game is still writing its native log" probe.  Used only to
    /// suppress restarting a healthy but busy client whose local status
    /// endpoint is temporarily unresponsive.
    /// </summary>
    private bool IsNativeLogFreshlyActive()
    {
        try
        {
            return File.Exists(PreferredHsModLogPath) &&
                   DateTime.Now - File.GetLastWriteTime(PreferredHsModLogPath) <= TimeSpan.FromSeconds(60);
        }
        catch { return false; }
    }


    /// <summary>
    /// A killed/crashed client can be relaunched into the FATAL_ERROR screen
    /// with the process still alive.  Wait for a usable native scene and fail
    /// fast on the error screen so the monitor can close and relaunch again.
    /// </summary>
    private async Task WaitForRecoveredNativeSceneAsync(int generation, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfTaskInterrupted(generation);
            if (IsFatalRuntimeScene())
                throw new TimeoutException("炉石重连后停在错误界面（FATAL_ERROR），准备再次重启。");
            if (TryReadRuntimeSceneFresh(out var scene) &&
                (scene.Equals("HUB", StringComparison.OrdinalIgnoreCase) ||
                 scene.Equals("TOURNAMENT", StringComparison.OrdinalIgnoreCase) ||
                 scene.Equals("COLLECTION", StringComparison.OrdinalIgnoreCase) ||
                 scene.Equals("GAMEPLAY", StringComparison.OrdinalIgnoreCase)))
                return;
            await Task.Delay(1000);
        }
    }


    private string? ReadFreshRecoveryScene(Process game)
    {
        try
        {
            if (_hsModStatus == null || _hsModProbeUnavailable || !TryReadArenaRuntimeState(out var runtime))
                return null;
            // A logged-in API is not proof of a healthy connection. Bind the
            // fresh scene publication to this actual process, including its
            // creation time, before retaining a silent network-fatal sequence.
            var gameStartedUtc = game.StartTime.ToUniversalTime();
            var apiFresh = IsHsModStatusFresh() && _hsModStatus.GameStartedAtUtc == gameStartedUtc;
            var runtimeUpdatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(runtime.updatedAtUnixMs).UtcDateTime;
            var fatal = ConnectionRecoveryMonitor.IsCurrentFatalScene(game.Id, _hsModStatus.GameProcessId,
                apiFresh, true, runtime.inGameplay, runtime.scene, runtimeUpdatedUtc, gameStartedUtc);
            var idle = !IsArenaMode && ConnectionRecoveryMonitor.IsCurrentIdleScene(game.Id, _hsModStatus.GameProcessId,
                apiFresh, true, runtime.inGameplay, runtime.scene, runtimeUpdatedUtc, gameStartedUtc);
            return fatal || idle ? runtime.scene : null;
        }
        catch { return null; }
    }


    private async Task RecoverStuckClientAsync(string reason)
    {
        if (_reconnecting || !_running || _stopRequested) return;
        _reconnecting = true;
        _recoveryPending = true;
        var taskGeneration = _taskGeneration;
        var recovered = false;
        _lastReconnectAt = DateTime.Now;
        _lastStuckRecoveryAt = DateTime.Now;
        _stuckRecoveryCount++;
        SetStatus("炉石卡住，正在重启");
        Append("检测到炉石疑似卡住（" + reason + "），正在关闭炉石和盒子并恢复任务…");
        try
        {
            DisableConstructedScript();
            SetWatchdogAutoResume(false, strict: true);
            await CloseHearthstoneAsync(taskGeneration);
            StopBoxForConfiguration();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline &&
                   (IsHearthstoneRunning() ||
                    Process.GetProcessesByName("HSAng").Any() ||
                    Process.GetProcessesByName("HSAExternal").Any()))
                await Task.Delay(250);

            for (var attempt = 1; ; attempt++)
            {
                ThrowIfTaskInterrupted(taskGeneration);
                LaunchHearthstoneViaBattleNet();
                EnsureBoxRunning();
                if (!await WaitForHearthstoneAfterLaunchAsync(TimeSpan.FromSeconds(30), 3))
                    throw new TimeoutException("卡住恢复后 90 秒内未重新检测到 Hearthstone.exe");
                ThrowIfTaskInterrupted(taskGeneration);
                try
                {
                    await WaitForRecoveredNativeSceneAsync(taskGeneration, TimeSpan.FromSeconds(45));
                    break;
                }
                catch (TimeoutException) when (attempt < 2)
                {
                    Append("炉石重启后仍停在错误界面，第 " + attempt + " 次重试…");
                    try { await CloseHearthstoneAsync(taskGeneration); }
                    catch (Exception closeEx) { AppendDeveloper("recover-stuck.close-failed=" + closeEx.GetType().Name + ": " + closeEx.Message); }
                    await Task.Delay(2000);
                }
            }
            // A fresh Battle.net launch can leave the old HsMod snapshot in
            // memory while the new process is already logged in. Refresh the
            // native status before applying the config so a valid session is
            // not rejected as an unknown/unsafe state when HsMod.cfg is still
            // shared-locked during startup.
            await RefreshHsModStatusForStartAsync().ConfigureAwait(true);
            ApplyConfigurationWithBoxRetry();
            if (_settings.RequireBoxHeartbeat && !await WaitForBoxReadyAsync(TimeSpan.FromSeconds(90)))
                throw new TimeoutException("卡住恢复后 90 秒内未收到新的盒子心跳/推荐文件");
            await ScriptActivationGate.EnableAsync(EnsureConfiguredConstructedDeckAsync,
                () => ThrowIfTaskInterrupted(taskGeneration), EnableConstructedScript);
            _clientRecovery.Reset();
            _arenaProgress.Reset(); _startedAt = DateTime.Now;
            recovered = true;
            _recoveryPending = false;
            Append("炉石和盒子已重启，当前任务继续运行（第 " + _stuckRecoveryCount + " 次恢复）。");
        }
        catch (OperationCanceledException) { Append("已按停止请求取消卡住恢复。"); }
        catch (BattleNetAuthenticationRequiredException ex) { PauseForBattleNetPrerequisite(ex.Message); }
        catch (Exception ex)
        {
            Append("炉石卡住自动恢复失败：" + ex.GetType().Name + "：" + ex.Message);
            NativeInstallService.WriteControllerDeveloperLog("RecoverStuckClientAsync", ex);
        }
        finally
        {
            _reconnecting = false;
            if (_running && !_stopRequested && !_awaitingBattleNetLogin) SetStatus(recovered ? "运行中（" + _settings.Mode + "）" : "恢复未完成，等待重试");
        }
    }


    private string? ReadHearthstoneStartupFailure(DateTime since)
    {
        try
        {
            var logsRoot = Path.Combine(_settings.HearthstoneDirectory, "Logs");
            if (!Directory.Exists(logsRoot)) return null;
            var session = FindRecentHearthstoneLogSessions(logsRoot, since)
                .OrderByDescending(p => p.StartTime)
                .FirstOrDefault();
            if (session.Directory == null) return null;
            var files = new[] { "Hearthstone.log", "Login.log", "Services.log" }
                .Select(name => Path.Combine(session.Directory.FullName, name))
                .Where(File.Exists);
            var lines = files.SelectMany(path => File.ReadLines(path).TakeLast(120)).ToArray();
            if (lines.Any(line => line.Contains("无法通过暴雪战网服务进行登录", StringComparison.Ordinal) ||
                                  line.Contains("Failed to get token", StringComparison.OrdinalIgnoreCase) ||
                                  line.Contains("Error.AddFatal()", StringComparison.OrdinalIgnoreCase)))
                return "炉石日志报告登录令牌无效或无法获取";
            if (lines.Any(line => line.Contains("Service Locator Shutdown Complete", StringComparison.OrdinalIgnoreCase)))
                return "炉石服务在登录阶段已关闭";
        }
        catch { }
        return null;
    }


    private bool HasHearthstoneLoggedIn(DateTime since)
    {
        try
        {
            var logsRoot = Path.Combine(_settings.HearthstoneDirectory, "Logs");
            if (!Directory.Exists(logsRoot)) return false;
            // The session name binds evidence to this process. A recently
            // updated old log can contain both old success and fatal errors.
            return FindRecentHearthstoneLogSessions(logsRoot, since)
                .OrderByDescending(p => p.StartTime)
                .Select(p => Path.Combine(p.Directory.FullName, "Login.log"))
                .Where(File.Exists)
                .Any(login => File.ReadLines(login).TakeLast(240)
                    .Any(line => line.Contains("We are now logged in", StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

}
