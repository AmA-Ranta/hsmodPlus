using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace ArenaBoxBot;

internal static class ArenaDeckEditReceipts
{
    private static bool s_installed;
    private static readonly Dictionary<bool, ArenaDeckEditPlan> s_plans = new();
    internal sealed class ServerSnapshot
    {
        internal long DeckId;
        internal long RedraftDeckId;
        internal List<ArenaDeckEditCard> Cards;
        internal string Window;
    }
    private static readonly Dictionary<bool, ServerSnapshot> s_serverSnapshots = new();
    internal static ServerSnapshot LatestServerDeck(bool mode) => s_serverSnapshots.TryGetValue(mode, out var value) ? value : null;
    internal static bool Installed => s_installed;
    private static string PathFor(bool underground) => ArenaRuntimePaths.File(
        underground ? "arena-deck-edit-underground.json" : "arena-deck-edit-normal.json");

    internal static ArenaDeckEditPlan Load(bool underground)
    {
        if (s_plans.TryGetValue(underground, out var cached)) return cached;
        var path = PathFor(underground);
        if (!File.Exists(path)) return null;
        var plan = JsonConvert.DeserializeObject<ArenaDeckEditPlan>(File.ReadAllText(path));
        if (!ArenaDeckEditPolicy.IsValidPlan(plan) || plan.Underground != underground)
            throw new InvalidDataException("竞技场编辑恢复记录无效：" + path);
        s_plans[underground] = plan;
        return plan;
    }

    internal static void Save(ArenaDeckEditPlan plan)
    {
        if (!ArenaDeckEditPolicy.IsValidPlan(plan)) throw new InvalidDataException("拒绝保存无效编辑计划");
        var path = PathFor(plan.Underground);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonConvert.SerializeObject(plan, new JsonSerializerSettings { Formatting = Formatting.Indented }));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
        s_plans[plan.Underground] = plan;
    }

    internal static void EnsureInstalled()
    {
        if (s_installed) return;
        try
        {
            // ConnectAPI getters consume UnpackNextUtilPacket. Observe the
            // exact object already returned to the native handler; calling a
            // getter again from that handler's postfix would consume/lose data.
            var save = AccessTools.Method(typeof(Network), "GetSaveArenaDeckResponse");
            var contents = AccessTools.Method(typeof(Network), "GetDraftChoicesAndContents");
            if (save == null || contents == null) throw new MissingMethodException("Arena edit server acknowledgement handlers");
            var harmony = new Harmony("local.hsmod.arenaboxbot.deck-edit-receipts");
            harmony.Patch(save, postfix: new HarmonyMethod(typeof(ArenaDeckEditReceipts), nameof(AfterSave)));
            harmony.Patch(contents, postfix: new HarmonyMethod(typeof(ArenaDeckEditReceipts), nameof(AfterContents)));
            s_installed = true;
        }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("Arena edit receipt hooks unavailable: " + ex.Message); }
    }

    private static List<ArenaDeckEditCard> Aggregate(IEnumerable<ArenaDeckEditCard> cards) => cards
        .GroupBy(card => card.Key, StringComparer.Ordinal).Select(group => new ArenaDeckEditCard
            { CardId = group.First().CardId, Premium = group.First().Premium, Count = group.Sum(card => card.Count) }).ToList();

    private static void AfterSave(PegasusUtil.SaveArenaDeckResponse __result)
    {
        try
        {
            var contents = __result?.Decks;
            if (contents?.Cards == null) return;
            var cards = Aggregate(contents.Cards.Select(card => new ArenaDeckEditCard
            {
                CardId = GameUtils.TranslateDbIdToCardId(card.Def.Asset, false),
                Premium = card.Def.Premium, Count = card.Qty
            }));
            foreach (var mode in new[] { false, true })
                Confirm(Load(mode), contents.Success, contents.DeckId, cards, "SaveArenaDeckResponse");
        }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("Arena edit save receipt failed: " + ex.Message); }
    }

    private static void AfterContents(Network.DraftChoicesAndContents __result)
    {
        try
        {
            var packet = __result;
            if (packet?.DeckInfo?.Cards == null || packet.Session == null) return;
            var cards = Aggregate(packet.DeckInfo.Cards.Select(card => new ArenaDeckEditCard
            {
                CardId = GameUtils.TranslateDbIdToCardId(card.DbId, false),
                Premium = (int)card.Premium, Count = card.Count
            }));
            if (ArenaDeckEditPolicy.IsValidCards(cards))
            {
                var window = ArenaDeckDiscardStream.NativeWindow(packet.IsUnderground, packet.DeckInfo.Deck,
                    packet.RedraftDeckId, packet.Wins, packet.Losses);
                s_serverSnapshots[packet.IsUnderground] = new ServerSnapshot
                    { DeckId = packet.DeckInfo.Deck, RedraftDeckId = packet.RedraftDeckId, Cards = cards, Window = window };
                if (packet.IsUnderground == ArenaSettings.IsUnderground)
                    ArenaDeckDiscardStream.BeginNativeWindow(window, packet.Session.State.ToString());
            }
            if (packet.Session.State.ToString() != "MIDRUN") return;
            // A fresh server packet proves persistence after disconnect/restart.
            // Local MIDRUN, local 30 cards or a stale file never prove a save.
            Confirm(Load(packet.IsUnderground), true, packet.DeckInfo.Deck, cards, "DraftChoicesAndContents/MIDRUN");
        }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("Arena edit recovery receipt failed: " + ex.Message); }
    }

    private static void Confirm(ArenaDeckEditPlan plan, bool success, long deckId,
        IList<ArenaDeckEditCard> cards, string source)
    {
        if (plan == null || plan.Stage != "Saving" || plan.DeckId != deckId) return;
        if (!ArenaDeckEditPolicy.ServerConfirms(plan, success, deckId, cards))
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena edit receipt did not confirm intended deck: source=" + source +
                "; deck=" + deckId + "; success=" + success + "; actual=" + ArenaDeckEditPolicy.Fingerprint(cards) +
                "; expected=" + ArenaDeckEditPolicy.Fingerprint(plan.Target));
            return;
        }
        plan.Stage = "Confirmed";
        plan.ConfirmedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        plan.ConfirmationSource = source;
        Save(plan);
        ArenaBoxBotPlugin.LogSource?.LogInfo("Arena edited deck server-confirmed: deck=" + deckId +
            "; cards=" + plan.TargetCount + "; source=" + source + "; context=" + plan.ContextKey);
    }
}

