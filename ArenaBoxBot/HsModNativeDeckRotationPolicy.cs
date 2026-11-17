using System;

namespace ArenaBoxBot;

internal enum NativeDeckRotationAction { None, Wait, Retry, Cancel, Stop }

internal sealed class NativeDeckRotationContext
{
    internal bool Constructed;
    internal bool RuntimeEnabled;
    internal bool RotationEnabled;
    internal bool Gameplay;
    internal bool MenuReady;
    internal bool StopAfterGame;
    internal int Mode;
    internal long DeckId;
    internal string DeckName = "";
}

/// <summary>Holds a native threshold request until a legal alternative can be selected.</summary>
internal sealed class HsModNativeDeckRotationPolicy
{
    internal const double WaitSeconds = 60;
    internal bool Pending { get; private set; }
    internal int Mode { get; private set; }
    internal long DeckId { get; private set; }
    internal string DeckName { get; private set; } = "";
    internal long ExcludeDeckId { get; private set; }
    internal string Reason { get; private set; } = "";
    internal double MenuWaitSeconds { get; private set; }
    private bool _menuSeen;
    private bool _lastWasGameplay;
    private double _lastTime;

    internal void Begin(NativeDeckRotationContext context, long excludeDeckId, string reason, double now)
    {
        Pending = true;
        Mode = context.Mode;
        DeckId = context.DeckId;
        DeckName = context.DeckName ?? "";
        ExcludeDeckId = excludeDeckId;
        Reason = reason ?? "";
        Restart(now);
    }

    internal void Restart(double now)
    {
        MenuWaitSeconds = 0;
        _menuSeen = false;
        _lastWasGameplay = false;
        _lastTime = now;
    }

    internal bool Matches(NativeDeckRotationContext context)
    {
        return Pending && context.Constructed && context.RotationEnabled && !context.StopAfterGame &&
            context.Mode == Mode && context.DeckId == DeckId &&
            string.Equals(context.DeckName ?? "", DeckName, StringComparison.Ordinal);
    }

    internal NativeDeckRotationAction Observe(NativeDeckRotationContext context, bool nativeListReady,
        int alternatives, double now)
    {
        if (!Pending) return NativeDeckRotationAction.None;
        if (!Matches(context) || !context.RuntimeEnabled) return NativeDeckRotationAction.Cancel;
        // A long game/exit animation cannot consume a menu loading budget.
        if (!context.Gameplay && !_lastWasGameplay && _menuSeen) MenuWaitSeconds += Math.Max(0, now - _lastTime);
        _lastTime = now;
        _lastWasGameplay = context.Gameplay;
        if (context.Gameplay) return NativeDeckRotationAction.Wait;
        if (context.MenuReady) _menuSeen = true;
        if (MenuWaitSeconds >= WaitSeconds) return NativeDeckRotationAction.Stop;
        if (!context.MenuReady || !nativeListReady || alternatives <= 0) return NativeDeckRotationAction.Wait;
        return NativeDeckRotationAction.Retry;
    }

    internal bool Complete(long selectedDeckId)
    {
        if (!Pending || selectedDeckId <= 0 || selectedDeckId == ExcludeDeckId) return false;
        Cancel();
        return true;
    }

    internal bool RetainFailedReplayIdentity(NativeDeckRotationContext observed)
    {
        if (!Pending || !observed.Constructed || observed.Mode != Mode) return false;
        // Native SetScriptDeck changes the memory entries before Save. A failed Save is
        // a program write, not a manual choice: retain the request with its actual memory identity.
        DeckId = observed.DeckId;
        DeckName = observed.DeckName ?? "";
        return true;
    }

    internal static bool CanSubmitAfterService(bool pendingOnEntry, bool runtimeEnabled, bool pending, bool failed)
    {
        // Tick already resolved its local deck before the final queue gate.
        // A switch at that gate requires a fresh Tick, even when the switch succeeds.
        return !pendingOnEntry && runtimeEnabled && !pending && !failed;
    }

    internal static bool CanSafelyStop(NativeDeckRotationContext context)
    {
        return context.Constructed && context.RotationEnabled && !context.Gameplay && context.MenuReady;
    }

    internal static bool CanConfirmNativeSet(long selected, long excluded, long setReceipt, bool bound)
    {
        return bound && selected > 0 && selected != excluded && selected == setReceipt;
    }

    internal void Cancel() { Pending = false; }
}
