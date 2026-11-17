using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ArenaBoxBot;

/// <summary>
/// Integrates Arena settings into HsMod's own settings screen. The old build
/// painted a second IMGUI window and captured F8; this bridge only uses
/// HsMod's ModSettingsUI Entry objects, so it inherits the same cards, search,
/// reset buttons, section navigation and styling.
/// </summary>
internal sealed class ArenaSettingsUi : MonoBehaviour
{
    private static Harmony s_harmony;
    private static float s_nextAttempt;
    private static Type s_uiType;
    private static Type s_entryType;
    private static FieldInfo s_entriesField;
    private static FieldInfo s_entriesVersionField;
    private static FieldInfo s_sectionsField;
    private static FieldInfo s_labelsField;
    private static MethodInfo s_reloadEntries;
    private static readonly ArenaStatusProjection s_statusProjection = new ArenaStatusProjection();
    private string _lastNativeScriptMode;
    private float _nextModeSync;

    private static readonly string[] ArenaKeys =
    {
        "arenaScriptMode", "arenaMode", "arenaAutoDraft", "arenaAutoRedraft", "arenaAutoMatch",
        "arenaAutoPlay", "arenaUseBoxScores", "arenaMaxGamesPerRun",
        "arenaStopOnTicketShortage", "arenaStopOnGoldShortage", "arenaTicketGoldCost"
    };

    internal static void Attach()
    {
        if (FindObjectOfType<ArenaSettingsUi>() != null) return;
        var go = new GameObject("ArenaBoxBotNativeSettings");
        DontDestroyOnLoad(go);
        go.AddComponent<ArenaSettingsUi>();
    }

    private void Update()
    {
        EnsureInstalled();
        SyncNativeScriptMode();
        ArenaActivityRuntime.SampleMainThread();
    }

    private void SyncNativeScriptMode()
    {
        if (Time.unscaledTime < _nextModeSync) return;
        _nextModeSync = Time.unscaledTime + 0.25f;
        var entry = ArenaSettings.ScriptMode;
        if (entry == null) return;
        try
        {
            var selected = HsModNativeArena.IsNativeArenaModeSelected()
                ? ArenaSettings.NativeScriptMode.竞技场.ToString()
                : HsModNativeArena.ReadHsModModeText();
            if (_lastNativeScriptMode == null)
            {
                if (Enum.TryParse(selected, out ArenaSettings.NativeScriptMode initial)) entry.Value = initial;
                _lastNativeScriptMode = entry.Value.ToString();
                return;
            }
            var configured = entry.Value.ToString();
            if (!string.Equals(configured, _lastNativeScriptMode, StringComparison.Ordinal))
            {
                HsModNativeArena.SetModeBridge(configured);
                _lastNativeScriptMode = configured;
                return;
            }
            if (!string.Equals(selected, _lastNativeScriptMode, StringComparison.Ordinal) &&
                Enum.TryParse(selected, out ArenaSettings.NativeScriptMode external))
            {
                entry.Value = external;
                _lastNativeScriptMode = selected;
            }
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogDebug("Native mode settings sync deferred: " + ex.Message);
        }
    }

