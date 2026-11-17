namespace ArenaController;

public static class ArenaRunStopBindingPolicy
{
    // A retained statistics ledger is not evidence that a server run still
    // exists. Pin the run only while the fresh, process-bound runtime shows it.
    public static bool CanPinRun(bool selected, bool trackedActive, bool inGameplay, string state)
        => selected && trackedActive && (inGameplay || state is
            "DRAFTING" or "MIDRUN" or "MIDRUN_REDRAFT_PENDING" or "REDRAFTING" or
            "EDITING_DECK" or "REWARDS" or "IN_REWARDS");

    public static bool EnableAfterBinding(bool pending, Func<bool> publish, Func<bool> enable)
    {
        if (pending && !publish())
            throw new InvalidOperationException("竞技场本轮停止请求尚未绑定当前客户端，暂不开启脚本。");
        return enable();
    }
}
