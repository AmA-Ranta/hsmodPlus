using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ArenaBoxBot;

/// <summary>
/// An observational bridge. Unity sampling and native receive hooks publish
/// immutable values; HTTP callers never access game objects or send actions.
/// </summary>
internal static class ArenaActivityRuntime
{
    private static readonly ArenaActivityEvidence s_evidence = new ArenaActivityEvidence();
    private static volatile ArenaActivitySnapshot s_snapshot;
    private static long s_sampleUtcTicks;
    private static long s_version;
    private static GameState s_game;
    private static string s_context = "";
    private static float s_nextSample, s_nextHookAttempt, s_nextWarning;
    private static Harmony s_harmony;
    private static bool s_powerHook, s_choiceHook;

    internal static ArenaActivitySnapshot Current
    {
        get
        {
            var snapshot = s_snapshot;
            if (snapshot == null) return null;
            // This getter is also used on HTTP worker threads. DateTime and
            // an immutable snapshot are safe here; Unity Time is not.
            var age = DateTime.UtcNow.Ticks - Interlocked.Read(ref s_sampleUtcTicks);
            return age >= 0 && age <= TimeSpan.FromSeconds(5).Ticks ? snapshot :
                new ArenaActivitySnapshot(-1, "等待竞技场状态刷新", "尚无已确认动作", null, null);
        }
    }

