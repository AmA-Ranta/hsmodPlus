using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ArenaControl.Contracts;

namespace ArenaController;

internal sealed partial class MainForm
{
    private static readonly string SceneHeartbeatFileName = "controller-scene.json";
    private static readonly string ReturnHubRequestFileName = "controller-return-hub.marker";
    private static readonly string ReturnHubReceiptFileName = "controller-return-hub.done";

    /// <summary>A live match is normally won by the script; only a match that
    /// nobody is playing (script off) is a leftover. Values are intentionally
    /// generous: a real game can take 10+ minutes.</summary>
    private static readonly TimeSpan LeftoverMatchGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LiveMatchTimeout = TimeSpan.FromMinutes(20);

    private string ControllerScenePath => HsModPath(SceneHeartbeatFileName);
    private string ReturnHubRequestMarkerPath => HsModPath(ReturnHubRequestFileName);
    private string ReturnHubReceiptFilePath => HsModPath(ReturnHubReceiptFileName);

    private bool TryReadSceneHeartbeat(out string scene, out bool gameplay, out long updatedAtUnixMs)
    {
        scene = "";
        gameplay = false;
        updatedAtUnixMs = 0;
        try
        {
            if (!File.Exists(ControllerScenePath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(ControllerScenePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("updatedAtUnixMs", out var updated)) return false;
            updatedAtUnixMs = updated.GetInt64();
            scene = root.TryGetProperty("scene", out var sceneElement) ? sceneElement.GetString() ?? "" : "";
            gameplay = root.TryGetProperty("gameplay", out var gameplayElement) &&
                       gameplayElement.ValueKind == JsonValueKind.True;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Set when the heartbeat last reported a live match.</summary>
    private DateTime _lastLiveMatchSeenUtc = DateTime.MinValue;

    /// <summary>
    /// Blocks a stop/switch boundary when a match is live or was seen within the
    /// last few seconds. The heartbeat is written once per second, so a match can
    /// be found between two writes; requiring a short quiet window closes that
    /// race (a switch used to close the client right after a game was found).
    /// </summary>
    private bool HeartbeatBlocksStopBoundary()
    {
        if (!TryReadSceneHeartbeat(out var scene, out var gameplay, out var updatedAtUnixMs)) return false;
        var fresh = ControllerLobbyPolicy.IsHeartbeatFresh(updatedAtUnixMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!fresh) return false;
        if (ControllerLobbyPolicy.IsLiveMatchConfirmed(scene, gameplay, true))
        {
            _lastLiveMatchSeenUtc = DateTime.UtcNow;
            return true;
        }
        return DateTime.UtcNow - _lastLiveMatchSeenUtc < TimeSpan.FromSeconds(5);
    }

    /// <summary>Fresh plugin heartbeat says a match is being played right now.</summary>
    private bool IsLiveMatchConfirmedByHeartbeat()
    {
        if (!TryReadSceneHeartbeat(out var scene, out var gameplay, out var updatedAtUnixMs)) return false;
        var fresh = ControllerLobbyPolicy.IsHeartbeatFresh(updatedAtUnixMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return ControllerLobbyPolicy.IsLiveMatchConfirmed(scene, gameplay, fresh);
    }

    private LobbyWaitAction ReadLobbyState(out string scene)
    {
        scene = "";
        if (!TryReadSceneHeartbeat(out scene, out var gameplay, out var updatedAtUnixMs))
            return LobbyWaitAction.Wait;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fresh = ControllerLobbyPolicy.IsHeartbeatFresh(updatedAtUnixMs, nowMs);
        return ControllerLobbyPolicy.Decide(scene, gameplay, fresh);
    }

    /// <summary>
    /// Every start path (manual, daily schedule, queued mode switch, recovery)
    /// passes through here before the script is enabled.  Starting from a
    /// settlement page, a draft screen or the middle of a mode transition is what
    /// made runs look "stuck", so the client must be on the main lobby first.
    /// A real match is never interrupted, but a match that cannot progress
    /// (script off, nobody playing) is recovered by restarting the client.
    /// </summary>
    private async Task EnsureAtMainLobbyAsync(int taskGeneration)
    {
        var overallDeadline = DateTime.UtcNow.AddMinutes(45);
        var hubDeadline = DateTime.MinValue;
        var matchWaitStartedAt = DateTime.MinValue;
        var loginWaitStartedAt = DateTime.MinValue;
        var loginRestartTried = false;
        var clientMissingSince = DateTime.MinValue;
        var requested = false;
        var temporaryScriptTried = false;
        var clientRestartTried = false;
        var lastState = "";

        while (DateTime.UtcNow < overallDeadline)
        {
            ThrowIfTaskInterrupted(taskGeneration);
            // The client can disappear while the gate is waiting (crash, manual
            // exit, failed relaunch). Waiting for a heartbeat that will never
            // come burns the whole deadline, so relaunch through Battle.net.
            if (!IsHearthstoneRunning())
            {
                if (clientMissingSince == DateTime.MinValue) clientMissingSince = DateTime.UtcNow;
                else if (DateTime.UtcNow - clientMissingSince > TimeSpan.FromSeconds(60))
                {
                    Append("等待主界面期间检测到炉石未运行，已重新通过战网启动客户端。");
                    SetStatus("正在重新启动客户端");
                    await RecoverToLobbyByRestartingClientAsync(taskGeneration).ConfigureAwait(true);
                    clientMissingSince = DateTime.MinValue;
                    overallDeadline = DateTime.UtcNow.AddMinutes(20);
                    // The recovery clears the hub request; re-arm the login wait so a
                    // fresh request can be issued if the intro popup blocks again.
                    requested = false;
                    loginWaitStartedAt = DateTime.MinValue;
                    lastState = "";
                }
                await Task.Delay(3000).ConfigureAwait(true);
                continue;
            }
            clientMissingSince = DateTime.MinValue;
            var action = ReadLobbyState(out var scene);
            var sceneLabel = string.IsNullOrEmpty(scene) ? "未知" : scene;
            switch (action)
            {
                case LobbyWaitAction.AlreadyAtLobby:
                    ClearReturnHubRequest();
                    if (lastState != "lobby")
                        Append("已确认客户端在主界面（" + sceneLabel + "），继续开启脚本。");
                    return;

                case LobbyWaitAction.RestartClient:
                    // FATAL_ERROR cannot be navigated away from (SceneMgr refuses the
                    // transition), so the only recovery is a clean client restart.
                    Append("客户端停在致命错误页（FATAL_ERROR），重启客户端回到主界面后继续。");
                    SetStatus("正在重启客户端回到主界面");
                    await RecoverToLobbyByRestartingClientAsync(taskGeneration).ConfigureAwait(true);
                    matchWaitStartedAt = DateTime.MinValue;
                    requested = false;
                    lastState = "recovered";
                    await Task.Delay(3000).ConfigureAwait(true);
                    continue;

                case LobbyWaitAction.WaitForMatchEnd:
                    if (requested)
                    {
                        ClearReturnHubRequest();
                        requested = false;
                    }
                    if (matchWaitStartedAt == DateTime.MinValue)
                    {
                        matchWaitStartedAt = DateTime.UtcNow;
                        SetStatus("等待本局结束再回主界面");
                        Append("开始前检测到对局进行中：先等本局结束再回主界面，不会打断对局。");
                        lastState = "match";
                    }
                    else if (!temporaryScriptTried &&
                             DateTime.UtcNow - matchWaitStartedAt > LeftoverMatchGrace &&
                             !IsConstructedScriptEnabled())
                    {
                        // Nobody is playing this match: it is a leftover from a crash or
                        // an abandoned task. It can never end by itself, and restarting
                        // the client only reconnects into the same match. Let the script
                        // finish it, then close the script and continue to the lobby.
                        temporaryScriptTried = true;
                        Append("对局进行中但脚本未开启（判定为遗留对局）：临时开启脚本接管本局，打完立即关闭并回到主界面。");
                        SetStatus("临时接管遗留对局");
                        if (await TryClearLeftoverMatchWithTemporaryScriptAsync(taskGeneration).ConfigureAwait(true))
                        {
                            matchWaitStartedAt = DateTime.MinValue;
                            temporaryScriptTried = false;
                            overallDeadline = DateTime.UtcNow.AddMinutes(30);
                            lastState = "cleared";
                            continue;
                        }
                        Append("临时脚本仍未结束该对局；重启客户端回到主界面。");
                        SetStatus("正在重启客户端回到主界面");
                        await RecoverToLobbyByRestartingClientAsync(taskGeneration).ConfigureAwait(true);
                        clientRestartTried = true;
                        matchWaitStartedAt = DateTime.MinValue;
                        lastState = "recovered";
                    }
                    else if (DateTime.UtcNow - matchWaitStartedAt > LiveMatchTimeout)
                    {
                        if (clientRestartTried)
                            throw new InvalidOperationException(
                                "对局长时间没有结束（已重启客户端一次仍未恢复），脚本未开启；请手动结束该对局后重试。");
                        Append("对局超过 " + (int)LiveMatchTimeout.TotalMinutes +
                               " 分钟仍未结束；重启客户端回到主界面后继续。");
                        SetStatus("正在重启客户端回到主界面");
                        await RecoverToLobbyByRestartingClientAsync(taskGeneration).ConfigureAwait(true);
                        clientRestartTried = true;
                        matchWaitStartedAt = DateTime.MinValue;
                        lastState = "recovered";
                    }
                    await Task.Delay(3000).ConfigureAwait(true);
                    continue;

                case LobbyWaitAction.WaitForLogin:
                    if (lastState != "login")
                    {
                        SetStatus("等待登录完成并进入主界面");
                        lastState = "login";
                        loginWaitStartedAt = DateTime.UtcNow;
                    }
                    else if (ControllerLobbyPolicy.DecideLoginStall((int)(DateTime.UtcNow - loginWaitStartedAt).TotalSeconds) ==
                             ControllerLobbyPolicy.LoginStallAction.GiveUp)
                    {
                        if (loginRestartTried)
                        {
                            ClearReturnHubRequest();
                            throw new InvalidOperationException(
                                "客户端在登录页停留超过 " + (ControllerLobbyPolicy.LoginStuckSeconds / 60) +
                                " 分钟仍未进入主界面（重启客户端一次后依旧卡住）；脚本未开启。" +
                                "请手动登录一次游戏（点掉登录页的弹窗）后再点开始。");
                        }
                        // A stuck login page often clears itself on a fresh client;
                        // failing the whole task on the first stall is unnecessary.
                        Append("客户端在登录页停留超过 " + (ControllerLobbyPolicy.LoginStuckSeconds / 60) +
                               " 分钟仍无响应；重启客户端后重试。");
                        SetStatus("正在重启客户端回到主界面");
                        await RecoverToLobbyByRestartingClientAsync(taskGeneration).ConfigureAwait(true);
                        loginRestartTried = true;
                        loginWaitStartedAt = DateTime.MinValue;
                        requested = false;
                        lastState = "recovered";
                    }
                    else if (!requested && DateTime.UtcNow - loginWaitStartedAt > TimeSpan.FromSeconds(ControllerLobbyPolicy.LoginWarnSeconds))
                    {
                        // The login welcome page keeps SceneMgr in LOGIN while the
                        // script is off, so the hub is unreachable until someone
                        // closes it. Ask the plugin to do it.
                        WriteReturnHubRequest();
                        requested = true;
                        Append("登录页停留超过 45 秒（可能被登录欢迎页阻塞），已请求插件关闭该页面。");
                    }
                    await Task.Delay(2000).ConfigureAwait(true);
                    continue;

                case LobbyWaitAction.RequestHub:
                    if (!requested)
                    {
                        WriteReturnHubRequest();
                        requested = true;
                        hubDeadline = DateTime.UtcNow.AddSeconds(90);
                        SetStatus("正在返回主界面");
                        Append("开始前先回主界面：已请求插件返回主界面（当前场景=" + sceneLabel + "）。");
                        lastState = "requested";
                    }
                    else if (DateTime.UtcNow > hubDeadline)
                    {
                        ClearReturnHubRequest();
                        Append("插件在 90 秒内没有回到主界面（当前场景=" + sceneLabel + "）；改用重启客户端回到主界面。");
                        SetStatus("正在重启客户端回到主界面");
                        await RecoverToLobbyByRestartingClientAsync(taskGeneration).ConfigureAwait(true);
                        requested = false;
                        lastState = "recovered";
                    }
                    await Task.Delay(2000).ConfigureAwait(true);
                    continue;

                default:
                    await Task.Delay(1000).ConfigureAwait(true);
                    continue;
            }
        }

        ClearReturnHubRequest();
        throw new InvalidOperationException("等待客户端回到主界面超时，脚本未开启。");
    }

    /// <summary>
    /// A leftover match cannot end by itself (script off, nobody playing) and a
    /// client restart only reconnects into it. Enabling the script for exactly one
    /// match is the only reliable way back to the lobby; the script is closed as
    /// soon as the scene leaves GAMEPLAY so it cannot start another match.
    /// </summary>
    private async Task<bool> TryClearLeftoverMatchWithTemporaryScriptAsync(int taskGeneration)
    {
        if (!IsHearthstoneRunning()) return false;
        try
        {
            EnableConstructedScript();
        }
        catch (Exception ex)
        {
            Append("临时开启脚本失败：" + ex.Message);
            return false;
        }
        var deadline = DateTime.UtcNow.AddMinutes(20);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                ThrowIfTaskInterrupted(taskGeneration);
                var action = ReadLobbyState(out var scene);
                var sceneLabel = string.IsNullOrEmpty(scene) ? "未知" : scene;
                // Only a definitive non-match state ends the wait. A missing or
                // stale heartbeat ("未知") is NOT proof that the match ended - it
                // used to end the wait early, turn the script off, and then the
                // gate found a live match again and timed out.
                if (action == LobbyWaitAction.AlreadyAtLobby ||
                    action == LobbyWaitAction.RequestHub ||
                    action == LobbyWaitAction.WaitForLogin)
                {
                    Append("遗留对局已结束（场景=" + sceneLabel + "），关闭临时脚本。");
                    return true;
                }
                if (action == LobbyWaitAction.RestartClient)
                {
                    Append("遗留对局结束时客户端停在致命错误页；交给控制器重启处理。");
                    return false;
                }
                await Task.Delay(3000).ConfigureAwait(true);
            }
            return false;
        }
        finally
        {
            try { DisableConstructedScript(); } catch (Exception ex) { Append("关闭临时脚本失败：" + ex.Message); }
        }
    }

    /// <summary>Last-resort lobby recovery: close the client through the normal
    /// <summary>Last-resort lobby recovery: close the client through the normal
    /// boundary and relaunch it through Battle.net, then wait for login.  Never
    /// runs while the script is enabled (the gate only runs before enabling).</summary>
    private async Task RecoverToLobbyByRestartingClientAsync(int taskGeneration)
    {
        try
        {
            ClearReturnHubRequest();
            if (IsHearthstoneRunning())
                await CloseHearthstoneAsync(taskGeneration).ConfigureAwait(true);
            ThrowIfTaskInterrupted(taskGeneration);
            await Task.Delay(3000).ConfigureAwait(true);
            ThrowIfTaskInterrupted(taskGeneration);
            LaunchHearthstoneViaBattleNet();
            Append("已重新发出官方启动请求，等待登录后确认主界面…");
            await WaitForHearthstoneAfterLaunchAsync(TimeSpan.FromSeconds(45), 3).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Append("重启客户端回到主界面失败：" + ex.Message);
        }
    }

    private void WriteReturnHubRequest()
    {
        try
        {
            // Return to the main lobby is one atomic command; the plugin performs
            // the navigation and acknowledges it.
            PublishControllerCommand(ControllerCommandContract.ActionReturnHub,
                ControllerCommandContract.BoundaryImmediate, "返回主菜单");
            RemoveControlFile(ReturnHubReceiptFilePath);
        }
        catch (Exception ex)
        {
            Append("请求返回主界面失败：" + ex.Message);
        }
    }

    private void ClearReturnHubRequest()
    {
        RemoveControlFile(ReturnHubRequestMarkerPath);
        RemoveControlFile(ReturnHubReceiptFilePath);
        if (_controllerCommand?.Action == ControllerCommandContract.ActionReturnHub) ClearControllerCommand();
    }
}
