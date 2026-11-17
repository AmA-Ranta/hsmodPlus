namespace ArenaController;

/// <summary>Only live, post-attachment server evidence may establish a disconnected session.</summary>
public sealed class ConnectionRecoveryMonitor
{
    private DateTime? _fatalAt;
    private DateTime? _lastInvalidAt;
    private int _invalidCount;
    private bool _historicalPrime;
    private DateTime? _loginWithoutProgressAt;
    private string _lastOptions = "";
    private int _recommendationTimeoutStreak;
    private int _lastTimeoutOptionsId = -1;

    /// <summary>Tracks Box recommendation stalls without treating them as a disconnect.</summary>
    public string? ObserveRecommendationTimeout(DateTime now, string line)
    {
        if (line.Contains("对局总结", StringComparison.Ordinal) || line.Contains("Gameplay.Awake", StringComparison.Ordinal))
        {
            _recommendationTimeoutStreak = 0;
            _lastTimeoutOptionsId = -1;
            return null;
        }
        if (line.Contains("BoxAI链路提交:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("recommendation applied", StringComparison.OrdinalIgnoreCase))
        {
            var previous = _recommendationTimeoutStreak;
            _recommendationTimeoutStreak = 0;
            _lastTimeoutOptionsId = -1;
            return previous > 0 ? "推荐超时后已观察到动作提交；此前连续超时=" + previous + "；服务端动作确认仍以原生日志为准" : null;
        }
        if (!line.Contains("BoxAI链路超时", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("BoxAI recommendation timeout", StringComparison.OrdinalIgnoreCase))
            return null;

        var optionsId = ParseIntAfter(line, "optionsId=");
        _lastTimeoutOptionsId = optionsId;
        _recommendationTimeoutStreak++;
        if (_recommendationTimeoutStreak == 1)
            return "推荐单次超时；optionsId=" + optionsId + "；仅记录诊断，不代表已恢复";
        if (_recommendationTimeoutStreak == 3)
            return "推荐连续超时=3；optionsId=" + optionsId + "；不能仅据此判定断线或盒子限流";
        if (_recommendationTimeoutStreak >= 6 && _recommendationTimeoutStreak % 3 == 0)
            return "推荐连续超时=" + _recommendationTimeoutStreak + "；optionsId=" + optionsId + "；可能影响本回合操作时间，需检查推荐发布与执行链";
        return null;
    }

    public int RecommendationTimeoutStreak => _recommendationTimeoutStreak;

    /// <summary>
    /// Seeds a disconnect sequence from the current log tail when the
    /// controller was restarted after the client had already entered its
    /// fatal/rewards scene. NativeLogReader intentionally skips old history,
    /// so without this prime a restarted controller would wait forever for a
    /// new error line that the game no longer emits.
    /// </summary>
    public void PrimeNetworkFailure(DateTime now)
    {
        _fatalAt ??= now;
        _lastInvalidAt = now;
        _invalidCount = Math.Max(_invalidCount, 3);
        _historicalPrime = true;
    }

    public static bool LooksLikeNetworkFailure(string line)
    {
        return IsNetworkFailureSignal(line);
    }

    private static int ParseIntAfter(string line, string marker)
    {
        var at = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        at += marker.Length;
        var end = at;
        while (end < line.Length && char.IsDigit(line[end])) end++;
        return int.TryParse(line[at..end], out var value) ? value : -1;
    }

    public void Observe(DateTime now, string line, bool requireProgressAfterLogin = false)
    {
        if (line.Contains("We are now logged in", StringComparison.OrdinalIgnoreCase))
        {
            if (requireProgressAfterLogin && _fatalAt.HasValue)
                _loginWithoutProgressAt ??= now;
            else Reset();
            return;
        }
        if (line.Contains("state: BNET_QUEUE_DELAYED", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("state: BNET_QUEUE_UPDATED", StringComparison.OrdinalIgnoreCase))
        {
            ClearNetworkEvidence();
            return;
        }
        var optionsAt = line.IndexOf("BoxAI链路Options:", StringComparison.OrdinalIgnoreCase);
        if (optionsAt >= 0)
        {
            var options = line[optionsAt..];
            if (options != _lastOptions)
            {
                // A new options packet proves the game is progressing, so it
                // clears disconnect evidence. It is not a successful Box
                // recommendation by itself; preserve the timeout streak.
                ClearNetworkEvidence();
                _lastOptions = options;
            }
            return;
        }
        // Hearthstone's network layer does not always put the useful reason
        // on the same line as Error.AddFatal.  In particular, DNS failures
        // are emitted as GetHostAddress/GLOBAL_ERROR_NETWORK_NO_CONNECTION,
        // followed by the generic fatal message.  Treat those live signals as
        // one disconnect sequence so recovery cannot silently wait forever
        // while the local HsMod HTTP endpoint still reports ready=true.
        if (IsNetworkFailureSignal(line))
        {
            _fatalAt ??= now;
            _lastInvalidAt = now;
            _invalidCount++;
            _historicalPrime = false;
            _loginWithoutProgressAt = null;
            return;
        }
    }

    public string? RecoveryReason(DateTime now, bool currentFatalSceneConfirmed = false,
        bool currentSafeIdleSceneConfirmed = false)
    {
        var postLoginIdle = currentSafeIdleSceneConfirmed && _loginWithoutProgressAt.HasValue &&
            now - _loginWithoutProgressAt.Value >= TimeSpan.FromMinutes(3);
        if (!_fatalAt.HasValue || !_lastInvalidAt.HasValue || _invalidCount < 3 ||
            now - _fatalAt.Value < TimeSpan.FromMinutes(3) ||
            (!_historicalPrime && !currentFatalSceneConfirmed && !postLoginIdle && now - _lastInvalidAt.Value > TimeSpan.FromSeconds(45))) return null;
        if (postLoginIdle)
            return "战网断线后虽已报告登录，当前客户端在安全菜单持续 3 分钟未见新的服务端排队或对局操作状态";
        return currentFatalSceneConfirmed
            ? "游戏明确报告会话断开，超过 3 分钟仍处于当前客户端 FATAL_ERROR 状态，未见重新登录或新的对局操作状态"
            : "游戏明确报告会话断开，连续 3 分钟发送失败且未见重新登录或新的对局操作状态";
    }

    public static bool IsCurrentFatalScene(int gamePid, int apiGamePid, bool apiFresh,
        bool runtimeFresh, bool inGameplay, string? scene, DateTime runtimeUpdatedUtc, DateTime gameStartedUtc)
        => IsCurrentNonGameplaySnapshot(gamePid, apiGamePid, apiFresh, runtimeFresh, inGameplay, runtimeUpdatedUtc, gameStartedUtc) &&
           string.Equals(scene, "FATAL_ERROR", StringComparison.OrdinalIgnoreCase);

    public static bool IsCurrentIdleScene(int gamePid, int apiGamePid, bool apiFresh,
        bool runtimeFresh, bool inGameplay, string? scene, DateTime runtimeUpdatedUtc, DateTime gameStartedUtc)
        => IsCurrentNonGameplaySnapshot(gamePid, apiGamePid, apiFresh, runtimeFresh, inGameplay, runtimeUpdatedUtc, gameStartedUtc) &&
           (string.Equals(scene, "TOURNAMENT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scene, "HUB", StringComparison.OrdinalIgnoreCase));

    private static bool IsCurrentNonGameplaySnapshot(int gamePid, int apiGamePid, bool apiFresh,
        bool runtimeFresh, bool inGameplay, DateTime runtimeUpdatedUtc, DateTime gameStartedUtc)
        => gamePid > 0 && apiGamePid == gamePid && apiFresh && runtimeFresh && !inGameplay && runtimeUpdatedUtc >= gameStartedUtc;

    private static bool IsNetworkFailureSignal(string line)
    {
        return line.Contains("SendUtilPacket could not send, connection not valid", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("ERROR_SDK_SOCKET_CLOSED", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("GLOBAL_ERROR_NETWORK_NO_CONNECTION", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("RpcController.SendData() - Not connected", StringComparison.OrdinalIgnoreCase) ||
               (line.Contains("Error.AddFatal", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("连接中断", StringComparison.Ordinal) ||
                 line.Contains("disconnected", StringComparison.OrdinalIgnoreCase)));
    }

    private void ClearNetworkEvidence() { _fatalAt = _lastInvalidAt = _loginWithoutProgressAt = null; _invalidCount = 0; _historicalPrime = false; }
    public void Reset() { ClearNetworkEvidence(); _lastOptions = ""; _recommendationTimeoutStreak = 0; _lastTimeoutOptionsId = -1; }
}