    internal static void SampleMainThread()
    {
        EnsureHooks();
        if (Time.unscaledTime < s_nextSample) return;
        s_nextSample = Time.unscaledTime + 0.25f;
        try
        {
            TryReportUnfilledChoice();
            if (IsAutomationActive())
            {
                var assembleState = GameState.Get();
                var assembleChoice = assembleState?.GetFriendlyEntityChoices();
                if (assembleChoice != null) TryExecuteAssembleRecommendation(assembleState, assembleChoice);
            }
            LogChoiceSubmitApiOnce();
            if (!HsModNativeArena.IsNativeArenaModeSelected())
            {
                ClearGame();
                s_snapshot = null;
                return;
            }
            var enabled = IsAutomationActive();
            var scene = SceneMgr.Get();
            var mode = scene?.GetMode().ToString() ?? "INITIALIZING";
            var game = GameMgr.Get();
            var arenaGame = mode == "GAMEPLAY" && game != null &&
                (game.IsArena() || game.IsUndergroundArena());
            string status;
            if (!enabled)
            {
                ClearGame();
                status = arenaGame ? "竞技场脚本已关闭（对局仍在进行）" : "竞技场脚本已关闭";
            }
            else if (scene != null && scene.IsTransitioning())
            {
                ClearGame();
                status = "页面切换中";
            }
            else if (arenaGame)
            {
                var state = GameState.Get();
                if (state == null || !ObserveGame(state, game.GetGameHandle())) status = "竞技场对局载入中";
                else if (state.IsGameOverNowOrPending()) status = "对局结算中";
                else if (state.IsMulliganPhaseNowOrPending()) status = "起手留牌中";
                else if (state.IsInChoiceMode() && state.GetFriendlyEntityChoices() != null) status = "对局中选择卡牌";
                else status = "第 " + state.GetTurn() + " 回合 · " +
                    (state.IsFriendlySidePlayerTurn() ? "我方回合" : "对手回合");
                status += CurrentRunText(game.IsUndergroundArena());
            }
            else
            {
                ClearGame();
                status = PhaseText(mode, scene != null && scene.IsTransitioning());
            }
            Publish(status);
        }
        catch (Exception ex)
        {
            ClearGame();
            Publish("竞技场状态暂不可读", false);
            Warn("Arena activity sampling deferred: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string PhaseText(string scene, bool transitioning)
    {
        if (transitioning) return "页面切换中";
        if (scene == "HUB") return "等待进入竞技场";
        if (scene != "DRAFT") return "等待竞技场页面（" + scene + "）";
        var manager = DraftManager.Get();
        if (manager == null || manager.CurrentSeason == null) return "竞技场信息载入中";
        var underground = manager.IsUnderground();
        var state = manager.GetArenaState(underground).ToString();
        switch (state)
        {
            case "DRAFTING":
            case "REDRAFTING":
                var slot = manager.IsRedrafting() ? manager.GetRedraftSlot() : manager.GetSlot();
                var maximum = manager.IsRedrafting() ? manager.GetMaxRedraftSlot() : manager.GetMaxSlot();
                var type = manager.GetSlotType().ToString();
                var selection = type == "DRAFT_SLOT_HERO" ? "选择英雄" :
                    type == "DRAFT_SLOT_HERO_POWER" ? "选择英雄技能" : "选择卡牌";
                return (state == "REDRAFTING" ? "重建套牌 · " : "") + selection + "（" + slot + "/" + maximum + "）";
            case "MIDRUN": return "竞技场套牌就绪，等待对局" + CurrentRunText(underground);
            case "REWARDS": return "领取竞技场奖励";
            case "NO_RUN": return "等待新竞技场轮次";
            default: return "竞技场：" + state;
        }
    }

    private static string CurrentRunText(bool underground)
    {
        var stats = ArenaStatsStore.Data;
        return stats != null && stats.RunActive && stats.RunUnderground == underground &&
            stats.CurrentRunWins >= 0 && stats.CurrentRunLosses >= 0
            ? " · 本轮 " + stats.CurrentRunWins + " 胜 / " + stats.CurrentRunLosses + " 负" : "";
    }

    private static void ClearGame()
    {
        s_game = null;
        s_context = "";
        s_evidence.SetContext("", false);
    }

    private static bool ObserveGame(GameState state, int handle)
    {
        if (state == null || handle == 0) { ClearGame(); return false; }
        var context = handle + ":" + RuntimeHelpers.GetHashCode(state);
        if (!ReferenceEquals(state, s_game) || context != s_context)
        {
            s_game = state;
            s_context = context;
            s_evidence.SetContext(context, true);
        }
        return true;
    }

    private static bool CanObserve(GameState state)
    {
        if (!HsModNativeArena.IsNativeArenaModeSelected() || !HsModNativeArena.IsRuntimeEnabled())
        {
            ClearGame();
            return false;
        }
        var scene = SceneMgr.Get();
        var game = GameMgr.Get();
        return scene != null && !scene.IsTransitioning() && scene.GetMode() == SceneMgr.Mode.GAMEPLAY && game != null &&
            (game.IsArena() || game.IsUndergroundArena()) && ReferenceEquals(state, GameState.Get()) &&
            ObserveGame(state, game.GetGameHandle());
    }

    private static void Publish(string status, bool readStats = true)
    {
        int? wins = null, losses = null;
        var stats = readStats ? ArenaStatsStore.Data : null;
        if (stats != null && stats.TotalWins >= 0 && stats.TotalLosses >= 0)
        {
            // These are Arena's persisted lifetime counters, not HsMod's
            // constructed session counters or the current run's baseline.
            wins = stats.TotalWins; losses = stats.TotalLosses;
        }
        var current = s_snapshot;
        if (current == null || current.Status != status || current.Action != s_evidence.Action ||
            current.Wins != wins || current.Losses != losses)
            s_snapshot = new ArenaActivitySnapshot(++s_version, status, s_evidence.Action, wins, losses);
        Interlocked.Exchange(ref s_sampleUtcTicks, DateTime.UtcNow.Ticks);
    }

    private static void EnsureHooks()
    {
        if ((s_powerHook && s_choiceHook) || Time.unscaledTime < s_nextHookAttempt) return;
        s_nextHookAttempt = Time.unscaledTime + 5f;
        try
        {
            if (s_harmony == null) s_harmony = new Harmony("local.hsmod.arenaboxbot.activity-observer");
            if (!s_powerHook)
            {
                var original = AccessTools.Method(typeof(GameState), "OnPowerHistory",
                    new[] { typeof(List<Network.PowerHistory>) });
                if (original == null) throw new MissingMethodException("GameState.OnPowerHistory");
                s_harmony.Patch(original, prefix: new HarmonyMethod(typeof(ArenaActivityRuntime), nameof(BeforePowerHistory)));
                s_powerHook = true;
            }
            if (!s_choiceHook)
            {
                var original = AccessTools.Method(typeof(GameState), "OnEntitiesChosenProcessed",
                    new[] { typeof(Network.EntitiesChosen) });
                if (original == null) throw new MissingMethodException("GameState.OnEntitiesChosenProcessed");
                s_harmony.Patch(original, prefix: new HarmonyMethod(typeof(ArenaActivityRuntime), nameof(BeforeEntitiesChosen)));
                s_choiceHook = true;
            }
        }
        catch (Exception ex) { Warn("Arena activity observation hook unavailable: " + ex.GetType().Name + ": " + ex.Message); }
    }

    // Both hooks return void, receive no ref/out parameters and swallow only
    // their own diagnostic failures. They cannot skip or alter native work.
    private static void BeforePowerHistory(GameState __instance, List<Network.PowerHistory> __0)
    {
        try
        {
            if (__0 == null || !CanObserve(__instance)) return;
            var friendly = __instance.GetFriendlySidePlayer();
            if (friendly == null) return;
            // Native history is queued for later processing. The entity map
            // cannot establish ownership while older work remains, or when
            // this batch changes identity/control before a block starts.
            var processor = __instance.GetPowerProcessor();
            var identityChanges = __0.Any(power =>
                power is Network.HistFullEntity || power is Network.HistShowEntity ||
                power is Network.HistChangeEntity ||
                (power is Network.HistTagChange change &&
                 ArenaActivityEvidence.IsOwnershipOrIdentityTag(change.Tag)));
            var canReadOwnership = ArenaActivityEvidence.CanReadPowerOwners(
                processor != null, processor == null || processor.HasTaskLists(), identityChanges);
            foreach (var power in __0)
            {
                if (canReadOwnership && power is Network.HistBlockStart block &&
                    block.Entities != null && block.Entities.Count == 1)
                {
                    var source = __instance.GetEntity(block.Entities[0]);
                    var owner = source?.GetController();
                    if (source == null || owner == null || owner.GetPlayerId() != __instance.GetFriendlyPlayerId()) continue;
                    var type = block.BlockType.ToString();
                    var heroPower = source.GetCardType().ToString() == "HERO_POWER";
                    // POWER from a minion/enchantment is commonly automatic;
                    // it must not be presented as a chosen script action.
                    s_evidence.ObservePower(s_context, type, true, heroPower, Label(__instance, source.GetEntityId()),
                        block.Target > 0 ? Label(__instance, block.Target) : "");
                }
                else if (power is Network.HistTagChange tag && tag.Entity == friendly.GetEntityId() &&
                    tag.Tag == (int)GAME_TAG.CURRENT_PLAYER)
                    s_evidence.ObserveCurrentPlayer(s_context, tag.Value);
            }
            Publish(s_snapshot?.Status ?? "竞技场对战中");
        }
        catch (Exception ex) { Warn("Arena activity power observation deferred: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static void BeforeEntitiesChosen(GameState __instance, Network.EntitiesChosen __0)
    {
        try
        {
            if (__0 == null || !CanObserve(__instance)) return;
            var expected = __instance.GetFriendlyEntityChoices();
            if (expected == null || expected.Entities == null || __0.Entities == null) return;
            var match = __0.Entities.Distinct().Count() == __0.Entities.Count &&
                __0.Entities.All(id => expected.Entities.Contains(id)) &&
                expected.ChoiceType.ToString() == __0.ChoiceType.ToString() &&
                __0.Entities.Count >= expected.CountMin && __0.Entities.Count <= expected.CountMax &&
                (__0.Entities.Count > 0 || __0.ChoiceType.ToString() == "MULLIGAN");
            var labels = string.Join("、", __0.Entities.Select(id => Label(__instance, id)));
            s_evidence.ObserveChoice(s_context, expected.ID, __0.ID,
                __0.PlayerId == __instance.GetFriendlyPlayerId(), match,
                __0.ChoiceType.ToString() == "MULLIGAN", labels);
            Publish(s_snapshot?.Status ?? "对局中选择卡牌");
        }
        catch (Exception ex) { Warn("Arena activity choice observation deferred: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static string Label(GameState state, int id)
    {
        var entity = state.GetEntity(id);
        // GetName may initialize native EntityDef/name resources while a
        // received packet is being processed. GetCardId reads the existing
        // entity field, so this observer never loads or changes card data.
        var name = entity?.GetCardId();
        return (string.IsNullOrWhiteSpace(name) ? "实体" : name.Replace('\r', ' ').Replace('\n', ' ')) + "#" + id;
    }

    private static int s_lastChoiceId = -1;
    private static float s_lastChoiceSeenAt;
    private static readonly HashSet<int> s_reportedChoices = new();

    /// <summary>Logs a friendly choice that stays open without a local submission,
    /// so arena tests can tell whether 拼装/Assemble choices are actually executed.</summary>
    private static void TryReportUnfilledChoice()
    {
        try
        {
            // The fallback exists only for a started run.  When the user plays by
            // hand (script switched off) the plugin must never choose anything.
            if (!IsAutomationActive()) { s_lastChoiceId = -1; return; }
            var state = GameState.Get();
            var choice = state?.GetFriendlyEntityChoices();
            if (choice == null) { s_lastChoiceId = -1; return; }
            if (choice.ID != s_lastChoiceId)
            {
                s_lastChoiceId = choice.ID;
                s_lastChoiceSeenAt = Time.unscaledTime;
                // Observation only: when a 拼装/Assemble style choice stalls, the
                // log must tell which choice type and how many entities it offered,
                // otherwise the fallback looks like it never ran.
                Warn("choice observed: id=" + choice.ID + ";type=" + choice.ChoiceType +
                    ";min=" + choice.CountMin + ";max=" + choice.CountMax +
                    ";entities=" + (choice.Entities?.Count ?? 0));
                return;
            }
            var age = Time.unscaledTime - s_lastChoiceSeenAt;
            // HsMod normally executes a choice within 2-4s; only a truly
            // stranded choice (e.g. assemble without a box recommendation) waits longer.
            // The assemble UI is often built a moment after the choice packet
            // arrives, so a "not assemble" verdict must not be cached forever:
            // re-evaluate while the choice stays open (throttled).
            if (!s_assembleChoices.TryGetValue(choice.ID, out var assembleChoice) || !assembleChoice)
            {
                if (Time.unscaledTime >= s_nextAssembleProbe)
                {
                    s_nextAssembleProbe = Time.unscaledTime + 0.5f;
                    assembleChoice = IsAssembleChoice(state, choice);
                    s_assembleChoices[choice.ID] = assembleChoice;
                }
            }
            if (!AssembleFallbackPolicy.ShouldSubmit(choice.ChoiceType.ToString(), assembleChoice, age,
                    s_submittedFallbackChoices.Contains(choice.ID))) return;
            if (choice.Entities == null || choice.Entities.Count == 0) return;
            // Mark the choice only after the native submit succeeded: marking it
            // first meant one failed reflection call stranded the choice forever.
            if (SubmitFallbackGeneralChoice(state, choice)) s_submittedFallbackChoices.Add(choice.ID);
            if (!s_reportedChoices.Add(choice.ID)) return;
            var ids = choice.Entities == null ? "" : string.Join("、", choice.Entities.Select(id => Label(state, id)));
            Warn("choice unfilled: id=" + choice.ID + ";type=" + choice.ChoiceType +
                ";min=" + choice.CountMin + ";max=" + choice.CountMax +
                ";entities=" + (choice.Entities?.Count ?? 0) + ";ageSec=" + (int)age + ";cards=" + ids);
        }
        catch { }
    }
    private static readonly HashSet<int> s_assembleRecommendationSubmitted = new();
    private static string s_assembleRecommendationRequestId = "";
    private static float s_assembleRecommendationSeenAt;

    /// <summary>HsMod's executor does not apply the box's Assemble ("拼装")
    /// recommendation, so the plugin submits the recommended choice position
    /// through the same native API the client uses (AddChosenEntity + SendChoices).</summary>
    /// <summary>Automation is active when HsMod's arena runtime switch is on, or
    /// when its global script switch is on (wild ladder and every other mode).</summary>
    private static bool IsAutomationActive()
    {
        try
        {
            HsModScriptState.EnsureStarted();
            return HsModNativeArena.IsRuntimeEnabled() || HsModScriptState.Enabled;
        }
        catch { return HsModNativeArena.IsRuntimeEnabled(); }
    }

    private static void TryExecuteAssembleRecommendation(GameState state, Network.EntityChoices choice)
    {
        try
        {
            if (!IsAutomationActive()) return;
            if (state == null || choice == null || choice.Entities == null || choice.Entities.Count == 0) return;
            if (s_assembleRecommendationSubmitted.Contains(choice.ID)) return;
            var path = ArenaRuntimePaths.File("boxai-recommendation.json");
            if (!System.IO.File.Exists(path)) return;
            var root = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path));
            var action = (root["action"]?.ToString() ?? "").Trim().ToLowerInvariant();
            var rawText = (root["rawText"]?.ToString() ?? "").Trim().ToLowerInvariant();
            var isAssemble = action == "assemble" || rawText.IndexOf("assemble", System.StringComparison.Ordinal) >= 0;
            if (!isAssemble) return;
            var gamePid = int.TryParse(root["gamePid"]?.ToString(), out var parsedPid) ? parsedPid : 0;
            if (gamePid != 0 && gamePid != System.Diagnostics.Process.GetCurrentProcess().Id) return;
            var requestId = root["requestId"]?.ToString() ?? "";
            // A stale file (box stopped publishing) must never be applied to a new
            // choice: the position would point at an unrelated entity.
            var capturedText = root["capturedAt"]?.ToString() ?? "";
            if (!DateTimeOffset.TryParse(capturedText, out var capturedAt)) return;
            if ((DateTimeOffset.Now - capturedAt).TotalSeconds > 5) return;
            var position = int.TryParse(root["choicePosition"]?.ToString(), out var parsedPosition) ? parsedPosition : -1;
            if (!string.Equals(s_assembleRecommendationRequestId, requestId, System.StringComparison.Ordinal))
            {
                s_assembleRecommendationRequestId = requestId;
                s_assembleRecommendationSeenAt = Time.unscaledTime;
                ArenaBoxBotPlugin.LogSource?.LogWarning("assemble recommendation seen: requestId=" + requestId +
                    ";action=" + action + ";position=" + position + ";entities=" + choice.Entities.Count);
            }
            var age = Time.unscaledTime - s_assembleRecommendationSeenAt;
            if (!AssembleRecommendationPolicy.ShouldSubmit(isAssemble, requestId, s_assembleRecommendationRequestId,
                    position, choice.Entities.Count, age, s_assembleRecommendationSubmitted.Contains(choice.ID))) return;
            var entityId = choice.Entities[position];
            if (HasPendingChosenEntities(state)) return;
            if (!TryPublishChosenEntities(state, choice, new List<int> { entityId })) return;
            if (!TrySendChoices(state)) return;
            s_assembleRecommendationSubmitted.Add(choice.ID);
            ArenaBoxBotPlugin.LogSource?.LogWarning("assemble recommendation submitted: id=" + choice.ID +
                ";requestId=" + requestId + ";position=" + position + ";entity=" + entityId + ";age=" + age.ToString("0.0"));
        }
        catch { }
    }
    private static float s_nextAssembleProbe;
    private static readonly Dictionary<int, bool> s_assembleChoices = new();
    private static readonly HashSet<int> s_assembleChecked = new();

    /// <summary>True only for the Assemble ("拼装") keyword choice: its source card
    /// carries the 拼装/Assemble keyword text.  Discover/other GENERAL choices must
    /// never be auto-picked, because the box normally answers them and a random
    /// pick would silently corrupt the player's choice.</summary>
    private static Type s_assembleUiType;
    private static bool s_assembleUiTypeResolved;
    private static bool s_assembleShapeLogged;

    /// <summary>The native Assemble ("拼装") selector is driven by AssembleUIManager.
    /// When that UI object is alive the open choice is an assemble choice, which is
    /// far more reliable than matching card text.</summary>
    private static bool IsAssembleUiShowing()
    {
        try
        {
            if (!s_assembleUiTypeResolved)
            {
                s_assembleUiTypeResolved = true;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        s_assembleUiType = assembly.GetTypes().FirstOrDefault(t => t.Name == "AssembleUIManager");
                    }
                    catch { }
                    if (s_assembleUiType != null) break;
                }
            }
            if (s_assembleUiType == null) return false;
            var found = UnityEngine.Object.FindObjectOfType(s_assembleUiType) as UnityEngine.Object;
            if (found == null) return false;
            // The manager object outlives a real assemble choice, so its mere
            // existence is not a signal.  Only the visible confirm button proves
            // the assemble selector is on screen right now.
            var buttonField = s_assembleUiType.GetField("m_confirmAssembleChoiceButton",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (buttonField == null) return false;
            var button = buttonField.GetValue(found);
            if (button == null) return false;
            var gameObjectProperty = button.GetType().GetProperty("gameObject",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var buttonObject = gameObjectProperty?.GetValue(button, null) as UnityEngine.GameObject;
            return buttonObject != null && buttonObject.activeInHierarchy;
        }
        catch { return false; }
    }

    /// <summary>One-time structural dump of the assemble surfaces so a real assemble
    /// choice can be classified without guessing again.</summary>
    private static void LogAssembleShapeOnce(GameState state, Network.EntityChoices choice)
    {
        if (s_assembleShapeLogged) return;
        s_assembleShapeLogged = true;
        try
        {
            var parts = new List<string> { "assemble shape: choiceType=" + choice.ChoiceType + ";source=" + choice.Source +
                ";uiType=" + (s_assembleUiType?.FullName ?? "null") + ";uiShowing=" + IsAssembleUiShowing() };
            if (choice.Entities != null)
            {
                foreach (var entityId in choice.Entities.Take(4))
                {
                    try
                    {
                        var card = state?.GetEntity(entityId)?.GetCard();
                        string text = null;
                        if (card != null)
                        {
                            var read = card.GetType().GetMethod("GetCardTextInHand",
                                           BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                       ?? card.GetType().GetMethod("GetCardText",
                                           BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (read != null && read.GetParameters().Length == 0) text = read.Invoke(card, null) as string;
                        }
                        parts.Add("e" + entityId + "=" + (string.IsNullOrEmpty(text) ? "?" :
                            text.Replace("\n", " ").Substring(0, Math.Min(24, text.Length))));
                    }
                    catch { }
                }
            }
            var uiType = s_assembleUiType;
            if (uiType != null)
            {
                parts.Add("uiType=" + uiType.FullName);
                foreach (var m in uiType.GetMembers(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        if (m.MemberType == System.Reflection.MemberTypes.Property || m.MemberType == System.Reflection.MemberTypes.Field)
                            parts.Add("  ui." + m.Name);
                    }
                    catch { }
                }
            }
            var mgr = ChoiceCardMgr.Get();
            if (mgr != null)
            {
                foreach (var field in mgr.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        var name = field.Name;
                        var typeName = field.FieldType.Name;
                        if (name.IndexOf("assemble", StringComparison.OrdinalIgnoreCase) < 0 &&
                            typeName.IndexOf("Assemble", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("ChoiceData", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var value = field.GetValue(mgr);
                        parts.Add("  mgr." + name + " : " + typeName + " = " + (value == null ? "null" : value.GetType().Name));
                    }
                    catch { }
                }
            }
            ArenaBoxBotPlugin.LogSource?.LogWarning(string.Join(" | ", parts));
        }
        catch { }
    }
    private static bool IsAssembleChoice(GameState state, Network.EntityChoices choice)
    {
        var result = false;
        try
        {
            LogAssembleShapeOnce(state, choice);
            if (IsAssembleUiShowing()) return true;
            var source = state?.GetEntity(choice.Source);
            var card = source?.GetCard();
            foreach (var target in new object[] { card, source })
            {
                if (target == null) continue;
                var method = target.GetType().GetMethod("GetCardTextInHand",
                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             ?? target.GetType().GetMethod("GetCardText",
                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null || method.GetParameters().Length != 0) continue;
                if (method.Invoke(target, null) is not string text || string.IsNullOrEmpty(text)) continue;
                if (text.IndexOf("拼装", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("Assemble", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = true;
                    break;
                }
            }
        }
        catch { }
        if (s_assembleChecked.Add(choice.ID))
        {
            try
            {
                ArenaBoxBotPlugin.LogSource?.LogWarning("choice assemble-check: id=" + choice.ID +
                    ";type=" + choice.ChoiceType + ";source=" + choice.Source + ";assemble=" + result);
            }
            catch { }
        }
        return result;
    }
    private static readonly HashSet<int> s_submittedFallbackChoices = new();


    /// <summary>Assemble ("拼装")/other GENERAL choices can arrive without a box
    /// recommendation, in which case HsMod never submits and the server closes
    /// the choice on timeout. Native submission is two steps: publish the chosen
    /// entity on the current choice, then call the parameterless SendChoices().</summary>
    private static bool SubmitFallbackGeneralChoice(GameState state, Network.EntityChoices choice)
    {
        try
        {
            // User decision: when the box AI publishes no recommendation (typical
            // for assemble/拼装), pick random candidates instead of stalling until
            // the server closes the choice on timeout.  The choice's own minimum is
            // honoured, so a multi-pick choice is not answered with a single pick.
            var count = Math.Max(1, Math.Min(choice.CountMin, choice.Entities.Count));
            var pool = new List<int>(choice.Entities);
            var chosen = new List<int>(count);
            for (var i = 0; i < count && pool.Count > 0; i++)
            {
                var index = UnityEngine.Random.Range(0, pool.Count);
                chosen.Add(pool[index]);
                pool.RemoveAt(index);
            if (HasPendingChosenEntities(state)) return false;
            }
            if (!TryPublishChosenEntities(state, choice, chosen)) return false;
            var send = typeof(GameState).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "SendChoices" && m.GetParameters().Length == 0);
            if (send == null)
            {
                Warn("choice fallback submit unavailable: parameterless SendChoices missing; choiceId=" + choice.ID);
                return false;
            }
            send.Invoke(state, null);
            ArenaBoxBotPlugin.LogSource?.LogWarning("choice fallback submitted: id=" + choice.ID + ";type=" + choice.ChoiceType +
                ";picked=" + string.Join("、", chosen) + ";count=" + chosen.Count +
                ";entities=" + choice.Entities.Count + ";mode=random");
            return true;
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("choice fallback submit failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>Publishes the chosen entity list on the live choice object.  The
    /// native shape is not a single setter: the game keeps a chosen-entity
    /// container (m_chosenEntities) that can be a list, a map keyed by player or
    /// choice id, or a setter taking an array, so all known shapes are tried.</summary>
    /// <summary>Second half of a native choice submission: after publishing the
    /// selected entities the client sends them with the parameterless SendChoices.</summary>
    private static MethodInfo s_getChosenEntities;

    /// <summary>True when someone (HsMod or the client) already filled the
    /// choice's chosen-entity container, i.e. a submission is already in flight.
    /// Submitting again would race a correct pick with a random one.</summary>
    private static bool HasPendingChosenEntities(GameState state)
    {
        try
        {
            if (state == null) return false;
            s_getChosenEntities ??= typeof(GameState).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "GetChosenEntities" && m.GetParameters().Length == 0);
            if (s_getChosenEntities == null) return false;
            var list = s_getChosenEntities.Invoke(state, null);
            if (list == null) return false;
            var countProperty = list.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            if (countProperty == null) return false;
            return Convert.ToInt32(countProperty.GetValue(list, null)) > 0;
        }
        catch { return false; }
    }
    private static bool TrySendChoices(GameState state)
    {
        try
        {
            var send = typeof(GameState).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "SendChoices" && m.GetParameters().Length == 0);
            if (send == null) return false;
            send.Invoke(state, null);
            return true;
        }
        catch { return false; }
    }
    private static bool TryPublishChosenEntities(GameState state, Network.EntityChoices choice, List<int> chosen)
    {
        // Mirror exactly what the client does when the player clicks the cards:
        // add each chosen entity, then send.  The runtime dump proved the native
        // surface is GameState.AddChosenEntity(Entity) + SendChoices().
        var addChosenMethod = typeof(GameState).GetMethod("AddChosenEntity",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var getChosenMethod = typeof(GameState).GetMethod("GetChosenEntities",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (addChosenMethod != null && getChosenMethod != null)
        {
            try
            {
                if (getChosenMethod.Invoke(state, null) is System.Collections.Generic.IList<int> current) current.Clear();
                foreach (var entityId in chosen)
                {
                    var entity = state.GetEntity(entityId);
                    if (entity == null) return false;
                    addChosenMethod.Invoke(state, new object[] { entity });
                }
                return true;
            }
            catch { }
        }
        var nativeChosenField = typeof(GameState).GetField("m_chosenEntities",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (nativeChosenField != null)
        {
            try
            {
                if (nativeChosenField.GetValue(state) is System.Collections.Generic.IList<int> nativeList &&
                    !nativeList.IsReadOnly)
                {
                    nativeList.Clear();
                    foreach (var entity in chosen) nativeList.Add(entity);
                    return true;
                }
                nativeChosenField.SetValue(state, new List<int>(chosen));
                return true;
            }
            catch { }
        }
        var choiceType = choice.GetType();
        foreach (var method in choiceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(m => m.Name == "SetChosenEntities"))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1) continue;
            var parameterType = parameters[0].ParameterType;
            try
            {
                if (parameterType.IsAssignableFrom(chosen.GetType())) { method.Invoke(choice, new object[] { chosen }); return true; }
                if (parameterType.IsAssignableFrom(typeof(int[]))) { method.Invoke(choice, new object[] { chosen.ToArray() }); return true; }
            }
            catch { }
            break;
        }
        foreach (var field in choiceType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(f => f.Name.IndexOf("chosen", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            object container;
            try { container = field.GetValue(choice); } catch { continue; }
            if (container == null) continue;
            var containerType = container.GetType();
            try
            {
                if (container is System.Collections.Generic.IList<int> list)
                {
                    list.Clear();
                    foreach (var entity in chosen) list.Add(entity);
                    return true;
                }
                var addTwo = containerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 2);
                if (addTwo != null) { addTwo.Invoke(container, new object[] { choice.ID, chosen }); return true; }
                var indexer = containerType.GetProperty("Item");
                if (indexer != null) { indexer.SetValue(container, chosen, new object[] { choice.ID }); return true; }
            }
            catch { }
        }
        // One unthrottled dump: Warn() is rate limited, so the real shape of a
        // failed native choice would otherwise never reach the log.
        if (s_choiceMemberTypesLogged.Add("shape2"))
        {
            var api = new List<string> { "choice api v2" };
            DumpChoiceApi(typeof(GameState), "GameState", api);
            DumpChoiceApi(typeof(ChoiceCardMgr), "ChoiceCardMgr", api);
            DumpChoiceApi(typeof(Network), "Network", api);
            ArenaBoxBotPlugin.LogSource?.LogWarning(string.Join(" | ", api));
        }

        if (s_choiceMemberTypesLogged.Add("shape:" + choiceType.FullName))
        {
            var lines = new List<string> { "choice shape: " + choiceType.FullName };
            foreach (var f2 in choiceType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                lines.Add("  field " + f2.Name + " : " + f2.FieldType.FullName);
            foreach (var m2 in choiceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(m => m.Name.IndexOf("Chosen", StringComparison.OrdinalIgnoreCase) >= 0))
                lines.Add("  method " + m2.Name + "(" + string.Join(", ", m2.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            ArenaBoxBotPlugin.LogSource?.LogWarning(string.Join(" | ", lines));
        }
        ArenaBoxBotPlugin.LogSource?.LogWarning("choice fallback skipped: no usable chosen-entity container on " + choiceType.Name + "; choiceId=" + choice.ID);
        return false;
    }
    private static bool s_loggedChoiceSubmitApi;

    /// <summary>Proves the fallback submit target exists and logs its real signature
    /// once, so a signature mismatch is found before an Assemble choice needs it.</summary>
    private static void LogChoiceSubmitApiOnce()
    {
        if (s_loggedChoiceSubmitApi) return;
        // The first sample can run before BepInEx attaches the plugin log
        // source; retry instead of losing the probe output forever.
        if (ArenaBoxBotPlugin.LogSource == null) return;
        s_loggedChoiceSubmitApi = true;
        try
        {
            var methods = typeof(GameState).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "SendChoices").ToArray();
            if (methods.Length == 0)
            {
                Warn("choice submit api: SendChoices not found on GameState");
                return;
            }
            foreach (var m in methods)
                Warn("choice submit api: SendChoices(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            foreach (var fld in typeof(GameState).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(x => x.Name.IndexOf("chosen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     x.Name.IndexOf("choice", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Warn("choice api field: GameState." + fld.Name + " : " + fld.FieldType.FullName);
                foreach (var fm in fld.FieldType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             .Where(x => x.Name is "Add" or "set_Item" or "Clear" or "Remove"))
                    Warn("choice api field method: " + fld.FieldType.Name + "." + fm.Name + "(" + string.Join(", ", fm.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            }
            foreach (var m in typeof(GameState).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(x => x.Name.IndexOf("Choice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     x.Name.IndexOf("Chosen", StringComparison.OrdinalIgnoreCase) >= 0)
                         .Where(x => x.GetParameters().Length <= 3))
                Warn("choice api method: GameState." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            foreach (var m in typeof(ChoiceCardMgr).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .Where(x => x.Name.IndexOf("Choose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     x.Name.IndexOf("Choice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     x.Name.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0)
                         .Where(x => x.GetParameters().Length <= 3))
                Warn("choice api method: ChoiceCardMgr." + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
        }
        catch (Exception ex)
        {
            Warn("choice submit api probe failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
    private static readonly HashSet<string> s_choiceMemberTypesLogged = new();

    /// <summary>Dumps the real choice-submission surface once, because the native
    /// DTO carries no chosen-entity container and the submit call must be found.</summary>
    private static void DumpChoiceApi(Type type, string label, List<string> api)
    {
        if (type == null) return;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            try
            {
                if (field.Name.IndexOf("cho", StringComparison.OrdinalIgnoreCase) < 0 &&
                    field.Name.IndexOf("send", StringComparison.OrdinalIgnoreCase) < 0) continue;
                api.Add(label + " field " + field.Name + " : " + field.FieldType.Name);
            }
            catch { }
        }
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            try
            {
                if (method.Name.IndexOf("Choice", StringComparison.OrdinalIgnoreCase) < 0 &&
                    method.Name.IndexOf("Chosen", StringComparison.OrdinalIgnoreCase) < 0) continue;
                api.Add(label + " method " + method.Name + "(" +
                        string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            }
            catch { }
        }
    }
    private static void LogChoiceTypeMembersOnce(Type choiceType)
    {
        if (!s_choiceMemberTypesLogged.Add(choiceType.FullName ?? choiceType.Name)) return;
        foreach (var fld in choiceType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            try { Warn("choice type field: " + choiceType.Name + "." + fld.Name + " : " + fld.FieldType.Name); }
            catch { }
        }
        foreach (var m in choiceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(x => x.Name.IndexOf("Chosen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 x.Name.IndexOf("Choice", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            try
            {
                Warn("choice type method: " + choiceType.Name + "." + m.Name + "(" +
                     string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            }
            catch { }
        }
        foreach (var m in typeof(ChoiceCardMgr).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(x => x.Name.IndexOf("Choose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 x.Name.IndexOf("Choice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 x.Name.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            try
            {
                Warn("choice card mgr method: ChoiceCardMgr." + m.Name + "(" +
                     string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");
            }
            catch { }
        }
    }
    private static void Warn(string message)
    {
        if (Time.unscaledTime < s_nextWarning) return;
        s_nextWarning = Time.unscaledTime + 10f;
        ArenaBoxBotPlugin.LogSource?.LogWarning(message);
    }
}
