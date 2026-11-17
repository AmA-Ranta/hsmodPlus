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

    private bool IsWithinSchedule()
    {
        var now = DateTime.Now.TimeOfDay; var start = ParseTime(_settings.StartTime, 8, 0).TimeOfDay; var stop = ParseTime(_settings.StopTime, 23, 0).TimeOfDay;
        return start <= stop ? now >= start && now < stop : now >= start || now < stop;
    }


    private bool ObserveNativeStop()
    {
        // Two independent fresh reads prevent transient IO/API failures from
        // masquerading as an F12 stop. Do not re-enable a user's native stop.
        var apiOff = IsHsModStatusFresh() && !_hsModProbeUnavailable && _hsModStatus?.ScriptEnabled == false;
        var arenaOff = !IsArenaMode || (TryReadArenaRuntimeState(out var native) && !native.runtimeEnabled && !native.inGameplay);
        if (!apiOff || !arenaOff) { _nativeDisabledSince = null; return false; }
        _nativeDisabledSince ??= DateTime.Now;

        // Native game-limit enforcement may turn the script off before the
        // controller's next poll sees the final settlement. Complete that
        // task through the normal safe-stop path, including CloseGame.
        var quotaReached = false;
        string? quotaReason = null;
        if (!IsArenaMode && _settings.MaxTotalGames > 0 && _baselineLadderGames.HasValue)
        {
            var summary = ReadLadderSummary(CurrentLadderMode);
            if (summary.Reliable && summary.Games >= _baselineLadderGames.Value &&
                summary.Games - _baselineLadderGames.Value >= _settings.MaxTotalGames)
            { quotaReached = true; quotaReason = "达到任务总局数（原生脚本已停止）"; }
        }

        var offSeconds = (DateTime.Now - _nativeDisabledSince.Value).TotalSeconds;
        var sinceEnabled = _scriptEnabledConfirmedAt == DateTime.MinValue
            ? double.MaxValue
            : (DateTime.Now - _scriptEnabledConfirmedAt).TotalSeconds;

        // Arena automatic stop reasons keep their dedicated safe-boundary path.
        if (IsArenaMode && TryReadArenaRuntimeState(out var arenaRuntime) &&
            NativeArenaStopPolicy.IsAutomaticCloseCandidate(_settings.CloseGameAfterStop, arenaRuntime.stopReason))
        {
            var factsKnown = TryReadArenaStopTransactionFacts(out var rewardsPending, out var purchasePending, out var entryPhase);
            if (!NativeArenaStopPolicy.CanFinish(IsArenaStopBoundaryConfirmed(), factsKnown,
                    rewardsPending, purchasePending, entryPhase))
            { SetStatus("脚本已停止，等待竞技场安全边界关闭炉石"); return true; }
            ++_taskGeneration;
            FinishStop("竞技场原生自动停止：" + arenaRuntime.stopReason);
            return true;
        }

        switch (ConstructedStopPolicy.Decide(true, offSeconds, sinceEnabled, quotaReached))
        {
            case NativeStopAction.Ignore:
            case NativeStopAction.Wait:
                return false;
            case NativeStopAction.CompleteQuota:
                RequestStop(quotaReason!);
                return true;
            case NativeStopAction.Reenable:
                if (DateTime.Now - _lastStopReenableAt < TimeSpan.FromSeconds(30)) return false;
                _lastStopReenableAt = DateTime.Now;
                try
                {
                    EnableConstructedScript();
                    _scriptEnabledConfirmedAt = DateTime.Now;
                    _nativeDisabledSince = null;
                    Append("检测到脚本在任务启动初期被外部关闭，已自动重新开启并继续接管。");
                }
                catch (Exception ex)
                {
                    AppendDeveloper("stop-policy.reenable.failed=" + ex.GetType().Name + ":" + ex.Message);
                }
                return false;
            default:
                break;
        }

        ++_taskGeneration;
        _running = false; _scheduleStopLatched = true;
        CancelArenaRunStop();
        LockTaskInputs(false);
        _start.Enabled = true; _stop.Enabled = false;
        _clientRecovery.Reset(); _arenaProgress.Reset();
        // A detached controller must not leave the plugin free to keep
        // playing: otherwise the UI says "stopped" while matches continue.
        try { SetWatchdogAutoResume(false, strict: true); }
        catch (Exception ex) { AppendDeveloper("stop-policy.autoresume-off.failed=" + ex.Message); }
        SetStatus("脚本被外部关闭（中控已停止接管）");
        Append("已确认游戏内脚本被外部关闭，中控停止接管并关闭插件自动续跑；可手动开始新的任务。");
        return true;
    }

    private bool TryObserveExternalConstructedStop()
    {
        try
        {
            if (!TryReadGameProcessIdentity(out var running, out var pid, out var started)) return false;
            if (!running)
            {
                // A current-task native receipt can outlive a normally exited
                // process. Only the last independently bound, recent API PID
                // may identify it; never trust an arbitrary receipt's PID.
                if (_hsModStatus == null || _hsModStatus.GameProcessId <= 0 ||
                    _hsModStatus.GameStartedAtUtc == DateTime.MinValue ||
                    DateTime.UtcNow - _lastHsModSuccessAt > TimeSpan.FromSeconds(30)) return false;
                pid = _hsModStatus.GameProcessId;
                started = _hsModStatus.GameStartedAtUtc;
            }
            foreach (var path in new[] { ConstructedStopRequestPath, ConstructedStopReceiptPath })
            {
                var receipt = path == ConstructedStopReceiptPath;
                if ((!running && !receipt) || !File.Exists(path)) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var external = JsonSerializer.Deserialize<ConstructedStopRequest>(stream,
                    new JsonSerializerOptions { IncludeFields = true });
                if (!ConstructedStopContract.CanAdoptExternalStop(external!, pid, started.Ticks,
                    _settings.Mode ?? "", _startedAt.ToUniversalTime().Ticks, DateTime.UtcNow.Ticks, receipt)) continue;
                _constructedStopRequest = external;
                AppendDeveloper("接收外部构筑局后停止：requestId=" + external!.RequestId + ";receipt=" + receipt);
                RequestStop("收到外部局后停止请求");
                return true;
            }
        }
        catch (Exception ex) { AppendDeveloper("外部构筑停止请求不可读，未据此修改运行状态：" + ex.GetType().Name); }
        return false;
    }


    private bool IsArenaRewardsPending()
    {
        if (TryReadArenaRuntimeState(out var runtime))
        {
            if (string.Equals(runtime.arenaState, "REWARDS", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runtime.arenaState, "IN_REWARDS", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var text = ReadTail(ArenaLogPath, 120);
        var reward = Math.Max(text.LastIndexOf("Arena state=REWARDS", StringComparison.OrdinalIgnoreCase),
            Math.Max(text.LastIndexOf("Arena state=IN_REWARDS", StringComparison.OrdinalIgnoreCase),
                Math.Max(text.LastIndexOf("underground arena state=REWARDS", StringComparison.OrdinalIgnoreCase),
                    text.LastIndexOf("underground arena state=IN_REWARDS", StringComparison.OrdinalIgnoreCase))));
        var noRun = Math.Max(text.LastIndexOf("Arena state=NO_RUN", StringComparison.OrdinalIgnoreCase),
            text.LastIndexOf("underground arena state=NO_RUN", StringComparison.OrdinalIgnoreCase));
        return reward >= 0 && reward > noRun;
    }


    private bool HasMatchingConstructedStopReceipt(int pid, DateTime started)
    {
        try
        {
            if (_constructedStopRequest == null) return false;
            var receipt = JsonSerializer.Deserialize<ConstructedStopRequest>(File.ReadAllText(ConstructedStopReceiptPath),
                new JsonSerializerOptions { IncludeFields = true });
            return ConstructedStopContract.Confirms(_constructedStopRequest, receipt!, pid, started.Ticks,
                _settings.Mode ?? "", DateTime.UtcNow.Ticks);
        }
        catch { return false; }
    }


    private bool TryReadUnrecoveredNetworkFailure()
    {
        try
        {
            if (!File.Exists(PreferredHsModLogPath)) return false;
            var pending = false;
            using var stream = new FileStream(PreferredHsModLogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("We are now logged in", StringComparison.OrdinalIgnoreCase))
                {
                    pending = false;
                    continue;
                }
                if (line.Contains("Gameplay.Awake", StringComparison.OrdinalIgnoreCase))
                {
                    pending = false;
                    continue;
                }
                if (ConnectionRecoveryMonitor.LooksLikeNetworkFailure(line))
                    pending = true;
            }
            return pending;
        }
        catch (Exception ex)
        {
            AppendDeveloper("connection-history.read=" + ex.GetType().Name + ":" + ex.Message);
            return false;
        }
    }


    private static bool IsBenignNativeNotice(string line)
    {
        // Unity engine errors (spell FX hookups, optional UI objects, asset
        // loaders) are client rendering noise: they stay in the developer log
        // but must not raise an unattended "plugin exception" alert.
        if (line.Contains(": Unity Log]", StringComparison.OrdinalIgnoreCase)) return true;

        // Our own diagnostic lines (映射/读取/队列/等待诊断) may mention an error
        // counter or a stale-request reason; they are health telemetry, never a
        // plugin failure, and must not raise an unattended alert.
        if (line.Contains("诊断:", StringComparison.OrdinalIgnoreCase)) return true;

        // HsMod fetches rank/MMR statistics alongside the game. A transient
        // network timeout there leaves gameplay untouched.
        if (line.Contains("BgRank: fetch failed", StringComparison.OrdinalIgnoreCase)) return true;

        // Stack-trace continuation lines belong to an error already reported;
        // re-alerting on them would double-count the same failure.
        if (line.Contains("--- End of stack trace from previous location", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("--- End of inner exception stack trace", StringComparison.OrdinalIgnoreCase))
            return true;

        // Hearthstone can emit this after the end-game screen while medal
        // data is still loading. It is not a script failure and must not
        // create a noisy developer-diagnostic alert or recovery candidate.
        if (line.Contains("NetCacheMedalInfo not yet available!", StringComparison.OrdinalIgnoreCase))
            return true;

        // Unity reports these while optional UI/visual objects are inactive or
        // still loading (shop notices, favourite-hero fallbacks, spell FX).
        // The client continues normally, so they must not be surfaced as a
        // plugin failure every time the monitor reads the native log.
        foreach (var benign in new[]
        {
            "Coroutine couldn't be started because the the game object",
            "Could not find object of name",
            "Couldn't find Favorite Hero",
            "Spell was null and could not be released",
            "PlayableAsset returned a null Playable on Instantiate",
            "FX Hookup Subscription Manager",
            "Target(Hearthstone.UI.Spawnable) for callback",
            "DBF Asset has 0 records",
            "SoundManager.Play() - source is null",
            "is missing from asset bundle",
            // Client-side teardown/reconnect workarounds: the client continues
            // normally, so they must not raise a plugin-failure notice.
            "FullscreenFXMgr is missing",
            "ShowDisconnectedGameResult()",
            "unhandled game result GR_UNKNOWN",
            "actualChoiceCards is NULL",
            "RpcController.SendData() - Not connected",
            "Trying to send a packet while disconnected",
            "for callback OnFatalError is null",
            "Hiding notification HelpTooltip_Tutorial",
            // End-of-match medal polling can time out while the client finishes
            // the end-game screen; the next queue continues normally.
            "EndGameScreen_MedalInfoTimeOut",
            // The end-of-match card-def loader can receive dbId 0 while the
            // result screen tears down; it is a client lookup miss, not a bot error.
            "DefLoader.GetCardDef() - dbId 0 does not map to a cardId",
            // Board spawn hooks can run before their actor component is attached.
            "InitializeCustomSpawn is not properly constructed",
            // Match-end teardown reports a peer disconnect while the result
            // screen finishes; the client and the next queue continue normally.
            "Disconnected from game server with BNet error",
            "ERROR_RPC_PEER_UNKNOWN"
        })
            if (line.Contains(benign, StringComparison.OrdinalIgnoreCase)) return true;

        // Unity may fail to instantiate an optional visual timeline asset
        // while the game is changing scenes. The client continues normally;
        // treating this renderer warning as a controller failure causes a
        // false restart alert during otherwise healthy turns.
        if (line.Contains("PlayableAsset returned a null Playable on Instantiate", StringComparison.OrdinalIgnoreCase))
            return true;

        // ERROR_OK is the successful socket-connect result written with an
        // unfortunate error-code label. It is followed by the handshake and
        // GAME_STARTED lines, so it must not be surfaced as a network fault.
        if (line.Contains("Connected to game server with error code ERROR_OK", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("socket error: Success, Message: Client: Connected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("BNet error: ERROR_OK socket error: Success, Message:", StringComparison.OrdinalIgnoreCase))
            return true;

        // The client can report a null optional spell FX object while
        // releasing pooled visuals. Gameplay continues and the next BoxAI
        // option is still processed; this renderer cleanup warning must not
        // be promoted to a controller failure.
        if (line.Contains("Spell Pooling message=Spell was null and could not be released", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }


    private void ObserveArenaMatchmakingTimeout()
    {
        if (!_running || _stopRequested || _reconnecting) return;
        var now = DateTime.Now;
        while (_arenaMatchmakingTimeouts.Count > 0 && now - _arenaMatchmakingTimeouts.Peek() > TimeSpan.FromMinutes(10))
            _arenaMatchmakingTimeouts.Dequeue();
        _arenaMatchmakingTimeouts.Enqueue(now);
        if (_arenaMatchmakingTimeouts.Count < 2 || _arenaNetworkRecovery || _reconnecting ||
            now - _lastArenaNetworkRecoveryAt < TimeSpan.FromMinutes(10) ||
            !_running || _stopRequested || !IsArenaMode)
            return;
        if (!TryReadArenaRuntimeState(out var runtime) || !runtime.runtimeEnabled || runtime.inGameplay)
            return;
        _arenaNetworkRecovery = true;
        _ = RecoverArenaMatchmakingAsync();
    }


    private void ObserveConstructedMatchmakingTimeout()
    {
        if (!_running || _stopRequested || _reconnecting || IsArenaMode) return;
        var now = DateTime.Now;
        while (_constructedMatchmakingTimeouts.Count > 0 &&
               now - _constructedMatchmakingTimeouts.Peek() > TimeSpan.FromMinutes(10))
            _constructedMatchmakingTimeouts.Dequeue();
        _constructedMatchmakingTimeouts.Enqueue(now);
        var scriptEnabled = _hsModStatus?.ScriptEnabled == true && IsHsModStatusFresh();
        if (!scriptEnabled)
        {
            try
            {
                var fresh = ProbeHsModStatusAsync().GetAwaiter().GetResult();
                if (fresh != null)
                {
                    _hsModStatus = fresh;
                    _hsModProbeUnavailable = false;
                    _lastHsModSuccessAt = DateTime.UtcNow;
                    scriptEnabled = fresh.ScriptEnabled && IsHsModStatusFresh();
                }
            }
            catch { }
        }
        var gameplayActive = IsConstructedGameplayActive();
        AppendDeveloper("constructed-matchmaking-timeout=" + _constructedMatchmakingTimeouts.Count +
            ";scriptEnabled=" + scriptEnabled + ";gameplayActive=" + gameplayActive);
        if (!MatchmakingTimeoutPolicy.ShouldRecover(_constructedMatchmakingTimeouts, now,
                _lastConstructedMatchRecoveryAt, scriptEnabled, gameplayActive))
            return;

        _lastConstructedMatchRecoveryAt = now;
        _constructedMatchmakingTimeouts.Clear();
        Append("构筑模式匹配连续两次超时；当前不在对局中，沿用战网启动链安全重启炉石和盒子网络会话。");
        _ = RecoverStuckClientAsync("构筑模式匹配连续两次握手超时");
    }

}
