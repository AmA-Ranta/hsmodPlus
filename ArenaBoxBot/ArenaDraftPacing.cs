using System;

namespace ArenaBoxBot;

/// <summary>
/// Arena keeps HsMod's two pace choices, with a longer reading interval for
/// newly displayed card buckets. This delay starts after the independent
/// visual readiness gate; neither pace may bypass native animations.
/// </summary>
internal static class ArenaDraftPacing
{
    internal const string SettingsDescription = "拟人：天梯等待 0.8～1.5 秒；竞技场选牌在画面稳定后再等待 2～3 秒。尽快：缩短思考等待，竞技场仍须等待卡牌加载及动画完成。酒馆战棋固定尽快。";
    internal const float HumanMinimumSeconds = 2f;
    internal const float HumanMaximumSeconds = 3f;
    internal const float FastMinimumSeconds = 0.15f;
    internal const float FastMaximumSeconds = 0.3f;

    internal static bool IsHuman(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = value.Trim();
        // HsMod persists the English enum value ("Fastest"); the localized
        // label is "尽快". Treat both as the fast pace.
        return !text.Equals("尽快", StringComparison.OrdinalIgnoreCase) &&
               !text.Equals("Fast", StringComparison.OrdinalIgnoreCase) &&
               !text.Equals("Fastest", StringComparison.OrdinalIgnoreCase);
    }

    internal static float NextDelay(bool human, Random random)
    {
        if (random == null) throw new ArgumentNullException(nameof(random));
        var min = human ? HumanMinimumSeconds : FastMinimumSeconds;
        var max = human ? HumanMaximumSeconds : FastMaximumSeconds;
        return (float)(min + random.NextDouble() * (max - min));
    }

    internal static bool IsReady(float now, float readyAt)
    {
        return readyAt <= 0f || now + 0.0001f >= readyAt;
    }
}
