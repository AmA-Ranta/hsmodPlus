using ArenaBoxBot;
using System.Collections;

var assertions = 0;
foreach (var running in new[] { false, true })
foreach (var owned in new[] { false, true })
foreach (var receipt in new[] { false, true })
foreach (var pending in new[] { false, true })
foreach (var message in new[] { false, true })
foreach (var okayOnly in new[] { false, true })
foreach (var callbackMissing in new[] { false, true })
{
    assertions++;
    var expected = running && owned && receipt && !pending && message && okayOnly && callbackMissing;
    if (ArenaPopupPolicy.CanAcknowledgeBalanceWarning(running, "DRAFT", owned, receipt, pending, message, okayOnly, callbackMissing) != expected)
        throw new Exception("Balance warning must never close pending purchase, confirmation callback, unrelated text or unowned order");
}
foreach (var scene in new[] { "HUB", "LOGIN", "GAMEPLAY", "UNKNOWN" })
{
    assertions++;
    if (ArenaPopupPolicy.CanAcknowledgeBalanceWarning(true, scene, true, true, false, true, true, true))
        throw new Exception("Balance warning scope must stay in the owned Draft menu");
}
void Expect(ArenaPopupAction actual, ArenaPopupAction expected, string context)
{
    assertions++;
    if (actual != expected) throw new Exception($"{context}: expected {expected}, got {actual}");
}

var protectedPanels = new[] { "Journal", "JournalPopup", "Hearthstone.Progression.JournalPopup",
    "ReconnectHelperDialog", "AlertPopup", "UIBPopup", "InfoPopup", "PrivacyPolicyPopup",
    "PrivacyFeaturesPopup", "InitialDownloadDialog", "FriendlyChallengeDialog",
    "PushNotificationSoftAskController", "UnknownPurchasePopup", "PackageCardsPopup", "StoreDoneWithBAM" };
foreach (var running in new[] { false, true })
foreach (var scene in new[] { "LOGIN", "HUB", "DRAFT", "GAMEPLAY", "TOURNAMENT" })
foreach (var panel in protectedPanels)
    Expect(ArenaPopupPolicy.Decide(running, scene, panel, false, true, false),
        ArenaPopupAction.LeaveOpen, $"Preserve functional panel {panel}/{running}/{scene}");

foreach (var panel in new[] { "WelcomeQuests", "NewsPopup", "StorePurchaseAuth", "ArenaRewardsClaimPopup" })
foreach (var scene in new[] { "LOGIN", "HUB", "DRAFT", "GAMEPLAY" })
    Expect(ArenaPopupPolicy.Decide(false, scene, panel, false, true, false),
        ArenaPopupAction.LeaveOpen, $"Stopped script must leave {panel} alone");

Expect(ArenaPopupPolicy.Decide(true, "HUB", "NewsPopup", false, false, false),
    ArenaPopupAction.DismissNotification, "An active Arena menu may close an ordinary notice");
Expect(ArenaPopupPolicy.Decide(true, "HUB", "Vendor.NewsPopup", false, false, false),
    ArenaPopupAction.LeaveOpen, "Unknown concrete type must not inherit a base-type exemption");
Expect(ArenaPopupPolicy.Decide(true, "GAMEPLAY", "NewsPopup", false, false, false),
    ArenaPopupAction.LeaveOpen, "Do not manipulate gameplay dialogs");

foreach (var panel in new[] { "StorePurchaseAuth" })
foreach (var owned in new[] { false, true })
foreach (var confirmed in new[] { false, true })
foreach (var scene in new[] { "LOGIN", "HUB", "DRAFT" })
    Expect(ArenaPopupPolicy.Decide(true, scene, panel, false, owned, confirmed),
        scene == "DRAFT" && owned && !confirmed ? ArenaPopupAction.ConfirmOwnedTicketPurchase : ArenaPopupAction.LeaveOpen,
        $"Purchase ownership boundary {panel}/{scene}/{owned}/{confirmed}");

Expect(ArenaPopupPolicy.Decide(true, "DRAFT", "ArenaRewardsClaimPopup", true, false, false),
    ArenaPopupAction.LeaveOpen, "Native reward claim must remain usable");
Expect(ArenaPopupPolicy.Decide(true, "DRAFT", "ArenaRewardsClaimPopup", false, false, false),
    ArenaPopupAction.DismissNotification, "Only stale post-reward decoration may be dismissed");
Console.WriteLine($"Popup policy regression passed: {assertions} assertions");
foreach (var pending in new[] { false, true })
foreach (var owned in new[] { false, true })
foreach (var type in new[] { "DraftManager", "StoreManager", "ReconnectHelperDialog", "" })
foreach (var callback in new[] { "OnDisablePremiumsConfirmationResponse", "OnPurchase", "OnDisconnect", "" })
{
    assertions++;
    var expected = pending && owned && type == "DraftManager" && callback == "OnDisablePremiumsConfirmationResponse";
    if (ArenaPopupPolicy.CanConfirmPremiumRemoval(pending, owned, type, callback) != expected)
        throw new Exception($"Unrelated confirmation was accepted: {pending}/{owned}/{type}/{callback}");
}
Console.WriteLine($"Popup ownership regression passed: {assertions} total assertions");

void Check(bool value, string reason)
{
    assertions++;
    if (!value) throw new Exception(reason);
}

