using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Blizzard.T5.Services;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

internal sealed partial class ArenaController
{
    private sealed class NotificationClose
    {
        internal int ComponentId;
        internal string Scene;
        internal string TypeName;
        internal ArenaPopupCloseOperation Operation;
        internal Coroutine Coroutine;
    }

    private readonly Dictionary<int, NotificationClose> _notificationCloses = new();

    private bool PopupActionsAllowed(string scene)
        => isActiveAndEnabled && !_runStopRequested && !File.Exists(ControllerStopAfterGamePath) &&
           ArenaPopupPolicy.CanScan(HsModNativeArena.IsNativeArenaModeSelected() &&
               HsModNativeArena.IsRuntimeEnabled(), scene) && SceneMgr.Get()?.GetMode().ToString() == scene;

    private void MaintainPopupOperations()
    {
        foreach (var close in _notificationCloses.Values.ToArray())
            if (!PopupActionsAllowed(close.Scene)) CancelNotificationClose(close);
        if (_ticketProductId != 0 && !TicketPurchaseActionsAllowed()) ResetTicketPopupOwnership();
    }

    private void CancelNotificationClose(NotificationClose close)
    {
        _notificationCloses.Remove(close.ComponentId);
        close.Operation.Dispose();
        if (close.Coroutine != null) StopCoroutine(close.Coroutine);
        Log("cancelled unfinished notification close type=" + close.TypeName + "; automation no longer owns the menu");
    }

    private void CancelPopupOperations()
    {
        foreach (var close in _notificationCloses.Values.ToArray()) CancelNotificationClose(close);
        ResetTicketPopupOwnership();
    }

    private void OnDisable() => CancelPopupOperations();
    private void OnDestroy() => CancelPopupOperations();

    private bool StartNotificationClose(MonoBehaviour component, string scene, string typeName, IEnumerator iterator)
    {
        var key = component.GetInstanceID();
        if (_notificationCloses.ContainsKey(key)) return false;
        var close = new NotificationClose { ComponentId = key, Scene = scene, TypeName = typeName };
        close.Operation = new ArenaPopupCloseOperation(iterator, () => PopupActionsAllowed(scene));
        // Register before StartCoroutine: Unity may run the iterator to completion synchronously.
        _notificationCloses.Add(key, close);
        Log("started native notification close coroutine type=" + typeName);
        try { close.Coroutine = StartCoroutine(RunNotificationClose(key, close)); }
        catch { _notificationCloses.Remove(key); close.Operation.Dispose(); throw; }
        return true;
    }

    private IEnumerator RunNotificationClose(int key, NotificationClose close)
    {
        try
        {
            while (true)
            {
                bool more;
                try { more = close.Operation.MoveNext(); }
                catch (Exception ex)
                {
                    Log("notification close coroutine failed type=" + close.TypeName + ": " + ex.GetType().Name + ": " + ex.Message);
                    yield break;
                }
                if (!more) break;
                yield return close.Operation.Current;
            }
            // Completion means the native callback finished, not that every
            // notification or functional window on screen was dismissed.
            if (close.Operation.Completed)
                Log("native notification close coroutine completed type=" + close.TypeName);
        }
        finally
        {
            close.Operation.Dispose();
            _notificationCloses.Remove(key);
        }
    }

    private long _ticketProductId;
    private ProductPage _ticketProductPage;
    private object _ticketNativePurchaseRoutine;
    private object _ticketCheckoutArgs;
    private object _ticketSuccessfulTransaction;
    private bool _submittingNativeTicketPurchase;
    private readonly HashSet<Delegate> _ticketConfirmedCallbacks = new();
    private readonly HashSet<int> _ticketAcknowledgedPopups = new();
    private string _ticketOwnershipDiagnostic = "";

    private static object PopupField(object value, string name)
    {
        for (var type = value?.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(value);
        }
        return null;
    }

    private static object PopupProperty(object value, string name)
        => value == null ? null : FindZeroIndexProperty(value.GetType(), name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value, null);

    private static long TicketProductId(object product)
    {
        var pmt = PopupProperty(product, "PmtId");
        if (pmt != null) return Convert.ToInt64(pmt);
        var id = PopupProperty(product, "Id");
        return id == null ? 0 : Convert.ToInt64(PopupProperty(id, "Value") ?? 0);
    }

    private bool TicketPurchaseActionsAllowed()
        => PopupActionsAllowed("DRAFT") && ArenaSettings.StopOnTicketShortage?.Value == false;

