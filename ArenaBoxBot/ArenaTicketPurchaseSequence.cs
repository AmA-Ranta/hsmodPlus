namespace ArenaBoxBot;

/// <summary>A new single-ticket order requires the previous owned order to be fully settled.</summary>
internal static class ArenaTicketPurchaseSequence
{
    internal static bool CanRequestRemainingTicket(bool confirmedOwnedReceipt, bool resultAcknowledged,
        bool purchasePending, bool purchaseCounted, bool entryPending, bool runActive,
        int ticketsBefore, int ticketsNow, int entryCost)
        => confirmedOwnedReceipt && resultAcknowledged && !purchasePending && purchaseCounted &&
           !entryPending && !runActive && ticketsBefore >= 0 && ticketsNow > ticketsBefore &&
           entryCost > 0 && ticketsNow < entryCost;
}
