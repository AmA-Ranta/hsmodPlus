namespace ArenaBoxBot;

internal static class ControllerMarkerStopReason
{
    internal static string Resolve(bool afterRewards, bool atBoundDraftPage, string current)
        => afterRewards ? "控制端已在奖励结算后停止" :
           atBoundDraftPage ? "控制端已在竞技场安全页停止" : current;
}
