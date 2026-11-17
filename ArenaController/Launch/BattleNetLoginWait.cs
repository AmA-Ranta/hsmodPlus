namespace ArenaController;

public enum BattleNetLoginObservation { Unknown, Required, Ready }

// A missing title is not an authenticated launcher. A failed launch batch
// also must not start another batch against the same unchanged launcher.
public sealed class BattleNetLoginWait
{
    public bool IsWaiting { get; private set; }
    private bool _readyAtPause;

    public void Pause(BattleNetLoginObservation observation,
        BattleNetLoginObservation lastCompletedObservation = BattleNetLoginObservation.Unknown)
    {
        IsWaiting = true;
        // An expired Ready is insufficient to resume, but still remembers
        // which unchanged launcher just exhausted its launch attempts.
        _readyAtPause = (observation == BattleNetLoginObservation.Unknown ? lastCompletedObservation : observation)
            == BattleNetLoginObservation.Ready;
    }

    public bool TryResume(BattleNetLoginObservation observation, bool gameExists)
    {
        if (!IsWaiting) return false;
        if (observation == BattleNetLoginObservation.Required) _readyAtPause = false;
        if (!gameExists && (observation != BattleNetLoginObservation.Ready || _readyAtPause)) return false;
        Reset();
        return true;
    }

    public void Reset() { IsWaiting = false; _readyAtPause = false; }

    public static BattleNetLoginObservation Classify(bool explicitLoginTitle, bool visibleLoginChild, bool enabledGamePlayButton)
        => explicitLoginTitle || visibleLoginChild ? BattleNetLoginObservation.Required :
            enabledGamePlayButton ? BattleNetLoginObservation.Ready : BattleNetLoginObservation.Unknown;

    public static bool IsHearthstonePlayButton(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
            ((name.StartsWith("进入游戏", StringComparison.Ordinal) && name.Contains("炉石传说", StringComparison.Ordinal)) ||
             (name.StartsWith("Play", StringComparison.OrdinalIgnoreCase) && name.Contains("Hearthstone", StringComparison.OrdinalIgnoreCase)));
}
