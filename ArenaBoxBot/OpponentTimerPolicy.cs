namespace ArenaBoxBot;

internal static class OpponentTimerPolicy
{
    internal static float Remaining(float original, bool activeOwnOption, bool ownerKnown,
        bool timerFriendly, bool hasCountdown, bool timerTimedOut)
        => original == 0f && activeOwnOption && ownerKnown && !timerFriendly &&
           hasCountdown && timerTimedOut ? float.NaN : original;
}
