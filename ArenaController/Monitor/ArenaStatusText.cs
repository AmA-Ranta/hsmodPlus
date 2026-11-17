namespace ArenaController;

public static class ArenaStatusText
{
    public static string DescribeRunState(bool runActive, bool hearthstoneRunning)
    {
        if (!runActive) return "等待新轮次";
        return hearthstoneRunning ? "竞技场进行中" : "未运行（有未完成轮次）";
    }
}
