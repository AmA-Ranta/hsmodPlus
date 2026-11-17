using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ArenaController;

public enum ConfigurationApplyResult { Applied, ExistingVerified, AppliedAfterBoxRestart }

/// <summary>Only a real Windows sharing/lock violation can justify a Box retry.</summary>
public static class ConfigurationApplyGuard
{
    public static bool IsSharingViolation(Exception error)
        => error is IOException && (error.HResult == unchecked((int)0x80070020) || error.HResult == unchecked((int)0x80070021));

    public static ConfigurationApplyResult Apply(Action apply, Func<bool> existingConfigurationMatches,
        Func<bool> boxRestartIsSafe, Action stopBox, Action<IOException> beforeBoxRestart)
    {
        try { apply(); return ConfigurationApplyResult.Applied; }
        catch (Exception error) when (error is UnauthorizedAccessException || IsSharingViolation(error))
        {
            if (ReadbackMatches(existingConfigurationMatches)) return ConfigurationApplyResult.ExistingVerified;
            if (error is UnauthorizedAccessException)
                throw new InvalidOperationException("配置文件访问被拒绝，未重启盒子。" + error.Message, error);
            if (!boxRestartIsSafe())
                throw new InvalidOperationException("配置文件存在共享锁，但当前对局或登录状态未确认，未重启盒子。请在安全页面重试。", error);

            beforeBoxRestart((IOException)error);
            stopBox();
            try { apply(); return ConfigurationApplyResult.AppliedAfterBoxRestart; }
            catch (Exception retry) when (retry is UnauthorizedAccessException || IsSharingViolation(retry))
            {
                if (ReadbackMatches(existingConfigurationMatches)) return ConfigurationApplyResult.ExistingVerified;
                var reason = retry is UnauthorizedAccessException ? "配置文件访问被拒绝" : "配置文件仍有共享锁";
                throw new InvalidOperationException(reason + "，重试未完成。" + retry.Message, retry);
            }
        }
    }

    private static bool ReadbackMatches(Func<bool> readback)
    {
        try { return readback(); }
        catch { return false; } // A failed read cannot authorize the fallback.
    }

    public static bool IsFreshSessionTime(DateTime nowUtc, DateTime gameStartedUtc,
        DateTime observedAtUtc, DateTime? nativeUpdatedAtUtc)
    {
        if (!nativeUpdatedAtUtc.HasValue) return false;
        var native = nativeUpdatedAtUtc.Value;
        return gameStartedUtc <= nowUtc && observedAtUtc >= gameStartedUtc && native >= gameStartedUtc &&
               observedAtUtc <= nowUtc && native <= nowUtc &&
               nowUtc - observedAtUtc <= TimeSpan.FromSeconds(30) &&
               nowUtc - native <= TimeSpan.FromSeconds(30);
    }

    public static bool MayRestartBox(bool processEnumerationSucceeded, bool gameRunning,
        bool currentLoggedInSession, bool nativeScriptDisabled, bool gameplayEvidence, bool knownSafeState)
        => processEnumerationSucceeded && (!gameRunning ||
           (currentLoggedInSession && nativeScriptDisabled && !gameplayEvidence && knownSafeState));

    public static bool IsGameplayActivity(string? activity)
    {
        var value = activity?.Trim() ?? "";
        if (value.Contains("回合", StringComparison.OrdinalIgnoreCase) || value.Contains("留牌", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("匹配", StringComparison.OrdinalIgnoreCase) || value.Contains("结算", StringComparison.OrdinalIgnoreCase)) return true;
        if (value == "竞技场套牌就绪，等待对局") return false;
        return ConstructedStopBoundary.IsGameplayActivity(value);
    }

    public static bool TryClassifyNativeBoundary(string json, DateTime nowUtc, DateTime gameStartedUtc,
        out bool gameplay, out bool safe)
    {
        gameplay = false; safe = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name).Any(group => group.Count() != 1)) return false;
            if (!root.TryGetProperty("updatedAtUnixMs", out var stamp) || !stamp.TryGetInt64(out var unix) || unix <= 0 ||
                !root.TryGetProperty("inGameplay", out var inGame) || inGame.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("runtimeEnabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("arenaSelected", out var selected) || selected.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("scene", out var sceneNode) || sceneNode.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("arenaState", out var stateNode) || stateNode.ValueKind != JsonValueKind.String) return false;
            var nativeUpdated = DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime;
            if (!IsFreshSessionTime(nowUtc, gameStartedUtc, nowUtc, nativeUpdated)) return false;
            var scene = sceneNode.GetString(); var state = stateNode.GetString();
            gameplay = inGame.GetBoolean() || string.Equals(scene, "GAMEPLAY", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "GAMEPLAY", StringComparison.OrdinalIgnoreCase);
            var knownScene = scene is "DRAFT" or "HUB" or "DISABLED" or "STOPPED";
            safe = knownScene && !enabled.GetBoolean() &&
                ArenaStopBoundary.IsKnownSafe(true, selected.GetBoolean(), false, gameplay, scene, state);
            return true;
        }
        catch { return false; }
    }