internal sealed partial class ArenaController
{
    private readonly ArenaDraftVisualGate _editVisualGate = new();
    private string _editActionKey;
    private float _editActionReadyAt;
    private float _editNextDiagnostic;
    private float _editWaitStarted = -1f;
    private string _editWaitContext;
    private string _editRecoveryRequestedContext;
    private string _editEntryContext;
    private string _editEntryRefreshContext;

    private static List<ArenaDeckEditCard> ReadEditCards(CollectionDeck deck)
    {
        var cards = new List<ArenaDeckEditCard>();
        if (deck?.GetSlots() == null) throw new InvalidDataException("原生编辑套牌尚未加载");
        var premiums = Enum.GetValues(typeof(TAG_PREMIUM)).Cast<TAG_PREMIUM>()
            .Where(premium => (int)premium >= 0 && (int)premium < (int)TAG_PREMIUM.MAX).Distinct().ToList();
        foreach (var slot in deck.GetSlots())
        {
            if (slot == null || slot.Count <= 0) continue;
            var observed = 0;
            foreach (var premium in premiums)
            {
                var count = slot.GetCount(premium);
                if (count <= 0) continue;
                cards.Add(new ArenaDeckEditCard { CardId = slot.CardID, Premium = (int)premium, Count = count });
                observed += count;
            }
            if (observed != slot.Count) throw new InvalidDataException("无法完整读取卡牌所有品质份数：" + slot.CardID);
        }
        if (!ArenaDeckEditPolicy.IsValidCards(cards)) throw new InvalidDataException("原生编辑卡牌列表不完整");
        return cards;
    }

