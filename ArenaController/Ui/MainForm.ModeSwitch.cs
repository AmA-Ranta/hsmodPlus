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

    private bool IsMatchInProgress()
    {
        try
        {
            if (!IsHearthstoneRunning()) return false;
            if (IsArenaMode) return IsArenaGameplayActive();
            // The plugin publishes inGameplay for every mode; a fresh snapshot is
            // stronger evidence than a log marker that outlives the match.
            if (TryReadRuntimeInGameplayFresh(out var runtimeInGameplay)) return runtimeInGameplay;
            return IsConstructedGameplayActive();
        }
        catch { return false; }
    }


    /// <summary>Fresh plugin snapshot: is a match actually being played now?</summary>
    private bool TryReadRuntimeInGameplayFresh(out bool inGameplay)
    {
        inGameplay = false;
        try
        {
            if (!TryReadArenaRuntimeState(out var runtime)) return false;
            if (!LiveMatchEvidence.IsRuntimeSnapshotFresh(runtime.updatedAtUnixMs,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return false;
            inGameplay = runtime.inGameplay;
            return true;
        }
        catch { return false; }
    }


    private SessionModeBinding? TryReadSessionModeBinding() => _sessionBindingStore.Read();


    private void WriteSessionModeBinding(string mode, Process game)
    {
        _sessionBindingStore.Write(mode, game.Id, game.StartTime.ToUniversalTime());
    }


    /// <summary>
    /// A mode switch rebuilds the client, so it must never land on a live match -
    /// even when the controller was idle (script off) while the operator kept
    /// playing. Poll the native activity/log until the game leaves gameplay.
    /// </summary>
    private async Task WaitForLiveMatchToEndAsync(int taskGeneration)
    {
        var waited = false;
        var deadline = DateTime.UtcNow.AddMinutes(45);
        while (DateTime.UtcNow < deadline)
        {
            ThrowIfTaskInterrupted(taskGeneration);
            if (!IsLiveMatchBlockingSwitch()) return;
            if (!waited)
            {
                waited = true;
                Append("检测到当前仍有对局在进行；等本局结束后再切换模式…");
                SetStatus("等待本局结束后切换模式");
            }
            await Task.Delay(5000).ConfigureAwait(true);
            try { RefreshStats(); } catch { }
        }
        throw new TimeoutException("等待当前对局结束超时（45 分钟）；为不打断对局，已停止切换模式。");
    }


    /// <summary>
    /// Live-match evidence for the pre-switch wait. Fresh HsMod activity wins:
    /// the constructed log probe can keep a stale "Gameplay.Awake" marker after
    /// a stop and would otherwise stall a mode switch until the 45 minute cap.
    /// </summary>
    private bool IsLiveMatchBlockingSwitch()
    {
        if (NativeActivityLooksInGameplay()) return true;
        var statusFresh = IsHsModStatusFresh() && !_hsModProbeUnavailable;
        var activity = _hsModStatus?.Activity ?? "";
        if (statusFresh && !string.IsNullOrWhiteSpace(activity) &&
            ControllerModeSwitchPolicy.ActivityLooksLikeLobby(activity))
            return false;
        return IsMatchInProgress();
    }


    private bool NativeActivityLooksInGameplay()
        => LiveMatchEvidence.ActivityLooksLikeGameplay(_hsModStatus?.Activity);


    /// <summary>
    /// 运行中点击开始任务：不打断当前对局，等本局结束后停脚本、重启客户端应用
    /// 最新模式，再自动开始。排队期间再次应用设置/再次点击开始都以最新为准；
    /// 手动点停止会取消排队。
    /// </summary>
    /// <summary>
    /// Cancels a deferred stop (user decided to keep playing the current mode).
    /// Only valid before the boundary is reached.
    /// </summary>
    private void CancelPendingStop(string why)
    {
        if (!_stopRequested) return;
        _stopRequested = false;
        _stopWaitingForPostGame = false;
        _stopRequestedLadderGames = null;
        _constructedStopRequest = null;
        RemoveControlFile(StopAfterGamePath);
        RemoveControlFile(InternalStopPath);
        RemoveControlFile(ConstructedStopRequestPath);
        RemoveControlFile(ConstructedStopReceiptPath);
        var label = string.IsNullOrEmpty(_activeRunMode) ? _settings.Mode : _activeRunMode;
        SetStatus("运行中（" + label + "）");
        Append("已取消停止请求（" + why + "），继续当前任务。");
    }


    private void QueueModeSwitchStart(string reason)
    {
        // Live matches must not be edited: staged settings stay staged and are
        // committed at the boundary by MonitorTick. The message only needs the
        // mode the user currently selected.
        var targetSettings = _pendingSettings ?? CaptureUiSettings();
        var targetMode = targetSettings.Mode;
        var target = ControllerModeSwitchPolicy.DescribeTarget(targetMode, targetSettings.ArenaMode);
        var activeLabel = ControllerModeSwitchPolicy.DescribeTarget(_activeRunMode, _settings.ArenaMode);
        if (ControllerModeSwitchPolicy.IsSameMode(targetMode, _activeRunMode))
        {
            _pendingModeSwitchStart = false;
            _pendingModeSwitchReason = "";
            CancelPendingStop("目标模式就是当前运行模式");
            Append("已取消排队：" + activeLabel + " 就是当前运行模式，无需切换。");
            SetStatus("运行中（" + activeLabel + "）");
            return;
        }
        _pendingModeSwitchStart = true;
        _pendingModeSwitchReason = reason;
        Append("已排队模式切换：" + target + "；当前对局结束后自动停止脚本、重启客户端并开始（期间再改模式以最新一次应用为准）。");
        SetStatus("已排队：本局结束后切换到 " + target);
        if (!_stopRequested) RequestStop("模式切换排队：" + target);
    }


    private bool TryReadRuntimeSceneFresh(out string scene)
    {
        scene = "";
        try
        {
            if (!TryReadArenaRuntimeState(out var runtime)) return false;
            if (!LiveMatchEvidence.IsRuntimeSnapshotFresh(runtime.updatedAtUnixMs,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return false;
            scene = runtime.scene ?? "";
            return scene.Length > 0;
        }
        catch { return false; }
    }


    private bool IsFatalRuntimeScene()
    {
        if (!TryReadRuntimeSceneFresh(out var scene)) return false;
        return scene.Equals("FATAL_ERROR", StringComparison.OrdinalIgnoreCase) ||
               scene.Equals("FATAL", StringComparison.OrdinalIgnoreCase) ||
               scene.Equals("ERROR", StringComparison.OrdinalIgnoreCase);
    }


    private bool IsArenaGameplayActive()
    {
        if (TryReadArenaRuntimeState(out var runtime))
            return runtime.inGameplay || runtime.scene.Equals("GAMEPLAY", StringComparison.OrdinalIgnoreCase);
        // Keep the last explicit gameplay transition even after thousands of
        // diagnostic lines. A stale/missing JSON snapshot is not a match end.
        try
        {
            using var stream = new FileStream(ArenaLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            bool? gameplay = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("entered Arena gameplay", StringComparison.OrdinalIgnoreCase)) gameplay = true;
                if (line.Contains("returned from Arena gameplay", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Arena state=REWARDS", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("arena state=IN_REWARDS", StringComparison.OrdinalIgnoreCase)) gameplay = false;
            }
            if (gameplay.HasValue) return gameplay.Value;
        }
        catch { /* Preserve the last known active match on an IO failure. */ }
        return _lastArenaRuntimePath == ArenaRuntimeStatePath && _lastArenaRuntime != null &&
            (_lastArenaRuntime.inGameplay || _lastArenaRuntime.scene.Equals("GAMEPLAY", StringComparison.OrdinalIgnoreCase));
    }


    private bool IsPostGameStopComplete()
    {
        if (TryReadArenaRuntimeState(out var runtime))
        {
            // A current REWARDS/GAMEPLAY/unknown phase is authoritative too:
            // older completion logs cannot override a negative safe boundary.
            var nativeSafe = ArenaStopBoundary.IsKnownSafe(
                true, runtime.arenaSelected, runtime.runtimeEnabled,
                runtime.inGameplay || runtime.scene.Equals("GAMEPLAY", StringComparison.OrdinalIgnoreCase), runtime.scene, runtime.arenaState);
            return ArenaStopBoundary.CompleteFromEvidence(IsHearthstoneRunning(), nativeSafe, false);
        }
        if (!IsHearthstoneRunning()) return true;
        try
        {
            using var game = HearthstoneProcessDiscovery.FindConfigured(_settings.HearthstoneDirectory);
            if (game != null && _arenaStopLogReader != null && _arenaStopLogEvidence != null)
            {
                foreach (var line in _arenaStopLogReader.Read(ArenaLogPath)) _arenaStopLogEvidence.Observe(line, game.Id);
                return ArenaStopBoundary.CompleteFromEvidence(true, null,
                    !_arenaStopLogReader.HasUnreadData && _arenaStopLogEvidence.IsCompleteFor(game.Id));
            }
        }
        catch { /* Incomplete evidence must keep waiting for a native boundary. */ }
        // Never convert an unknown state into a safe stop merely because a
        // timer expired. The controller must leave Hearthstone alive until a
        // native NO_RUN/STOPPED marker (or an exited process) proves that the
        // current game and reward flow are finished.
        return false;
    }


    private bool IsArenaStopBoundaryConfirmed()
    {
        if (!IsHearthstoneRunning()) return true;
        if (!TryReadArenaRuntimeState(out var runtime)) return false;
        return ArenaStopBoundary.IsKnownSafe(
            hearthstoneRunning: true,
            arenaSelected: runtime.arenaSelected,
            runtimeEnabled: runtime.runtimeEnabled,
            inGameplay: runtime.inGameplay,
            scene: runtime.scene,
            arenaState: runtime.arenaState);
    }


    /// <summary>
    /// Single, top-of-tick stop evaluator. Keeping it before every login/launch
    /// wait branch guarantees a pending stop can never be stranded by a boot
    /// transition such as "等待收藏加载".
    /// </summary>
    /// <summary>Running task with no script, no match and no pending stop: the
    /// stop flow never finished its bookkeeping. Starting must reset instead of
    /// queueing forever.</summary>
    private bool HasStaleRunState()
    {
        if (!IsHsModStatusFresh() || _hsModProbeUnavailable) return false;
        if (_hsModStatus?.ScriptEnabled != false) return false;
        return !IsMatchInProgress();
    }

    private bool TryFinishPendingStop()
    {
        if (!_stopRequested) return false;
        var ready = !_stopWaitingForPostGame ||
            (IsArenaMode
                ? IsPostGameStopComplete() || IsArenaStopSettledOff()
                : IsConstructedStopComplete());
        if (!ready)
        {
            if (DateTime.Now - _lastPendingStopDebugAt > TimeSpan.FromSeconds(15))
            {
                _lastPendingStopDebugAt = DateTime.Now;
                AppendDeveloper("stop.pending;script=" + (_hsModStatus == null ? "?" : _hsModStatus.ScriptEnabled.ToString()) +
                    ";activity=" + (_hsModStatus?.Activity ?? "") + ";receipt=" + HasConstructedStopReceipt() +
                    ";gameplayKnown=" + TryReadConstructedGameplayActive(out _));
            }
            return false;
        }
        FinishStop(!IsHearthstoneRunning() ? "炉石已退出，停止任务" :
            _arenaRoundLimitStopRequested ? "已确认本轮停止边界" : "已确认局后停止边界");
        AppendLatestLog();
        return true;
    }


    /// <summary>
    /// A stranded Arena stop: the plugin already went off (fresh status says
    /// script off, runtime says runtimeEnabled=false/inGameplay=false and the
    /// scene left GAMEPLAY) but the controller still waits for a reward/transaction
    /// receipt that will never arrive. Holding the task "running" in that state
    /// makes every later start queue forever. Treat a sustained settled-off state
    /// as the boundary.
    /// </summary>
    private bool IsArenaStopSettledOff()
    {
        if (!IsArenaMode) return false;
        var fresh = IsHsModStatusFresh() && !_hsModProbeUnavailable;
        var settled = fresh && _hsModStatus?.ScriptEnabled == false &&
                      TryReadArenaRuntimeState(out var runtime) &&
                      !runtime.inGameplay && !runtime.runtimeEnabled &&
                      !string.Equals(runtime.scene, "GAMEPLAY", StringComparison.OrdinalIgnoreCase);
        if (!settled) { _arenaSettledOffSinceUtc = null; return false; }
        _arenaSettledOffSinceUtc ??= DateTime.UtcNow;
        return DateTime.UtcNow - _arenaSettledOffSinceUtc.Value >= TimeSpan.FromSeconds(10);
    }

    /// <summary>Start of the current sustained "no live match" heartbeat window.</summary>
    private DateTime? _constructedQuietSinceUtc;

    private bool IsConstructedStopComplete()
    {
        if (!IsHearthstoneRunning()) return true;
        // Counters can recover from a failed/partial read, or an old result
        // can arrive late during the next match. Neither may close GAMEPLAY.
        var gameplayKnown = TryReadConstructedGameplayActive(out var active);
        // A completed result or cached lobby label can race with the next
        // queue. Require this request's native safe-boundary receipt, or a
        // sustained script-off observation at a fresh safe boundary.
        var receipt = HasConstructedStopReceipt();
        // Script-off evidence: fresh API, or the log-side 脚本开关 state when
        // the web endpoint is slow/stale during a client boot (collection load).
        var apiFresh = IsHsModStatusFresh() && !_hsModProbeUnavailable;
        var scriptOff = (apiFresh && _hsModStatus?.ScriptEnabled == false) || !IsConstructedScriptEnabled();
        if (scriptOff) _constructedScriptOffSinceUtc ??= DateTime.UtcNow;
        else _constructedScriptOffSinceUtc = null;
        var scriptOffSustained = _constructedScriptOffSinceUtc.HasValue &&
            DateTime.UtcNow - _constructedScriptOffSinceUtc.Value >= TimeSpan.FromSeconds(15);
        var activity = _hsModStatus?.Activity;
        var safeBoundary = !ConstructedStopBoundary.IsGameplayActivity(activity) &&
            ConstructedStopBoundary.IsKnownSafe(activity, false);
        // While the client boots the status endpoint can be unreachable and no
        // activity text exists; the log-side gameplay probe is the only signal.
        // Absence of a known-active gameplay marker is a safe boot boundary.
        if (activity == null) safeBoundary = !(gameplayKnown && active);
        // The activity allow-list cannot know every mode-specific idle label
        // (Arena reports "竞技场脚本已关闭"), so a fresh heartbeat that keeps
        // saying "no match" for a while is authoritative too.
        var heartbeatQuiet = false;
        if (TryReadSceneHeartbeat(out var heartbeatScene, out var heartbeatGameplay, out var heartbeatAt))
        {
            var heartbeatFresh = ControllerLobbyPolicy.IsHeartbeatFresh(heartbeatAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (ControllerLobbyPolicy.IsQuietSceneHeartbeat(heartbeatScene, heartbeatGameplay, heartbeatFresh))
            {
                _constructedQuietSinceUtc ??= DateTime.UtcNow;
                heartbeatQuiet = DateTime.UtcNow - _constructedQuietSinceUtc.Value >= TimeSpan.FromSeconds(90);
            }
            else _constructedQuietSinceUtc = null;
        }
        else _constructedQuietSinceUtc = null;
        var nativeSafeOff = scriptOff && scriptOffSustained && (safeBoundary || heartbeatQuiet);
        return ConstructedStopContract.CanCompleteStop(true, gameplayKnown, active, receipt, nativeSafeOff);
    }


    private bool IsConstructedStopBoundaryConfirmed()
    {
        if (!IsHearthstoneRunning()) return true;
        var status = _hsModStatus;
        if (status == null || !status.Ready || _hsModProbeUnavailable || DateTime.UtcNow - _lastHsModSuccessAt > TimeSpan.FromSeconds(30)) return false;
        if (status.UpdatedAt.HasValue && DateTime.UtcNow - status.UpdatedAt.Value.ToUniversalTime() > TimeSpan.FromSeconds(30))
            return false;
        return ConstructedStopBoundary.IsKnownSafe(status.Activity);
    }


    private bool IsConstructedGameplayActive()
    {
        if (!TryReadConstructedGameplayActive(out var active)) return true;
        if (!active) return false;
        // A Gameplay.Awake marker can survive a stop. A live match keeps
        // writing to the HsMod log, so require the evidence to be fresh
        // instead of trusting an old marker (observed 23:36: client idle in
        // HUB with script off while the marker still said "in match").
        try
        {
            var log = new FileInfo(PreferredHsModLogPath);
            if (!log.Exists) return false;
            return DateTime.UtcNow - log.LastWriteTimeUtc <= TimeSpan.FromSeconds(120);
        }
        catch { return false; }
    }


    private bool TryReadConstructedGameplayActive(out bool active)
    {
        active = false;
        try
        {
            if (!File.Exists(PreferredHsModLogPath)) return false;
            // ReadTail also traverses the whole file, but discarded the start
            // marker after 500 noisy lines. Keep only the latest transition so
            // a healthy long match does not become an idle recovery candidate.
            bool? gameplay = null;
            var networkFailure = false;
            using var stream = new FileStream(PreferredHsModLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("Gameplay.Awake", StringComparison.OrdinalIgnoreCase))
                {
                    gameplay = true;
                    networkFailure = false;
                }
                // A long-lived HsMod log can contain Gameplay.Awake from a
                // previous match.  These explicit client/queue transitions
                // prove that the current session is back in matchmaking or
                // idle, so do not let that stale marker block timeout recovery.
                if (line.Contains("state: CLIENT_STARTED", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("state: BNET_QUEUE_", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("state: CLIENT_CANCELED", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("state: INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    gameplay = false;
                    networkFailure = false;
                }
                if (line.Contains("对局总结", StringComparison.OrdinalIgnoreCase)) gameplay = false;
                // Battlegrounds does not emit WildLadderBot's summary. Its
                // terminal server exit and persisted placement are full-game
                // boundaries, unlike combat-round or generic disconnect logs.
                if (line.Contains("Network.DisconnectFromGameServer() - Reason: EndGameScreen", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("BgSessionStats: place ", StringComparison.OrdinalIgnoreCase))
                    gameplay = false;
                if (ConnectionRecoveryMonitor.LooksLikeNetworkFailure(line)) networkFailure = true;
                // After a network fatal the client leaves the server scene via
                // CLIENT_CANCELED/INVALID. Treat that as a safe boundary even
                // if no HsMod "对局总结" line was emitted, while a live
                // gameplay disconnect without this transition remains held.
                if (networkFailure &&
                    (line.Contains("state: CLIENT_CANCELED", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("state: INVALID", StringComparison.OrdinalIgnoreCase)))
                {
                    gameplay = false;
                }
            }
            if (!gameplay.HasValue) return false;
            active = gameplay.Value;
            return true;
        }
        catch { return false; }
    }

}