foreach (var running in new[] { false, true })
foreach (var scene in new[] { "DRAFT", "HUB", "GAMEPLAY" })
foreach (var owned in new[] { false, true })
foreach (var stopOnShortage in new[] { false, true })
foreach (var sameNativeOwner in new[] { false, true })
foreach (var callback in new[] {
    ("ProductPage+<>c__DisplayClass57_0", "<TryBuy>b__0", true),
    ("PurchaseManager+<>c__DisplayClass28_1", "<Internal_PurchaseProductCoroutine>b__5", true),
    ("PurchaseManager+<>c__DisplayClass28_0", "<Internal_PurchaseProductCoroutine>b__1", false),
    ("PurchaseManager+<>c__DisplayClass28_2", "<Internal_PurchaseProductCoroutine>b__8", false),
    ("StoreManager", "ConfirmPurchase", false), ("ReconnectHelperDialog", "Disconnect", false),
    ("Journal", "Purchase", false), ("ProductPage+<>c__DisplayClass57_0", "<TryBuy>b__1", false) })
{
    var expected = running && scene == "DRAFT" && owned && !stopOnShortage && sameNativeOwner && callback.Item3;
    Check(ArenaPopupPolicy.CanConfirmTicketAlert(running, scene, owned, stopOnShortage,
        sameNativeOwner, callback.Item1, callback.Item2) == expected,
        $"Ticket callback ownership failed: {running}/{scene}/{owned}/{stopOnShortage}/{sameNativeOwner}/{callback}");
}

var events = new List<string>();
// A matching product name or stale ticket ID must never authorize another
// product, a cash/runestone payment, or a bulk purchase.
foreach (var expectedProduct in new long[] { 0, 12345 })
foreach (var actualProduct in new long[] { 0, 12345, 67890 })
foreach (var currency in new[] { -1, 0, 1, 3, 4, 6 })
foreach (var quantity in new[] { 0, 1, 2 })
    Check(ArenaPopupPolicy.MatchesGoldTicketOrder(expectedProduct, actualProduct, currency, quantity) ==
        (expectedProduct == 12345 && actualProduct == 12345 && currency == 1 && quantity == 1),
        $"Only the single requested GOLD ticket order is authorized: {expectedProduct}/{actualProduct}/{currency}/{quantity}");

var allowed = true;
var nativeWait = new object();
IEnumerator NativeClose()
{
    try
    {
        events.Add("outro");
        yield return nativeWait;
        events.Add("destroy");
        events.Add("dismiss callback");
    }
    finally { events.Add("dispose"); }
}
var close = new ArenaPopupCloseOperation(NativeClose(), () => allowed);
Check(events.Count == 0 && !close.Completed, "Returning a native iterator must not be reported as closed");
Check(close.MoveNext() && ReferenceEquals(close.Current, nativeWait), "Native animation wait must be yielded to Unity");
Check(events.SequenceEqual(new[] { "outro" }) && !close.Completed, "Do not run destruction before Unity resumes animation");
Check(!close.MoveNext() && close.Completed, "Native close body must actually finish");
Check(events.SequenceEqual(new[] { "outro", "destroy", "dismiss callback", "dispose" }), "Run complete native close and callback exactly once");
Check(!close.MoveNext(), "A completed native iterator cannot be restarted");
close.Dispose();
Check(events.Count == 4 && close.Completed && !close.Cancelled, "Completed disposal is idempotent");

events.Clear();
allowed = true;
var cancelled = new ArenaPopupCloseOperation(NativeClose(), () => allowed);
Check(cancelled.MoveNext(), "First frame starts the notification outro");
allowed = false;
Check(!cancelled.MoveNext() && cancelled.Cancelled && !cancelled.Completed, "F12 off or scene change must cancel unfinished closure");
Check(events.SequenceEqual(new[] { "outro", "dispose" }), "Stopping must not destroy the panel or execute dismiss callback later");
allowed = true;
Check(!cancelled.MoveNext() && events.Count == 2, "Restart must not resurrect an old close callback");

events.Clear();
IEnumerator ParentClose()
{
    try { yield return NativeClose(); events.Add("parent completed"); }
    finally { events.Add("parent disposed"); }
}
var nested = new ArenaPopupCloseOperation(ParentClose(), () => allowed);
Check(nested.MoveNext() && ReferenceEquals(nested.Current, nativeWait), "Nested iterators must remain in the tracked operation");
nested.Dispose();
Check(nested.Cancelled && !nested.Completed && !nested.MoveNext(), "Explicit cancellation also stops a nested native iterator");
Check(events.SequenceEqual(new[] { "outro", "dispose", "parent disposed" }), "Cancel must dispose nested work without completing callbacks");

events.Clear();
var neverStarted = new ArenaPopupCloseOperation(NativeClose(), () => false);
Check(!neverStarted.MoveNext() && neverStarted.Cancelled && events.Count == 0, "A stale scheduled callback must not start native effects");
IEnumerator FailingClose()
{
    yield return nativeWait;
    throw new InvalidOperationException("native failure");
}
var failing = new ArenaPopupCloseOperation(FailingClose(), () => true);
Check(failing.MoveNext(), "A failing native routine may begin normally");
try { failing.MoveNext(); throw new Exception("Expected native failure"); }
catch (InvalidOperationException) { }
Check(failing.Cancelled && !failing.Completed && !failing.MoveNext(), "An exception must never be reported as a closed popup");
Console.WriteLine($"Popup callback/cancellation regression passed: {assertions} total assertions");