    private bool HandleArenaDeckEditing(DraftManager manager, string arenaState)
    {
        // This is independent of AutoMatch. AutoDraft/AutoRedraft own deck
        // editing; AutoMatch controls only the next already-valid match.
        ArenaDeckEditReceipts.EnsureInstalled();
        var mode = manager.IsUnderground();
        var deck = manager.GetDraftDeck(mode);
        var plan = ArenaDeckEditReceipts.Load(mode);
        var pending = plan != null && plan.Stage != "Confirmed";
        if (arenaState != "EDITING_DECK" && !pending) return false;
        if (mode != ArenaSettings.IsUnderground || deck == null || deck.ID <= 0)
            return HoldArenaDeckEdit("等待原生编辑套牌身份", "identity");
        if (!ArenaDeckEditReceipts.Installed)
            return HoldArenaDeckEdit("保存确认监听未就绪，禁止编辑与匹配", "hooks");
        if (pending && (plan.DeckId != deck.ID || plan.SeasonId != manager.CurrentSeasonId))
        {
            RequestArenaStop("上次竞技场套牌保存尚未确认，当前套牌身份已变化；请查看编辑恢复日志");
            return true;
        }
        if (plan?.Stage == "Saving")
        {
            var context = plan.ContextKey;
            if (_editRecoveryRequestedContext != context &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - plan.SaveRequestedAtUnixMs >= 10000)
            {
                _editRecoveryRequestedContext = context;
                Network.Get()?.RequestDraftChoicesAndContents(mode);
                Log("Arena edit save awaiting receipt; requested authoritative deck contents once; context=" + context);
            }
            return HoldArenaDeckEdit("等待服务器确认编辑后的完整套牌", context);
        }
        if (arenaState != "EDITING_DECK")
            return HoldArenaDeckEdit("未提交的竞技场编辑尚未恢复，禁止匹配", plan?.ContextKey ?? "pending");
        var display = GetDraftDisplay();
        if (display == null || display != DraftDisplay.Get() || !display.IsLoadingComplete() || display.IsRewardsFlowPending)
            return HoldArenaDeckEdit("等待原生编辑界面加载", "display");
        var cards = ReadEditCards(deck);
        var redraftDeck = manager.GetRedraftDeck();
        var redraftId = redraftDeck?.ID ?? 0;
        var redraftEditing = (redraftDeck?.GetTotalCardCount() ?? 0) > 0 ||
            (plan != null && plan.Stage != "Confirmed" && plan.Original.Sum(card => card.Count) > plan.TargetCount);
        if (!ArenaSettings.AutoDraft.Value || (redraftEditing && !ArenaSettings.AutoRedraft.Value))
            return HoldArenaDeckEdit("自动编辑已关闭，请先完成套牌编辑", "manual", false);
        var client = manager.GetCurrentClientState().ToString();
        var baseContext = (mode ? "U:" : "N:") + manager.CurrentSeasonId + ":" + deck.ID + ":" + redraftId +
            ":window=" + ArenaDeckEditReceipts.LatestServerDeck(mode)?.Window;
        if (client != (mode ? "Underground_DeckEdit" : "Normal_DeckEdit"))
        {
            // Never repeat SetupEditDeckTray: it adds the replacement deck to
            // the editable deck. Enter through the native client transition.
            var entryKey = baseContext + ":display=" + display.GetInstanceID();
            if (_editEntryContext != entryKey && !GameUtils.IsAnyTransitionActive() &&
                (client.EndsWith("_Landing", StringComparison.Ordinal) || client.EndsWith("_Ready", StringComparison.Ordinal)))
            {
                var authoritative = ArenaDeckEditReceipts.LatestServerDeck(mode);
                if (authoritative == null || authoritative.DeckId != deck.ID || authoritative.RedraftDeckId != redraftId ||
                    ArenaDeckEditPolicy.Fingerprint(authoritative.Cards) != ArenaDeckEditPolicy.Fingerprint(cards))
                {
                    if (_editEntryRefreshContext != entryKey && Network.Get() != null)
                    {
                        _editEntryRefreshContext = entryKey;
                        Network.Get().RequestDraftChoicesAndContents(mode);
                        Log("Arena edit entry awaiting authoritative base deck before native merge; context=" + entryKey);
                    }
                    return HoldArenaDeckEdit("等待服务器恢复编辑前套牌，避免重复合入重选牌", entryKey);
                }
                _editEntryContext = entryKey;
                var landing = ArenaLandingPageManager.Get();
                var landingWidget = landing?.GetWidget();
                if (landing == null || !landing.IsLoadingComplete() || landingWidget == null ||
                    !landingWidget.IsReady || landingWidget.IsChangingStates)
                { _editEntryContext = null; return HoldArenaDeckEdit("等待原生落地页面就绪后进入编辑", baseContext); }
                var accepted = landingWidget.TriggerEvent("MOVETO_DRAFTING", default);
                Log("Arena edit native entry requested once; client=" + client + "; accepted=" + accepted +
                    "; context=" + baseContext);
            }
            return HoldArenaDeckEdit("等待原生套牌编辑页面", baseContext);
        }
        var visibleCount = DraftField<int>(display, "m_deckCount");
        var targetCount = DraftField<int>(display, "m_maxDeckCount");
        var count = cards.Sum(card => card.Count);
        if (targetCount <= 0 || count != visibleCount)
            return HoldArenaDeckEdit("等待套牌数量与原生编辑界面一致", baseContext);
        var contextKey = baseContext + ":" + targetCount + ":" + ArenaDeckEditPolicy.Fingerprint(cards);
        var editPose = new[] { (float)count, (float)targetCount };
        if (redraftEditing)
        {
            if (!EnsureArenaRedraftEditVisual(display, manager, baseContext, manager.GetMaxRedraftSlot() + 1, out var slotPose))
                return HoldArenaDeckEdit(_editVisualDiagnostic, baseContext);
            editPose = editPose.Concat(slotPose).ToArray();
        }
        var nativeWidget = DraftField<Hearthstone.UI.Widget>(display, "m_arenaDraftScreenWidget");
        var ready = display.isActiveAndEnabled && nativeWidget != null && nativeWidget.IsReady && nativeWidget.IsActive && !nativeWidget.IsChangingStates &&
            !GameUtils.IsAnyTransitionActive() && !DraftField<bool>(display, "m_phaseMessageShowing");
        if (!_editVisualGate.Observe("edit:" + baseContext, contextKey, editPose,
            ready, Time.unscaledTime, Time.frameCount))
            return HoldArenaDeckEdit("等待编辑页面和卡牌数量持续稳定", baseContext);
        if (plan == null || plan.Stage == "Confirmed")
        {
            var server = ArenaDeckEditReceipts.LatestServerDeck(mode);
            // OnChosen appends only server-acknowledged choices to the native
            // replacement deck. Before any deletion, combine those exact
            // copies with the server's saved main deck, including premiums.
            var merged = server != null && server.DeckId == deck.ID && server.RedraftDeckId == redraftId
                ? server.Cards.Concat((redraftDeck?.GetTotalCardCount() ?? 0) > 0
                    ? ReadEditCards(redraftDeck) : new List<ArenaDeckEditCard>())
                    .GroupBy(card => card.Key, StringComparer.Ordinal).Select(group => new ArenaDeckEditCard
                    { CardId = group.First().CardId, Premium = group.First().Premium, Count = group.Sum(card => card.Count) }).ToList()
                : null;
            if (!ArenaDeckEditPolicy.CanStartPlan(redraftEditing, cards, merged))
                return HoldArenaDeckEdit("重选后的完整编辑套牌尚未一致；不能把旧的 " + targetCount +
                    " 张套牌当作已完成弃牌", baseContext);
            var recommendation = count > targetCount && server != null
                ? ArenaDeckDiscardStream.Read(server.Window, contextKey, cards, merged, targetCount) : null;
            var removals = count == targetCount ? new Dictionary<string, int>() : recommendation?.RemoveCounts;
            if (removals == null)
                return HoldArenaDeckEdit("等待盒子对当前完整套牌的明确弃牌建议（" + count + "/" + targetCount + "）：" +
                    ArenaDeckDiscardStream.Diagnostic, contextKey);
            if (!ArenaDeckEditPolicy.TryPlan(cards, targetCount, removals, out var target, out var reason))
                return HoldArenaDeckEdit("盒子弃牌建议尚未通过校验：" + reason, contextKey);
            plan = new ArenaDeckEditPlan { Underground = mode, SeasonId = manager.CurrentSeasonId, DeckId = deck.ID,
                RedraftDeckId = redraftId, TargetCount = targetCount, ContextKey = contextKey,
                RecommendationEvidence = recommendation?.Evidence ?? "native exact-count edit completion",
                Original = cards, Target = target };
            ArenaDeckEditReceipts.Save(plan);
            _editWaitStarted = -1f;
            Log("Arena edit plan persisted before mutation: context=" + contextKey + "; target=" +
                ArenaDeckEditPolicy.Fingerprint(target) + "; Box evidence=" + plan.RecommendationEvidence);
        }
        if (plan.RedraftDeckId != redraftId)
        {
            RequestArenaStop("竞技场编辑的临时套牌身份已改变，未继续弃牌或匹配");
            return true;
        }
        if (!ArenaDeckEditPolicy.TryNextRemoval(plan, cards, out var removal, out var invalid))
        {
            RequestArenaStop("竞技场编辑套牌与已保存的盒子建议不一致：" + invalid);
            return true;
        }
        var actionKey = contextKey + (removal == null ? ":save" : ":remove:" + removal.Key);
        if (_editActionKey != actionKey)
        {
            _editActionKey = actionKey;
            _editActionReadyAt = Time.unscaledTime + ArenaDraftPacing.NextDelay(ArenaSettings.UseHumanDraftPace, s_draftPaceRandom);
            return true;
        }
        if (Time.unscaledTime < _editActionReadyAt) return true;
        if (removal != null)
        {
            // The native callback swaps rather than removes when a filled
            // replacement slot is hovered. Do not manipulate private input
            // state; wait until the user/native UI naturally releases it.
            var hovered = DraftField<RedraftCardSlot>(display, "m_hoveredCardSlot");
            if (!string.IsNullOrEmpty(DraftField<string>(display, "m_draggingCardId")) ||
                DraftField<bool>(display, "m_isDraggingCardFromCollection") || display.IsDraggingCardFromDeck() ||
                (hovered != null && hovered.HasCard))
                return HoldArenaDeckEdit("等待鼠标结束拖拽并离开已占用的重选卡槽", plan.ContextKey);
            if (!display.RemoveCardFromArenaDeck(removal.CardId, (TAG_PREMIUM)removal.Premium))
                return HoldArenaDeckEdit("原生编辑暂不能移除盒子推荐卡牌：" + removal.CardId, plan.ContextKey);
            var after = ReadEditCards(deck);
            if (!ArenaDeckEditPolicy.RemovedExactly(cards, after, removal))
            {
                RequestArenaStop("原生弃牌后的数量变化异常，已停止以避免重复移除");
                return true;
            }
            _editActionKey = null;
            _editVisualGate.Reset();
            _editWaitStarted = -1f;
            Log("Arena native recommended discard: card=" + removal.CardId + "; premium=" + removal.Premium +
                "; quantity=1; count=" + count + "->" + (count - 1) + "; context=" + plan.ContextKey);
            return true;
        }
        // Exactly the intended multiset is now on screen. Arm the persistent
        // receipt latch before invoking the native finish button callback.
        if (!string.IsNullOrEmpty(DraftField<string>(display, "m_draggingCardId")) ||
            DraftField<bool>(display, "m_isDraggingCardFromCollection") || display.IsDraggingCardFromDeck())
            return HoldArenaDeckEdit("等待原生卡牌拖拽完成后保存套牌", plan.ContextKey);
        plan.Stage = "Saving";
        plan.SaveRequestedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ArenaDeckEditReceipts.Save(plan);
        var finish = AccessTools.Method(typeof(DraftDisplay), "OnEditDeckButtonReleased");
        if (finish == null) throw new MissingMethodException("DraftDisplay.OnEditDeckButtonReleased");
        try
        {
            finish.Invoke(display, null);
            _editWaitStarted = -1f;
            Log("Arena native edit finish submitted once; waiting for successful server deck receipt; context=" + plan.ContextKey);
        }
        catch (Exception ex) { Log("Arena edit finish returned an error; retaining save receipt latch: " + ex.Message); }
        return true;
    }

    private bool HoldArenaDeckEdit(string reason, string context, bool bounded = true)
    {
        s_arenaOverlayPhase = reason;
        if (_editWaitContext != context || _editWaitStarted < 0f)
        { _editWaitContext = context; _editWaitStarted = Time.unscaledTime; }
        if (Time.unscaledTime >= _editNextDiagnostic)
        {
            _editNextDiagnostic = Time.unscaledTime + 10f;
            Log("Arena deck edit waiting: " + reason + "; context=" + context);
        }
        if (bounded && Time.unscaledTime - _editWaitStarted >= 120f)
            RequestArenaStop("竞技场编辑超过 120 秒未得到有效推进：" + reason + "；未绕过编辑开始匹配");
        return true;
    }
}
