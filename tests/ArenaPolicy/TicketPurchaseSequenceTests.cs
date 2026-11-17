using ArenaBoxBot;

internal static class TicketPurchaseSequenceTests
{
    internal static void Run()
    {
        Check(ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, false, true, false, false, 0, 1, 2), "observed Underground single paid ticket/needed2 can prepare next native order");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, false, true, false, false, 1, 2, 2), "second receipt reaching native entry cost never buys a third ticket");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, false, true, false, false, 0, 1, 1), "Normal one-ticket purchase stays unchanged");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(false, true, false, true, false, false, 0, 1, 2), "balance/count alone cannot authorize purchase");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, false, false, true, false, false, 0, 1, 2), "receipt before actual result acknowledgement waits");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, true, true, false, false, 0, 1, 2), "pending payment cannot be repeated");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, false, false, false, false, 0, 1, 2), "unrecorded receipt cannot authorize purchase");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, false, true, true, false, 0, 1, 2), "accepted/unresolved entry remains protected");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, false, true, false, true, 0, 1, 2), "active round cannot start a new order");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, false, true, false, false, 1, 1, 2), "unchanged native balance remains held");
        Check(!ArenaTicketPurchaseSequence.CanRequestRemainingTicket(true, true, false, true, false, false, -1, 1, 2), "unknown original balance remains held");
        Console.WriteLine("PASS: real0->1 partial ticket scenario,2-ticket target/Normal1-ticket/no extra third, owned success/UI acknowledgement/pending entry and payment protection");
    }
    private static void Check(bool ok, string reason)
    { if (!ok) throw new InvalidOperationException(reason); }
}
