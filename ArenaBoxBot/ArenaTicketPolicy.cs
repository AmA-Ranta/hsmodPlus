using System;

namespace ArenaBoxBot;

/// <summary>
/// Pure resource gate for the native Arena ticket page. Unity code owns page
/// navigation and confirmation; this type owns only the stop/allow decision.
/// </summary>
public enum ArenaTicketGateResult
{
    Ready,
    AllowPurchase,
    WaitForResourceCheck,
    StopTicketShortage,
    StopGoldShortage
}

public static class ArenaTicketPolicy
{
    public static ArenaTicketGateResult Decide(
        int ticketsOwned,
        int ticketsRequired,
        bool goldKnown,
        int gold,
        int ticketPrice,
        bool stopOnTicketShortage,
        bool stopOnGoldShortage,
        float purchaseElapsedSeconds,
        float purchaseTimeoutSeconds,
        bool resourceCheckDue)
    {
        if (ticketsOwned >= ticketsRequired) return ArenaTicketGateResult.Ready;
        // This is a resource limit, not a retry preference. A paid purchase
        // must never be attempted when the user selected stop on shortage.
        if (stopOnTicketShortage) return ArenaTicketGateResult.StopTicketShortage;
        if (!stopOnTicketShortage && !stopOnGoldShortage)
            return ArenaTicketGateResult.AllowPurchase;
        if (!resourceCheckDue)
            return ArenaTicketGateResult.WaitForResourceCheck;
        if (goldKnown && gold < Math.Max(0, ticketPrice) && stopOnGoldShortage)
            return ArenaTicketGateResult.StopGoldShortage;
        return ArenaTicketGateResult.AllowPurchase;
    }
}