    private bool HasOwnedTicketPurchase => _ticketProductId > 0 && TicketPurchaseActionsAllowed();
    private bool _ticketResultAcknowledged;

    private void PrepareRemainingTicketPurchase(DraftManager manager)
    {
        var data = ArenaStatsStore.Data;
        if (manager == null || data == null || !HasOwnedTicketPurchase ||
            !ArenaTicketPurchaseSequence.CanRequestRemainingTicket(
                _ticketPurchaseConfirmed && _ticketSuccessfulTransaction != null, _ticketResultAcknowledged,
                data.PurchasePending, data.PurchaseCounted, ArenaStatsLedger.HasUnresolvedEntry(data), data.RunActive,
                data.PurchaseTicketsBefore, manager.GetNumTicketsOwned(), ArenaSettings.TicketCost)) return;
        // Keep the settled receipt, balance, and ownership intact. The existing
        // BeginTicketPopupOwnership method waits for the native transaction to
        // become idle before capturing a fresh order/checkout/receipt identity.
        _ticketPurchaseRequested = false;
        _ticketPurchaseClicked = false;
        _ticketPurchaseConfirmed = false;
        _ticketPurchaseStartedAt = -1f;
        _ticketPurchaseAttempts = 0;
        _nextTicketProductRetry = Time.unscaledTime;
        Log("owned single-ticket order and result UI completed; preparing remaining native ticket: owned=" +
            manager.GetNumTicketsOwned() + "; required=" + ArenaSettings.TicketCost + "; previous receipt retained");
    }

    private void ResetTicketPopupOwnership()
    {
        _ticketProductId = 0;
        _ticketProductPage = null;
        _ticketNativePurchaseRoutine = null;
        _ticketCheckoutArgs = null;
        _ticketSuccessfulTransaction = null;
        _ticketResultAcknowledged = false;
        _submittingNativeTicketPurchase = false;
        _ticketConfirmedCallbacks.Clear();
        _ticketAcknowledgedPopups.Clear();
        _ticketOwnershipDiagnostic = "";
        if (ReferenceEquals(ArenaTicketPurchaseObserver.Owner, this)) ArenaTicketPurchaseObserver.Owner = null;
    }

    private bool BeginTicketPopupOwnership(long productId, ProductPage page)
    {
        if (productId <= 0 || !TicketPurchaseActionsAllowed() || !ArenaTicketPurchaseObserver.EnsureInstalled()) return false;
        var store = StoreManager.Get();
        var inTransaction = typeof(StoreManager).GetMethod("IsInTransaction", InstancePrivate);
        if (store == null || inTransaction == null || Equals(inTransaction.Invoke(store, null), true) ||
            store.IsPurchaseAuthViewShown() || !ServiceManager.TryGet<PurchaseManager>(out var purchaseManager) ||
            PopupField(purchaseManager, "m_currentPurchaseCoroutine") != null)
        {
            LogTicketOwnershipDeferred("another native purchase or authorization is still active");
            return false;
        }
        ResetTicketPopupOwnership();
        _ticketProductId = productId;
        _ticketProductPage = page;
        ArenaTicketPurchaseObserver.Owner = this;
        return true;
    }

    private bool MatchesTicketOrder(object product, object price, object quantity)
        => HasOwnedTicketPurchase && ArenaPopupPolicy.MatchesGoldTicketOrder(_ticketProductId, TicketProductId(product),
            Convert.ToInt32(PopupProperty(price, "Currency") ?? -1), Convert.ToInt32(quantity ?? 0));

    internal void ObserveTicketPurchaseRoutine(object purchase, IEnumerator routine)
    {
        if (!_submittingNativeTicketPurchase || routine == null ||
            !MatchesTicketOrder(PopupField(purchase, "Product"), PopupField(purchase, "Price"), PopupField(purchase, "Quantity"))) return;
        _ticketNativePurchaseRoutine = routine;
        Log("observed owned native gold ticket order: product=" + _ticketProductId);
    }

    internal void ObserveTicketCheckout(object args)
    {
        if (!HasOwnedTicketPurchase || _ticketNativePurchaseRoutine == null ||
            !ArenaPopupPolicy.MatchesGoldTicketOrder(_ticketProductId,
                Convert.ToInt64(PopupField(args, "PmtProductId") ?? 0),
                Convert.ToInt32(PopupField(args, "PaymentCurrency") ?? -1),
                Convert.ToInt32(PopupField(args, "Quantity") ?? 0))) return;
        // This exact argument instance was built by the iterator captured
        // while our one gold-ticket click was on the stack. PMT alone is not ownership.
        if (!ReferenceEquals(PopupField(_ticketNativePurchaseRoutine, "<args>5__2"), args)) return;
        _ticketCheckoutArgs = args;
        Log("observed owned native ticket checkout: product=" + _ticketProductId + ", currency=GOLD, quantity=1");
    }

