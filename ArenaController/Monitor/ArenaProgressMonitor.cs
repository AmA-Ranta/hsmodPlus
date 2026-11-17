namespace ArenaController;

/// <summary>Track stable native phase/selection progress, never match duration or heartbeat timestamps.</summary>
public sealed class ArenaProgressMonitor
{
    private string? _progress;
    private DateTime _since;
    public string? Observe(DateTime now, bool managed, bool stopping, bool runtimeFresh,
        bool enabled, bool inGameplay, string scene, string state, int slot, int maxSlot, int games, string nativeProgress = "")
    {
        if (!managed || stopping || !runtimeFresh || !enabled || inGameplay ||
            string.IsNullOrWhiteSpace(scene) || scene.Equals("GAMEPLAY", StringComparison.OrdinalIgnoreCase))
        { Reset(); return null; }
        var mismatch = scene.Equals("TOURNAMENT", StringComparison.OrdinalIgnoreCase);
        var draft = scene.Equals("DRAFT", StringComparison.OrdinalIgnoreCase);
        if (!mismatch && !draft) { Reset(); return null; }
        var key = scene + "|" + state + "|" + slot + "|" + maxSlot + "|" + games + "|" + nativeProgress;
        if (_progress != key || now < _since) { _progress = key; _since = now; return null; }
        // Draft/collection animation, redraft and slow matchmaking may take minutes.
        var threshold = TimeSpan.FromMinutes(mismatch ? 3 : 6);
        if (now - _since < threshold) return null;
        return mismatch ? "竞技场持续停在天梯页面超过 3 分钟" : "竞技场非对局流程超过 6 分钟没有推进（" + state + "，选牌位置 " + slot + "/" + maxSlot + "）";
    }
    public void Reset() { _progress = null; _since = default; }
}
