using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ArenaBoxBot;

internal sealed partial class ArenaController
{
    private string _editVisualEntryContext;
    private string _editVisualMissingContext;
    private float _editVisualMissingSince;
    private string _editVisualDiagnostic;
    private float _editVisualNextDiagnostic;
    private string _editVisualReadyContext;

    private bool EnsureArenaRedraftEditVisual(DraftDisplay display, DraftManager manager, string context,
        int expectedSlots, out float[] pose)
    {
        pose = null;
        var screen = DraftField<ArenaDraftScreen>(display, "m_arenaDraftScreen");
        var slots = DraftField<List<RedraftCardSlot>>(display, "m_redraftCardSlots");
        // These two fields are public in the actual game assembly.
        var widgets = display.m_redraftCardSlotWidgets;
        var broadcaster = display.m_arenaDraftScreenBroadcaster;
        var containerActive = screen != null && screen.IsArenaRedraftCollectionContainerActive();
        var visualKey = context + ":display=" + display.GetInstanceID();
        var restoredFromLanding = _editEntryContext == visualKey;
        var presentationDispatched = !restoredFromLanding || _editVisualEntryContext == visualKey;
        var activeSlots = slots?.Count(slot => slot != null && slot.isActiveAndEnabled && slot.gameObject.activeInHierarchy) ?? 0;
        var activeWidgets = widgets?.Count(widget => widget != null && widget.IsReady && widget.IsActive && !widget.IsChangingStates) ?? 0;
        var hero = display.ChosenHero;
        var visualDetails = "container=" + containerActive + "; slots=" + (slots?.Count ?? 0) + "/" + expectedSlots +
            "; activeSlots=" + activeSlots + "; widgets=" + (widgets?.Count ?? 0) + "; activeWidgets=" + activeWidgets +
            "; mode=" + display.GetDraftMode() + "; visualState=" + broadcaster?.State +
            "; requestedState=" + broadcaster?.RequestedState + "; changing=" + broadcaster?.IsChangingStates +
            "; eventHasState=" + (broadcaster != null && broadcaster.HasState("CODE_SLIDEIN_REDRAFT")) +
            "; restoredFromLanding=" + restoredFromLanding + "; presentationDispatched=" + presentationDispatched +
            "; chosenHero=" + (hero == null ? "null" : hero.name + "/active=" + hero.gameObject.activeInHierarchy);
        var slotBindingsReady = expectedSlots > 0 && slots != null && widgets != null &&
            slots.Count == expectedSlots && widgets.Count == expectedSlots &&
            slots.All(slot => slot != null && slot.Index >= 0 && slot.Index < expectedSlots) &&
            slots.Select(slot => slot.Index).Distinct().Count() == expectedSlots;
        var observedPose = new List<float>();
        var visibleSlotRenderers = 0;
        if (slotBindingsReady)
        {
            for (var index = 0; index < expectedSlots; index++)
            {
                var widget = widgets[index];
                var slot = slots.Single(candidate => candidate.Index == index);
                if (widget == null || !widget.IsReady || !widget.IsActive || widget.IsChangingStates ||
                    !slot.isActiveAndEnabled || !slot.gameObject.activeInHierarchy ||
                    widget.GetComponentInChildren<RedraftCardSlot>() != slot)
                { slotBindingsReady = false; break; }
                var visible = widget.GetComponentsInChildren<Renderer>()
                    .Where(renderer => renderer != null && renderer.enabled && renderer.isVisible &&
                        renderer.gameObject.activeInHierarchy).ToArray();
                if (visible.Length == 0) { slotBindingsReady = false; break; }
                visibleSlotRenderers++;
                var position = slot.transform.position;
                observedPose.Add(position.x); observedPose.Add(position.y); observedPose.Add(position.z);
                foreach (var renderer in visible)
                {
                    var bounds = renderer.bounds;
                    observedPose.Add(bounds.center.x); observedPose.Add(bounds.center.y); observedPose.Add(bounds.center.z);
                    observedPose.Add(bounds.size.x); observedPose.Add(bounds.size.y); observedPose.Add(bounds.size.z);
                }
            }
        }
        visualDetails += "; slotsWithVisibleRenderer=" + visibleSlotRenderers;
        if (presentationDispatched && containerActive && slotBindingsReady &&
            display.GetDraftMode() == DraftDisplay.DraftMode.ACTIVE_DRAFT_DECK)
        {
            _editVisualMissingContext = null;
            pose = observedPose.ToArray();
            if (_editVisualReadyContext != visualKey)
            {
                _editVisualReadyContext = visualKey;
                Log("Arena redraft edit visual ready; " + visualDetails + "; context=" + context);
            }
            return true;
        }

        if (_editVisualMissingContext != visualKey)
        { _editVisualMissingContext = visualKey; _editVisualMissingSince = Time.unscaledTime; }
        _editVisualDiagnostic = "等待重选编辑区域完整显示（已就绪 " + activeWidgets + "/" + expectedSlots + " 个弃牌槽）";
        if (Time.unscaledTime >= _editVisualNextDiagnostic)
        {
            _editVisualNextDiagnostic = Time.unscaledTime + 10f;
            Log("Arena redraft edit visual pending; " + visualDetails + "; bound=" + slotBindingsReady + "; context=" + context);
        }
        var nativeWidget = DraftField<Hearthstone.UI.Widget>(display, "m_arenaDraftScreenWidget");
        if (_editVisualEntryContext != visualKey && Time.unscaledTime - _editVisualMissingSince >= 3f &&
            !GameUtils.IsAnyTransitionActive() && !DraftField<bool>(display, "m_phaseMessageShowing") &&
            nativeWidget != null && nativeWidget.IsReady && !nativeWidget.IsChangingStates &&
            broadcaster != null && broadcaster.HasState("CODE_SLIDEIN_REDRAFT"))
        {
            _editVisualEntryContext = visualKey;
            // OnMoveFromLanding already called SetupEditDeckTray exactly once
            // and merged the server 30+5. Complete only GoToArenaEditDeck's
            // missing desktop presentation half: never merge those cards twice.
            display.BroadcastArenaDraftScreenEvent("CODE_SLIDEIN_REDRAFT");
            display.StartCoroutine(display.SetDraftModeCoroutine(DraftDisplay.DraftMode.ACTIVE_DRAFT_DECK));
            display.ChosenHero?.Hide();
            display.DeterminePlayButtonText();
            // Native BroadcastArenaDraftScreenEvent returns void. Record the
            // real visual state/request after dispatch rather than inventing
            // a Boolean acknowledgement for this API.
            Log("Arena native redraft edit presentation dispatched once without merging deck again; event=CODE_SLIDEIN_REDRAFT" +
                "; eventHasState=True; nativeReturn=void; visualState=" + broadcaster.State +
                "; requestedState=" + broadcaster.RequestedState + "; changing=" + broadcaster.IsChangingStates +
                "; chosenHero=" + (hero == null ? "null" : hero.name + "/active=" + hero.gameObject.activeInHierarchy) +
                "; context=" + context);
        }
        return false;
    }
}