    internal void ObserveTicketPurchaseSuccess(object manager, object transaction)
    {
        // HandlePurchaseSuccess clears m_pendingProductPurchaseArgs before it
        // builds the result window. Capture the native receipt while the
        // exact GOLD order is still attached, rather than guessing from text.
        if (!HasOwnedTicketPurchase || _ticketCheckoutArgs == null || transaction == null ||
            !ReferenceEquals(PopupField(manager, "m_pendingProductPurchaseArgs"), _ticketCheckoutArgs) ||
            Convert.ToInt64(PopupProperty(transaction, "PMTProductID") ?? 0) != _ticketProductId) return;
        _ticketSuccessfulTransaction = transaction;
        _ticketPurchaseConfirmed = true;
        Log("observed successful native GOLD ticket receipt: product=" + _ticketProductId);
    }

    private bool IsOwnedTicketAlert(AlertPopup popup, out Delegate callback)
    {
        callback = null;
        if (!HasOwnedTicketPurchase) return false;
        var info = PopupField(popup, "m_popupInfo");
        callback = PopupField(info, "m_responseCallback") as Delegate;
        if (callback == null || callback.GetInvocationList().Length != 1 || _ticketConfirmedCallbacks.Contains(callback)) return false;
        var type = callback.Method.DeclaringType?.FullName;
        var name = callback.Method.Name;
        var exactOwner = false;
        if (_ticketProductPage != null && ReferenceEquals(info, PopupField(_ticketProductPage, "m_preBuyPopupInfo")))
        {
            // Native ProductPage.TryBuy captures the immutable product, gold price,
            // and quantity in this callback and stores the same PopupInfo on the page.
            exactOwner = MatchesTicketOrder(PopupField(callback.Target, "immuatableSelectedProduct"),
                PopupField(callback.Target, "price"), PopupField(callback.Target, "quantity"));
        }
        else if (_ticketNativePurchaseRoutine != null &&
                 ReferenceEquals(callback.Target, PopupField(_ticketNativePurchaseRoutine, "<>8__2")))
        {
            var purchase = PopupField(_ticketNativePurchaseRoutine, "purchase");
            exactOwner = MatchesTicketOrder(PopupField(purchase, "Product"), PopupField(purchase, "Price"), PopupField(purchase, "Quantity"));
        }
        return ArenaPopupPolicy.CanConfirmTicketAlert(true, "DRAFT", HasOwnedTicketPurchase,
            ArenaSettings.StopOnTicketShortage.Value, exactOwner, type, name);
    }

