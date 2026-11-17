using System;

namespace ArenaBoxBot;

internal static class BgShopSourcePolicy
{
    // Tavern spells are CARDTYPE42, distinct from ordinary SPELL5. Both
    // remain subject to the exact live DragBuy_Spell target check.
    internal static bool IsPurchaseCardType(string action, int cardType)
        => action == "buy"
            ? cardType == (int)TAG_CARDTYPE.MINION
            : action == "buy_special" &&
              (cardType == (int)TAG_CARDTYPE.SPELL || cardType == (int)TAG_CARDTYPE.BATTLEGROUND_SPELL);

    internal static bool IsCurrent(int pid, int recPid, int packet, int recPacket,
        double age, bool activeOwnOption, string action, int explicitEntity, string zone)
        => (action == "buy" || action == "buy_special") && explicitEntity == 0 &&
           (string.IsNullOrEmpty(zone) || string.Equals(zone, "baconshop", StringComparison.OrdinalIgnoreCase)) &&
           OptionEntityFallbackPolicy.IsCurrent(pid, recPid, packet, recPacket, age, activeOwnOption);
}
