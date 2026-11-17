using System;
using System.Collections.Generic;

namespace ArenaBoxBot;

internal enum ArenaPopupAction
{
    LeaveOpen,
    DismissNotification,
    ConfirmOwnedTicketPurchase
}

/// <summary>Only known notices may be closed while the Arena script owns a menu.</summary>
internal static class ArenaPopupPolicy
{
    private static readonly HashSet<string> Notifications = new(StringComparer.Ordinal)
    {
        "RankedIntroPopup", "RankedBonusStarsPopup", "DeckArchetypesIntroPopup",
        "CompletedQuestsUpdatedPopup", "EventEndedPopup", "QuestNotificationPopup",
        "RankedRewardDisplay", "BaconInfoPopup", "NewsPopup",
        "CardNerfPopup", "CardNerfGlows", "NewModePopup", "SetRotationPopup",
        "RedundantNDEPopup", "WelcomeQuests", "InnKeepersSpecial", "BoxProductBanner",
        "SetRotationNewPlayerPopup", "WarModeInfoPopup", "DuosPopupTutorial",
        "ReplayableTutorialPopup", "AdventureWingOpenBanner",
        "Hearthstone.Progression.QuestNotificationPopup",
        "Hearthstone.Progression.AchievementToast", "Hearthstone.Progression.QuestProgressToast",
        "Hearthstone.Progression.QuestXpReward"
    };

    internal static bool CanScan(bool nativeArenaRunning, string scene)
        => nativeArenaRunning && (scene == "LOGIN" || scene == "HUB" || scene == "DRAFT");

    internal static bool CanConfirmPremiumRemoval(bool pending, bool currentDraftOwnsCallback,
        string declaringType, string callbackName)
        => pending && currentDraftOwnsCallback && declaringType == "DraftManager" &&
           callbackName == "OnDisablePremiumsConfirmationResponse";

    internal static bool CanConfirmTicketAlert(bool running, string scene, bool purchaseOwned,
        bool stopOnTicketShortage, bool exactNativeCallbackOwner, string declaringType, string callbackName)
        => running && scene == "DRAFT" && purchaseOwned && !stopOnTicketShortage &&
           exactNativeCallbackOwner &&
           ((declaringType == "ProductPage+<>c__DisplayClass57_0" && callbackName == "<TryBuy>b__0") ||
            (declaringType == "PurchaseManager+<>c__DisplayClass28_1" &&
             callbackName == "<Internal_PurchaseProductCoroutine>b__5"));

    internal static bool MatchesGoldTicketOrder(long expectedProductId, long actualProductId, int currency, int quantity)
        => expectedProductId > 0 && actualProductId == expectedProductId && currency == 1 && quantity == 1;

    internal static bool CanAcknowledgeBalanceWarning(bool running, string scene, bool currentGoldOrderOwned,
        bool successfulCurrentReceipt, bool purchasePending, bool exactLocalizedMessage, bool okayOnly, bool callbackMissing)
        => running && scene == "DRAFT" && currentGoldOrderOwned && successfulCurrentReceipt &&
           !purchasePending && exactLocalizedMessage && okayOnly && callbackMissing;

    internal static ArenaPopupAction Decide(bool nativeArenaRunning, string scene, string actualTypeName,
        bool nativeRewardState, bool ticketPurchaseOwned, bool ticketPurchaseConfirmed)
    {
        if (!CanScan(nativeArenaRunning, scene) || string.IsNullOrEmpty(actualTypeName))
            return ArenaPopupAction.LeaveOpen;

        // Match the concrete runtime type, never a base class or a substring.
        // Journal, reconnect/login, consent and arbitrary confirmation panels
        // are intentionally absent from the notification list.
        // StoreDoneWithBAM belongs to the native real-money checkout path.
        // A gold ticket task never authorizes acknowledging that panel.
        if (actualTypeName == "StorePurchaseAuth")
            return scene == "DRAFT" && ticketPurchaseOwned && !ticketPurchaseConfirmed
                ? ArenaPopupAction.ConfirmOwnedTicketPurchase : ArenaPopupAction.LeaveOpen;

        if (actualTypeName == "ArenaRewardsClaimPopup")
            return scene == "DRAFT" && !nativeRewardState
                ? ArenaPopupAction.DismissNotification : ArenaPopupAction.LeaveOpen;

        return Notifications.Contains(actualTypeName)
            ? ArenaPopupAction.DismissNotification : ArenaPopupAction.LeaveOpen;
    }
}