    public static bool MatchesValue(string text, string[] sections, string[] keys, string expected)
    {
        var inSection = false;
        var matches = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                if (!trimmed.EndsWith("]", StringComparison.Ordinal)) return false;
                inSection = sections.Contains(trimmed[1..^1].Trim(), StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal)) continue;
            var separator = trimmed.IndexOf('=');
            if (separator < 0 || !keys.Contains(trimmed[..separator].Trim(), StringComparer.OrdinalIgnoreCase)) continue;
            matches++;
            var actual = trimmed[(separator + 1)..].Trim();
            // HsMod 1004 writes several enum values in English even when the
            // UI is Chinese. Treat the known recommendation-source alias as
            // the same persisted setting so a locked, already-correct config
            // can be reused without restarting the Box or rewriting HsMod.cfg.
            var equivalent = string.Equals(expected, "盒子文件", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(actual, "BoxFile", StringComparison.OrdinalIgnoreCase);
            if (!equivalent && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return matches > 0; // Conflicting duplicate keys/sections never certify the requested value.
    }

    public static bool MatchesConfiguration(string hsmod, string arena, string mode, string arenaType,
        int runGames, int totalGames, bool ticketStop, bool goldStop, string deckName)
    {
        bool Hs(string section, string key, string expected) => MatchesValue(hsmod, new[] { section }, new[] { key }, expected);
        bool Arena(string key, string expected) => MatchesValue(arena, new[] { "竞技场脚本" }, new[] { key }, expected);
        if (!MatchesValue(hsmod, new[] { "Global", "全局" }, new[] { "HsMod Language", "HsMod语言" }, "zhCN") ||
            !Hs("HsMod", "HsMod.Init.Language", "zhCN") || !Hs("自动脚本", "推荐来源", "盒子文件")) return false;
        var isArena = string.Equals(mode, "竞技场", StringComparison.Ordinal);
        if (!Hs("自动脚本", "脚本模式", HsModModeMapping.ToNative(isArena ? "标准" : mode))) return false;
        if (isArena)
            return Arena("脚本模式", "竞技场") && Arena("竞技场模式", arenaType) && Arena("本轮最多对局数", runGames.ToString()) &&
                   Arena("门票不足自动停止", ticketStop ? "true" : "false") && Arena("金币不足自动停止", goldStop ? "true" : "false");
        if (mode != "酒馆战棋" && !string.IsNullOrWhiteSpace(deckName) &&
            (!Hs("自动脚本", "卡组名称", deckName.Trim()) || !Hs("自动脚本", "卡组ID", "0"))) return false;
        return Hs("自动脚本", "场数限制", totalGames.ToString()) && Hs("自动脚本", "开启或关闭脚本", "F12") && Arena("脚本模式", mode);
    }

    public static bool WatchdogLeaseMatches(string json, bool enabled)
        => TryReadWatchdogLease(json, out var restart, out var active) && restart == enabled && active == enabled;

    public static bool WatchdogLeaseHasRequiredFlags(string json)
        => TryReadWatchdogLease(json, out _, out _);

    public static void WriteWatchdogLease(string json, bool enabled, Action<string> write, Func<string> readback)
    {
        write(SetWatchdogLeaseValue(json, enabled));
        if (!WatchdogLeaseMatches(readback(), enabled))
            throw new InvalidDataException("看门狗控制状态写入后校验失败。");
    }

    public static string SetWatchdogLeaseValue(string json, bool enabled)
    {
        if (!WatchdogLeaseHasRequiredFlags(json)) throw new InvalidDataException("看门狗控制状态不是明确的根级布尔值。");
        using var document = JsonDocument.Parse(json);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("restartEnabled") || property.NameEquals("scriptActive")) writer.WriteBoolean(property.Name, enabled);
                else property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static bool TryReadWatchdogLease(string json, out bool restartEnabled, out bool scriptActive)
    {
        restartEnabled = scriptActive = false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in new[] { "restartEnabled", "scriptActive" })
            {
                var properties = root.EnumerateObject().Where(p => p.NameEquals(name)).ToArray();
                if (properties.Length != 1 ||
                    properties[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                if (name == "restartEnabled") restartEnabled = properties[0].Value.GetBoolean();
                else scriptActive = properties[0].Value.GetBoolean();
            }
            return true;
        }
        catch (JsonException) { return false; }
    }
}