    internal static void EnsureInstalled()
    {
        if (s_harmony != null || Time.unscaledTime < s_nextAttempt) return;
        s_nextAttempt = Time.unscaledTime + 3f;
        try
        {
            s_uiType = Type.GetType("HsMod.ModSettingsUI, HsMod");
            if (s_uiType == null) return;
            s_entryType = s_uiType.GetNestedType("Entry", BindingFlags.NonPublic);
            s_entriesField = s_uiType.GetField("allEntries", BindingFlags.Instance | BindingFlags.NonPublic);
            s_entriesVersionField = s_uiType.GetField("entriesVersion", BindingFlags.Instance | BindingFlags.NonPublic);
            s_sectionsField = s_uiType.GetField("sections", BindingFlags.Instance | BindingFlags.NonPublic);
            s_labelsField = s_uiType.GetField("sectionLabels", BindingFlags.Instance | BindingFlags.NonPublic);
            s_reloadEntries = s_uiType.GetMethod("ReloadEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var reloadPostfix = typeof(ArenaSettingsUi).GetMethod(nameof(ReloadEntriesPostfix), BindingFlags.Static | BindingFlags.NonPublic);
            var visible = Type.GetType("HsMod.Utilities.ConfigVisibility, HsMod")?.GetMethod("IsVisible", BindingFlags.Static | BindingFlags.Public);
            var visiblePostfix = typeof(ArenaSettingsUi).GetMethod(nameof(IsVisiblePostfix), BindingFlags.Static | BindingFlags.NonPublic);
            var visibilityState = Type.GetType("HsMod.Utilities.ConfigVisibility, HsMod")?.GetMethod("VisibilityState", BindingFlags.Static | BindingFlags.Public);
            var visibilityStatePostfix = typeof(ArenaSettingsUi).GetMethod(nameof(VisibilityStatePostfix), BindingFlags.Static | BindingFlags.NonPublic);
            var harmony = new Harmony("local.hsmod.arenaboxbot.native-settings");
            if (s_reloadEntries != null && reloadPostfix != null)
                harmony.Patch(s_reloadEntries, postfix: new HarmonyMethod(reloadPostfix));
            if (visible != null && visiblePostfix != null)
                harmony.Patch(visible, postfix: new HarmonyMethod(visiblePostfix));
            if (visibilityState != null && visibilityStatePostfix != null)
                harmony.Patch(visibilityState, postfix: new HarmonyMethod(visibilityStatePostfix));
            InstallWebApiPatch(harmony);
            s_harmony = harmony;
            ArenaBoxBotPlugin.LogSource?.LogInfo("Arena settings integrated into HsMod native settings");
            var instance = s_uiType.GetField("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            if (instance != null) s_reloadEntries?.Invoke(instance, null);
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Native Arena settings deferred: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void ReloadEntriesPostfix(object __instance)
    {
        try
        {
            if (__instance == null || s_entryType == null || s_entriesField == null) return;
            var entries = s_entriesField.GetValue(__instance) as IList;
            var sections = s_sectionsField?.GetValue(__instance) as IList;
            var labels = s_labelsField?.GetValue(__instance) as IDictionary;
            if (entries == null || sections == null || labels == null) return;
            var injected = false;
            foreach (var item in entries)
            {
                var fieldName = item?.GetType().GetField("FieldName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item) as string;
                if (fieldName == ArenaModeMetadata.ShortcutKey) Set(item, "Description", ArenaModeMetadata.ShortcutDescription);
                if (fieldName == "scriptPace") Set(item, "Description", ArenaDraftPacing.SettingsDescription);
            }
            foreach (var key in ArenaKeys)
            {
                var config = FindConfig(key);
                if (config == null) continue;
                var already = false;
                foreach (var item in entries)
                {
                    var name = item?.GetType().GetField("FieldName", BindingFlags.Instance | BindingFlags.Public)?.GetValue(item) as string;
                    if (name == key) { already = true; break; }
                }
                if (already) continue;
                var entry = Activator.CreateInstance(s_entryType, nonPublic: true);
                Set(entry, "FieldName", key);
                Set(entry, "Config", config);
                Set(entry, "Name", DisplayName(key, config));
                Set(entry, "Section", key == "arenaScriptMode" ? "自动脚本" : "竞技场脚本");
                Set(entry, "Description", Description(key, config));
                Set(entry, "IsAdvanced", false);
                entries.Add(entry);
                injected = true;
            }
            if (injected && !sections.Contains("竞技场脚本")) sections.Add("竞技场脚本");
            if (injected) labels["竞技场脚本"] = "竞技场脚本";
            if (injected && s_entriesVersionField != null)
            {
                // CurrentScan caches the filtered Entry list by the version
                // number. ReloadEntries has already completed when this
                // postfix runs, so invalidate that cache after appending the
                // Arena entries or the new section may stay invisible until a
                // later unrelated settings reload.
                var version = s_entriesVersionField.GetValue(__instance) is int current ? current : 0;
                s_entriesVersionField.SetValue(__instance, version + 1);
            }
        }
        catch (Exception ex)
        {
            ArenaBoxBotPlugin.LogSource?.LogWarning("Arena settings UI refresh failed: " + ex.Message);
        }
    }

    private static void Set(object target, string field, object value)
    {
        s_entryType.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(target, value);
    }

    private static ConfigEntryBase FindConfig(string key)
    {
        string fieldName;
        switch (key)
        {
            case "arenaScriptMode": fieldName = "ScriptMode"; break;
            case "arenaMode": fieldName = "Mode"; break;
            case "arenaAutoDraft": fieldName = "AutoDraft"; break;
            case "arenaAutoRedraft": fieldName = "AutoRedraft"; break;
            case "arenaAutoMatch": fieldName = "AutoMatch"; break;
            case "arenaAutoPlay": fieldName = "AutoPlay"; break;
            case "arenaUseBoxScores": fieldName = "UseBoxScores"; break;
            case "arenaMaxGamesPerRun": fieldName = "MaxGamesPerRun"; break;
            case "arenaStopOnTicketShortage": fieldName = "StopOnTicketShortage"; break;
            case "arenaStopOnGoldShortage": fieldName = "StopOnGoldShortage"; break;
            case "arenaTicketGoldCost": fieldName = "TicketGoldCost"; break;
            default: return null;
        }
        return typeof(ArenaSettings).GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as ConfigEntryBase;
    }

    private static string DisplayName(string key, ConfigEntryBase config)
    {
        switch (key)
        {
            case "arenaScriptMode": return "脚本模式";
            case "arenaMode": return "竞技场类型";
            case "arenaAutoDraft": return "自动选牌";
            case "arenaAutoRedraft": return "输局后自动重建套牌";
            case "arenaAutoMatch": return "自动匹配下一局";
            case "arenaAutoPlay": return "自动对战";
            case "arenaUseBoxScores": return "使用盒子推荐打法";
            case "arenaMaxGamesPerRun": return "本轮最多对局数";
            case "arenaStopOnTicketShortage": return "门票不足时停止";
            case "arenaStopOnGoldShortage": return "金币不足时停止";
            case "arenaTicketGoldCost": return "门票金币价格";
            default: return config.Definition.Key;
        }
    }

    private static string Description(string key, ConfigEntryBase config)
    {
        switch (key)
        {
            case "arenaScriptMode": return "标准、狂野、休闲、酒馆战棋和竞技场。竞技场由独立流程接管选牌、对战、奖励和下一轮。";
            case "arenaMode": return "普通竞技场或地下竞技场。竞技场流程独立于标准、狂野和酒馆战棋。";
            case "arenaAutoDraft": return "按盒子推荐完成选英雄和选牌；关闭后停在选牌页面。";
            case "arenaAutoRedraft": return "竞技场输局后，按盒子推荐完成重建套牌；关闭后停在重建页面。";
            case "arenaAutoMatch": return "套牌完成并领奖后自动进入下一局；关闭后等待手动操作。";
            case "arenaAutoPlay": return "对局中执行盒子推荐的出牌、攻击和结束回合。";
            case "arenaUseBoxScores": return "选英雄、选卡与重选遵循盒子推荐打法；没有匹配当前选项的新建议时等待，不按最高评分选卡。";
            case "arenaMaxGamesPerRun": return "同一轮累计的自动对局上限，0 表示不限制；启停、重连保留计数。到限后完成本局及已有奖励并停止。调高上限或设为 0 可继续本轮。";
            case "arenaStopOnTicketShortage": return "门票不足时立即停止，不花金币购买。关闭此项后才允许自动用金币补票。";
            case "arenaStopOnGoldShortage": return "金币低于门票价格时停止竞技场流程。";
            case "arenaTicketGoldCost": return "仅用于判断金币是否足够，默认 150，不修改游戏商品价格。";
            default: return config.Description?.Description ?? "";
        }
    }

    private static void IsVisiblePostfix(string fieldName, ref bool __result)
    {
        // The injected card is the single mode selector. Keep HsMod's old
        // four-value card out of the screen in every mode so users never see
        // two competing selectors.
        if (fieldName == "wildLadderPlayMode") { __result = false; return; }
        if (!HsModNativeArena.IsNativeArenaModeSelected()) return;
        if (fieldName == "wildLadderDeckName" || fieldName == "ladderRankControl" ||
            fieldName == "ladderRankControlRank" || fieldName == "ladderRankControlStars" ||
            fieldName == "ladderRankControlLegendRank") __result = false;
    }

    private static void VisibilityStatePostfix(ref int __result)
    {
        // CurrentScan caches its filtered Entry list by this integer. Include
        // the virtual mode so switching the native card immediately removes
        // constructed deck/rank rows when Arena is selected.
        __result = (__result * 31) + (HsModNativeArena.IsNativeArenaModeSelected() ? 1 : 2);
    }

    private static void InstallWebApiPatch(Harmony harmony)
    {
        var api = Type.GetType("HsMod.WebApi, HsMod");
        if (api == null) return;
        var metadata = api.GetMethod("GetAllConfigMetadata", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var write = api.GetMethod("RunPluginConfigAsync", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var metadataPostfix = typeof(ArenaSettingsUi).GetMethod(nameof(MetadataPostfix), BindingFlags.Static | BindingFlags.NonPublic);
        var writePrefix = typeof(ArenaSettingsUi).GetMethod(nameof(WritePrefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (metadata != null) harmony.Patch(metadata, postfix: new HarmonyMethod(metadataPostfix));
        if (write != null) harmony.Patch(write, prefix: new HarmonyMethod(writePrefix));
        // /api/status and /api/script responses both read this getter. Keep
        // HsMod's own volatile snapshot untouched; project only the returned
        // view so leaving Arena immediately restores its original base mode.
        var runtime = Type.GetType("HsMod.Web.WebRuntime, HsMod");
        var snapshot = runtime?.GetProperty("Snapshot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
        if (snapshot != null)
            harmony.Patch(snapshot, postfix: new HarmonyMethod(typeof(ArenaSettingsUi).GetMethod(nameof(StatusSnapshotPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        else ArenaBoxBotPlugin.LogSource?.LogWarning("Arena status bridge: HsMod WebRuntime.Snapshot was not found");
    }

    private static void StatusSnapshotPostfix(ref string __result)
    {
        __result = s_statusProjection.Project(__result, HsModNativeArena.IsNativeArenaModeSelected(), ArenaActivityRuntime.Current);
    }

    private static void MetadataPostfix(ref string __result)
    {
        try
        {
            if (string.IsNullOrEmpty(__result)) return;
            var root = JObject.Parse(__result);
            var groups = root["groups"] as JObject;
            if (groups == null) return;
            ArenaModeMetadata.UpdateShortcutDescription(groups);
            var paceItem = FindMetadataItem(groups, "scriptPace");
            if (paceItem != null) paceItem["description"] = ArenaDraftPacing.SettingsDescription;
            // The installed HsMod binary still serializes a four-value enum.
            // Extend the existing 自动脚本 mode item in the web settings
            // response so Arena is a first-class option without rewriting the
            // HsMod assembly.
            var scriptItem = FindMetadataItem(groups, "wildLadderPlayMode");
            if (scriptItem != null)
            {
                var values = scriptItem["enumValues"] as JArray ?? new JArray();
                // HsMod serializes its enum values as English identifiers
                // (Standard/Wild/Casual/Battlegrounds). Keep the same wire
                // format for the virtual option and expose Chinese only as
                // its display label.
                if (!values.Any(v => string.Equals((string)v, "Arena", StringComparison.Ordinal))) values.Add("Arena");
                var labels = scriptItem["enumLabels"] as JObject ?? new JObject();
                labels["Arena"] = "竞技场";
                scriptItem["enumValues"] = values;
                scriptItem["enumLabels"] = labels;
                scriptItem["description"] = "脚本模式：标准、狂野、休闲、酒馆战棋、竞技场";
                if (HsModNativeArena.IsNativeArenaModeSelected()) scriptItem["value"] = "Arena";
            }
            var list = groups["竞技场脚本"] as JArray ?? new JArray();
            foreach (var key in ArenaKeys)
            {
                // WebApi already exposes HsMod's original mode card (extended
                // above with Arena). Do not publish the UI-only mirror as a
                // second web card.
                if (key == "arenaScriptMode") continue;
                if (ContainsKey(list, key)) continue;
                var config = FindConfig(key);
                if (config == null) continue;
                var item = new JObject
                {
                    ["key"] = key, ["name"] = DisplayName(key, config), ["label"] = "竞技场脚本",
                    ["description"] = Description(key, config), ["type"] = ApiType(config.SettingType),
                    ["value"] = config.GetSerializedValue(), ["defaultValue"] = config.GetSerializedValue(),
                    ["canWrite"] = true, ["readOnlyReason"] = "", ["isAdvanced"] = false, ["reloadOnSave"] = false
                };
                if (config.SettingType.IsEnum)
                {
                    var values = new JArray();
                    var labels = new JObject();
                    foreach (var value in Enum.GetNames(config.SettingType)) { values.Add(value); labels[value] = value; }
                    item["enumValues"] = values; item["enumLabels"] = labels; item["isFlags"] = false;
                }
                list.Add(item);
            }
            groups["竞技场脚本"] = list;
            __result = root.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception ex) { ArenaBoxBotPlugin.LogSource?.LogWarning("Arena metadata extension failed: " + ex.Message); }
    }

    private static bool ContainsKey(JArray list, string key)
    {
        foreach (var item in list) if (item is JObject obj && string.Equals((string)obj["key"], key, StringComparison.Ordinal)) return true;
        return false;
    }

    private static JObject FindMetadataItem(JObject groups, string key)
    {
        foreach (var group in groups.Properties())
        {
            if (!(group.Value is JArray items)) continue;
            foreach (var item in items)
                if (item is JObject obj && string.Equals((string)obj["key"], key, StringComparison.Ordinal)) return obj;
        }
        return null;
    }

    private static string ApiType(Type type)
    {
        if (type == typeof(bool)) return "bool";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(float)) return "float";
        if (type.IsEnum) return "enum";
        return "string";
    }

    private static bool WritePrefix(string key, string value, ref string res, ref int __result)
    {
        if (key != null && key.EndsWith(".name", StringComparison.Ordinal)) key = key.Substring(0, key.Length - 5);
        if (string.Equals(key, "wildLadderPlayMode", StringComparison.Ordinal))
        {
            if (value == "Arena" || value == "竞技场" || value == "Standard" || value == "Wild" ||
                value == "Casual" || value == "Battlegrounds" || value == "标准" || value == "狂野" ||
                value == "休闲" || value == "酒馆战棋")
            {
                var display = HsModNativeArena.DisplayMode(value);
                HsModNativeArena.SetModeBridge(display);
                res = display == "竞技场" ? "Arena" : HsModNativeArena.HsModModeValue(display);
                __result = 200;
                return false;
            }
        }
        var config = key == null ? null : FindConfig(key);
        if (config == null) return true;
        try { config.SetSerializedValue(value ?? ""); res = config.GetSerializedValue(); __result = 200; }
        catch (Exception ex) { res = "竞技场配置保存失败：" + ex.Message; __result = 400; }
        return false;
    }
}