    private bool TryAcknowledgeOwnedBalanceWarning(AlertPopup popup)
    {
        if (popup == null || _ticketAcknowledgedPopups.Contains(popup.GetInstanceID())) return false;
        var info = PopupField(popup, "m_popupInfo");
        var expected = GameStrings.Format("GLUE_STORE_FAIL_CURRENCY_BALANCE", Array.Empty<object>());
        var text = PopupField(info, "m_text") as string;
        var callbackField = info?.GetType().GetField("m_responseCallback",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var callbackKnownMissing = callbackField != null && typeof(Delegate).IsAssignableFrom(callbackField.FieldType) &&
            callbackField.GetValue(info) == null;
        var stats = ArenaStatsStore.Data;
        var successful = _ticketSuccessfulTransaction != null && _ticketCheckoutArgs != null &&
            _ticketPurchaseConfirmed && stats != null && stats.PurchaseCounted;
        // Native StoreManager constructs this exact notice with OK and no
        // callback after its already-successful order cannot refresh balance.
        // A confirmation callback or another store notice stays untouched.
        if (!ArenaPopupPolicy.CanAcknowledgeBalanceWarning(true, "DRAFT", HasOwnedTicketPurchase,
                successful, stats?.PurchasePending != false,
                !string.IsNullOrEmpty(expected) && string.Equals(text, expected, StringComparison.Ordinal),
                string.Equals(PopupField(info, "m_responseDisplay")?.ToString(), "OK", StringComparison.Ordinal),
                callbackKnownMissing) ||
            !Equals(PopupField(info, "m_showAlertIcon"), true)) return false;
        var button = PopupField(popup, "m_okayButton") as UIBButton;
        if (!NativePopupButtonReady(button) || !ReferenceEquals(info, PopupField(popup, "m_popupInfo"))) return false;
        button.TriggerRelease();
        _ticketAcknowledgedPopups.Add(popup.GetInstanceID());
        Log("acknowledged native balance-refresh warning after successful owned GOLD ticket receipt: product=" +
            _ticketProductId + "; balance refresh remains unverified; entry receipt retained");
        return true;
    }

    private bool TryAcknowledgeOwnedTicketResult(MonoBehaviour component)
    {
        if (!HasOwnedTicketPurchase || component.GetType() != typeof(StorePurchaseAuth) ||
            _ticketAcknowledgedPopups.Contains(component.GetInstanceID())) return false;
        var transaction = PopupField(component, "m_transaction");
        if (_ticketCheckoutArgs == null || transaction == null ||
            !ReferenceEquals(transaction, _ticketSuccessfulTransaction) ||
            Convert.ToInt64(PopupProperty(transaction, "PMTProductID") ?? 0) != _ticketProductId ||
            !Equals(PopupField(component, "m_showingSuccess"), true))
        {
            LogTicketOwnershipDeferred("authorization result lacks this gold order/transaction or is not successful");
            return false;
        }
        var button = PopupField(component, "m_okButton") as UIBButton;
        if (!NativePopupButtonReady(button)) return false;
        // Use the bound button; native OnOkayButtonPressed requires UIEvent.
        button.TriggerRelease();
        _ticketAcknowledgedPopups.Add(component.GetInstanceID());
        _ticketPurchaseConfirmed = true;
        Log("acknowledged owned native gold ticket result: product=" + _ticketProductId);
        _ticketResultAcknowledged = true;
        return true;
    }

    private static bool NativePopupButtonReady(UIBButton button)
    {
        if (button == null || !button.gameObject.activeInHierarchy || !button.enabled ||
            !button.IsEnabled() || !button.IsEnabled(UIEventType.RELEASE)) return false;
        var scrolling = FindZeroArgMethod(typeof(PegUIElement), "IsScrolling",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return scrolling != null && Equals(scrolling.Invoke(button, null), false);
    }

    private void LogTicketOwnershipDeferred(string reason)
    {
        if (_ticketOwnershipDiagnostic == reason) return;
        _ticketOwnershipDiagnostic = reason;
        Log("native ticket dialog left open: " + reason + "; product=" + _ticketProductId);
    }
}

/// <summary>Read-only observations of native order, checkout and receipt; no callback suppression.</summary>
internal static class ArenaTicketPurchaseObserver
{
    internal static ArenaController Owner;
    private static bool _installed;
    private static bool _failed;

    internal static bool EnsureInstalled()
    {
        if (_installed) return true;
        if (_failed) return false;
        try
        {
            var harmony = new Harmony("local.hsmod.arenaboxbot.ticket-receipt-observer");
            var routine = AccessTools.Method(typeof(PurchaseManager), "Internal_PurchaseProductCoroutine");
            var checkout = AccessTools.Method(typeof(StoreManager), "StartStoreBuy");
            var success = AccessTools.Method(typeof(StoreManager), "HandlePurchaseSuccess");
            if (routine == null || checkout == null || success == null) throw new MissingMethodException("native gold purchase observation points unavailable");
            harmony.Patch(routine, postfix: new HarmonyMethod(typeof(ArenaTicketPurchaseObserver), nameof(ObserveRoutine)));
            harmony.Patch(checkout, prefix: new HarmonyMethod(typeof(ArenaTicketPurchaseObserver), nameof(ObserveCheckout)));
            harmony.Patch(success, prefix: new HarmonyMethod(typeof(ArenaTicketPurchaseObserver), nameof(ObserveSuccess)));
            _installed = true;
        }
        catch (Exception ex)
        {
            _failed = true;
            ArenaBoxBotPlugin.LogSource?.LogWarning("native ticket ownership observer unavailable: " + ex.GetType().Name + ": " + ex.Message);
        }
        return _installed;
    }

    private static void ObserveRoutine(object[] __args, IEnumerator __result)
    {
        try { Owner?.ObserveTicketPurchaseRoutine(__args[0], __result); }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("ticket order observation failed: " + ex.Message); }
    }

    private static void ObserveCheckout(object[] __args)
    {
        try { Owner?.ObserveTicketCheckout(__args[0]); }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("ticket checkout observation failed: " + ex.Message); }
    }

    private static void ObserveSuccess(object __instance, object[] __args)
    {
        try { Owner?.ObserveTicketPurchaseSuccess(__instance, __args[1]); }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("ticket receipt observation failed: " + ex.Message); }
    }
}
