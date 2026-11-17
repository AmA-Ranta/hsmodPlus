namespace ArenaController;

/// <summary>
/// Requires sustained failures of live health signals before recovering a
/// session. Match duration and the age of a match-start log are deliberately
/// excluded: healthy long games must never be closed by the controller.
/// </summary>
public sealed class ClientRecoveryMonitor
{
    private DateTime? _unresponsiveSince;
    private DateTime? _boxUnavailableSince;
    private DateTime? _runtimeUnavailableSince;
    private DateTime? _gameplayFreezeSince;

    public string? ObserveGameplay(DateTime now, bool windowResponsive, bool runtimeFresh)
    {
        _unresponsiveSince = _boxUnavailableSince = _runtimeUnavailableSince = null;
        // An active match is never timed out by its duration, a Box window
        // title or a single failed API read. Both Unity's window and runtime
        // must remain unavailable for another 90 seconds before recovery.
        var frozen = Sustained(!windowResponsive && !runtimeFresh, now,
            ref _gameplayFreezeSince, TimeSpan.FromSeconds(90));
        return frozen ? "对局窗口及插件运行状态同时持续无响应超过 90 秒" : null;
    }

    public string? Observe(DateTime now, bool windowResponsive, bool boxWindowUsable,
        bool boxHeartbeatFresh, bool runtimeFresh, bool arenaMode, bool nativeLogActive = false)
    {
        _gameplayFreezeSince = null;
        var unresponsive = Sustained(!windowResponsive, now, ref _unresponsiveSince,
            TimeSpan.FromSeconds(25));
        // HSAng can recreate its Qt window while its collector keeps working.
        // Either a usable window or a fresh heartbeat cancels the box timer.
        var boxUnavailable = Sustained(!boxWindowUsable && !boxHeartbeatFresh, now,
            ref _boxUnavailableSince, TimeSpan.FromSeconds(45));
        // A busy in-match client can leave the local status endpoint silent for
        // a long time while the native log keeps growing.  Restarting then
        // throws away a healthy match, so fresh native activity suppresses the
        // runtime timer entirely; the match boundary keeps its own guard.
        var runtimeUnavailable = Sustained(!runtimeFresh && !nativeLogActive, now, ref _runtimeUnavailableSince,
            TimeSpan.FromSeconds(arenaMode ? 15 : 90));

        if (unresponsive) return "Hearthstone 窗口持续无响应超过 25 秒";
        if (boxUnavailable) return "盒子主窗口和心跳连续异常超过 45 秒";
        if (runtimeUnavailable)
            return arenaMode ? "竞技场运行状态超过 90 秒未更新" : "HsMod 状态接口超过 90 秒未响应";
        return null;
    }

    public void Reset()
    {
        _unresponsiveSince = null;
        _boxUnavailableSince = null;
        _runtimeUnavailableSince = null;
        _gameplayFreezeSince = null;
    }

    private static bool Sustained(bool failed, DateTime now, ref DateTime? since, TimeSpan minimum)
    {
        if (!failed)
        {
            since = null;
            return false;
        }
        if (!since.HasValue || now < since.Value) since = now;
        return now - since.Value >= minimum;
    }
}
