using ArenaController;
using System.Text.Json;

internal static class Program
{
    private static int _checks;
    private static readonly DateTime Now = new(2026, 10, 8, 11, 0, 0, DateTimeKind.Utc);

    private static void Main()
    {
        Classification();
        ActualApplySequence();
        NativeBoundary();
        CompleteReadback();
        LeaseReadback();
        Console.WriteLine("PASS: production configuration apply/retry, session boundary and exact readback (" + _checks + " assertions)");
    }

    private static IOException Io(uint hresult) => new("fixture config failure", unchecked((int)hresult));
    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(name);
    }

    private static void Classification()
    {
        foreach (var code in new uint[] { 0x80070020, 0x80070021 })
            Check(ConfigurationApplyGuard.IsSharingViolation(Io(code)), "Real Win32 sharing and lock HRESULT");
        foreach (var error in new Exception[]
        {
            new UnauthorizedAccessException("denied"), new DirectoryNotFoundException("missing"), new PathTooLongException("long"),
            Io(0x80070005), Io(0x80070003), Io(0x80070070), Io(0x80130020), Io(32), Io(33), new Exception("not IO")
        }) Check(!ConfigurationApplyGuard.IsSharingViolation(error), "Other errors must not be classified as Box sharing locks: " + error.HResult);
    }

    private static void ActualApplySequence()
    {
        var events = new List<string>();
        var result = ConfigurationApplyGuard.Apply(() => events.Add("apply"), () => throw new Exception("must not read"),
            () => throw new Exception("must not gate"), () => events.Add("stop"), _ => events.Add("notice"));
        Check(result == ConfigurationApplyResult.Applied && events.SequenceEqual(new[] { "apply" }), "Successful apply never stops Box");

        foreach (var initial in new Exception[] { new UnauthorizedAccessException("denied"), Io(0x80070020), Io(0x80070021) })
        {
            events.Clear();
            result = ConfigurationApplyGuard.Apply(() => { events.Add("apply"); throw initial; }, () => { events.Add("readback"); return true; },
                () => throw new Exception("same configuration must not restart"), () => events.Add("stop"), _ => events.Add("notice"));
            Check(result == ConfigurationApplyResult.ExistingVerified && events.SequenceEqual(new[] { "apply", "readback" }),
                "An exact existing configuration avoids Box restart even after a real write failure");
        }

        var denied = new UnauthorizedAccessException("denied config path");
        events.Clear();
        try
        {
            ConfigurationApplyGuard.Apply(() => { events.Add("apply"); throw denied; }, () => false,
                () => { events.Add("gate"); return true; }, () => events.Add("stop"), _ => events.Add("notice"));
            throw new Exception("Unauthorized must fail");
        }
        catch (InvalidOperationException error)
        {
            Check(ReferenceEquals(error.InnerException, denied) && error.Message.Contains("访问被拒绝") && events.SequenceEqual(new[] { "apply" }),
                "Access denial preserves its cause and never invokes Box gate or stop");
        }

        foreach (var ordinary in new Exception[] { Io(0x80070070), new DirectoryNotFoundException("missing"), new PathTooLongException("long") })
        {
            events.Clear();
            try
            {
                ConfigurationApplyGuard.Apply(() => { events.Add("apply"); throw ordinary; }, () => { events.Add("readback"); return true; },
                    () => true, () => events.Add("stop"), _ => events.Add("notice"));
                throw new Exception("Ordinary IO must propagate");
            }
            catch (Exception error) when (ReferenceEquals(error, ordinary))
            { Check(events.SequenceEqual(new[] { "apply" }), "Ordinary IO errors are not retried or hidden by fallback"); }
        }

        foreach (var code in new uint[] { 0x80070020, 0x80070021 })
        {
            events.Clear(); var calls = 0;
            result = ConfigurationApplyGuard.Apply(() => { events.Add("apply"); if (++calls == 1) throw Io(code); },
                () => { events.Add("readback"); return false; }, () => { events.Add("safe"); return true; },
                () => events.Add("stop"), _ => events.Add("notice"));
            Check(result == ConfigurationApplyResult.AppliedAfterBoxRestart &&
                events.SequenceEqual(new[] { "apply", "readback", "safe", "notice", "stop", "apply" }),
                "Only a sharing/lock violation reaches one safe stop and one retry, after the original apply");
        }

        events.Clear();
        try
        {
            ConfigurationApplyGuard.Apply(() => { events.Add("apply"); throw Io(0x80070020); }, () => false,
                () => false, () => events.Add("stop"), _ => events.Add("notice"));
            throw new Exception("Unknown/gameplay boundary must reject restart");
        }
        catch (InvalidOperationException error)
        { Check(error.InnerException is IOException && events.SequenceEqual(new[] { "apply" }), "Gameplay or unknown boundary preserves the client"); }

        events.Clear(); var attempts = 0;
        try
        {
            ConfigurationApplyGuard.Apply(() => { events.Add("apply"); if (++attempts == 1) throw Io(0x80070020); throw denied; },
                () => false, () => true, () => events.Add("stop"), _ => { });
            throw new Exception("Retry denial must fail");
        }
        catch (InvalidOperationException error)
        { Check(ReferenceEquals(error.InnerException, denied) && attempts == 2 && events.Count(e => e == "stop") == 1, "Retry failure never loops or claims success"); }

        events.Clear();
        try
        {
            ConfigurationApplyGuard.Apply(() => { events.Add("apply"); throw denied; }, () => throw new IOException("unreadable"),
                () => true, () => events.Add("stop"), _ => { });
            throw new Exception("Failed readback must not authorize fallback");
        }
        catch (InvalidOperationException error)
        { Check(ReferenceEquals(error.InnerException, denied) && events.Count == 1, "Unreadable existing config cannot authorize access-denial fallback"); }
    }

    private static void NativeBoundary()
    {
        var started = Now.AddMinutes(-10);
        Check(ConfigurationApplyGuard.IsFreshSessionTime(Now, started, Now.AddSeconds(-1), Now.AddSeconds(-1)), "Current observed/native timestamps");
        foreach (var updated in new DateTime?[] { null, Now.AddSeconds(-31), started.AddSeconds(-1), Now.AddSeconds(1) })
            Check(!ConfigurationApplyGuard.IsFreshSessionTime(Now, started, Now, updated), "Missing, old, previous-session and future native timestamps reject restart");
        Check(!ConfigurationApplyGuard.IsFreshSessionTime(Now, started, Now.AddSeconds(-31), Now), "An old observation cannot certify a current session");
        Check(ConfigurationApplyGuard.MayRestartBox(true, false, false, false, false, false), "Successful enumeration confirms no game");
        Check(!ConfigurationApplyGuard.MayRestartBox(false, false, true, true, false, true), "Enumeration failure is not an absent game");
        Check(ConfigurationApplyGuard.MayRestartBox(true, true, true, true, false, true), "Current logged-in native off safe boundary");
        Check(!ConfigurationApplyGuard.MayRestartBox(true, true, false, true, false, true), "A stale/mismatched/missing login session cannot authorize restart");
        Check(!ConfigurationApplyGuard.MayRestartBox(true, true, true, false, false, true), "An independent running script cannot be interrupted to configure Box");
        Check(!ConfigurationApplyGuard.MayRestartBox(true, true, true, true, true, true), "Any independent gameplay evidence vetoes safe-looking status");
        Check(!ConfigurationApplyGuard.MayRestartBox(true, true, true, true, false, false), "Unknown page cannot authorize restart");

        string Snapshot(string scene = "DRAFT", string state = "MIDRUN", bool inGame = false, DateTime? updated = null, bool runtimeEnabled = false)
            => JsonSerializer.Serialize(new { updatedAtUnixMs = new DateTimeOffset(updated ?? Now).ToUnixTimeMilliseconds(),
                inGameplay = inGame, runtimeEnabled, arenaSelected = true, scene, arenaState = state });
        Check(ConfigurationApplyGuard.TryClassifyNativeBoundary(Snapshot(), Now, started, out var gameplay, out var safe) && !gameplay && safe,
            "Production native parser certifies a current explicit non-gameplay page");
        Check(ConfigurationApplyGuard.TryClassifyNativeBoundary(Snapshot(runtimeEnabled: true), Now, started, out gameplay, out safe) &&
            !gameplay && !safe && !ConfigurationApplyGuard.MayRestartBox(true, true, true, true, gameplay, safe),
            "Cached API off cannot override a freshly enabled native script, even on MIDRUN");
        foreach (var json in new[] { Snapshot("GAMEPLAY"), Snapshot(state: "GAMEPLAY"), Snapshot(inGame: true) })
            Check(ConfigurationApplyGuard.TryClassifyNativeBoundary(json, Now, started, out gameplay, out safe) && gameplay && !safe,
                "Native gameplay field, scene and state each veto restart");
        foreach (var json in new[]
        {
            "", "{", "{}", Snapshot().Replace("\"inGameplay\":false,", ""),
            Snapshot().Replace("\"inGameplay\":false", "\"inGameplay\":\"false\""),
            Snapshot().Replace("\"inGameplay\":false", "\"inGameplay\":true,\"inGameplay\":false"),
            Snapshot(updated: Now.AddSeconds(-31)), Snapshot(updated: Now.AddSeconds(1)), Snapshot(updated: started.AddSeconds(-1))
        }) Check(!ConfigurationApplyGuard.TryClassifyNativeBoundary(json, Now, started, out _, out _), "Bad/missing/stale native data cannot default to safe");
        Check(ConfigurationApplyGuard.TryClassifyNativeBoundary(Snapshot("INITIALIZING", "UNKNOWN"), Now, started, out _, out safe) && !safe,
            "A parseable unknown phase stays unsafe");
        foreach (var text in new[] { "第 2 回合 · 我方回合", "起手留牌", "对局结算", "GAMEPLAY", "关闭但对局仍在进行" })
            Check(ConfigurationApplyGuard.IsGameplayActivity(text), "Native gameplay activity veto: " + text);
        Check(!ConfigurationApplyGuard.IsGameplayActivity("竞技场套牌就绪，等待对局"), "Waiting for a future game is not current gameplay");
        Check(ConfigurationApplyGuard.IsGameplayActivity("第 2 回合 · 等待对局"), "Positive gameplay evidence cannot be hidden by an appended waiting label");
        Check(ConfigurationApplyGuard.IsGameplayActivity("GAMEPLAY 等待对局"), "A native GAMEPLAY marker cannot be hidden by an appended waiting label");
    }

    private static void CompleteReadback()
    {
        var hs = "[Global]\nHsMod Language = zhCN\n[HsMod]\nHsMod.Init.Language = zhCN\n[自动脚本]\n推荐来源 = 盒子文件\n脚本模式 = Standard\n";
        var arena = "[竞技场脚本]\n脚本模式 = 竞技场\n竞技场模式 = 地下竞技场\n本轮最多对局数 = 0\n门票不足自动停止 = false\n金币不足自动停止 = true\n";
        bool ArenaMatches(string h, string a) => ConfigurationApplyGuard.MatchesConfiguration(h, a, "竞技场", "地下竞技场", 0, 0, false, true, "ignored");
        Check(ArenaMatches(hs, arena), "Exact production Arena fallback configuration");
        foreach (var key in new[] { "HsMod Language = zhCN", "HsMod.Init.Language = zhCN", "推荐来源 = 盒子文件", "脚本模式 = Standard" })
            Check(!ArenaMatches(hs.Replace(key, key + "-wrong"), arena), "Every shared HsMod write participates in fallback: " + key);
        foreach (var key in new[] { "脚本模式 = 竞技场", "竞技场模式 = 地下竞技场", "本轮最多对局数 = 0", "门票不足自动停止 = false", "金币不足自动停止 = true" })
            Check(!ArenaMatches(hs, arena.Replace(key, key + "-wrong")), "Every Arena write participates in fallback: " + key);
        Check(ArenaMatches(hs.Replace("[Global]", "[全局]").Replace("HsMod Language", "HsMod语言"), arena), "Existing native Chinese locale spelling");
        Check(!ArenaMatches(hs + "脚本模式 = Wild\n", arena), "Conflicting duplicate native keys cannot certify applied configuration");

        var wildHs = hs.Replace("Standard", "Wild") + "卡组名称 = 复制机械骑\n卡组ID = 0\n场数限制 = 12\n开启或关闭脚本 = F12\n";
        var wildArena = "[竞技场脚本]\n脚本模式 = 狂野\n";
        bool WildMatches(string h, string a, string deck = "复制机械骑") => ConfigurationApplyGuard.MatchesConfiguration(h, a, "狂野", "地下竞技场", 0, 12, false, true, deck);
        Check(WildMatches(wildHs, wildArena), "Exact production constructed fallback configuration");
        foreach (var key in new[] { "卡组名称 = 复制机械骑", "卡组ID = 0", "场数限制 = 12", "开启或关闭脚本 = F12" })
            Check(!WildMatches(wildHs.Replace(key, key + "-wrong"), wildArena), "Every constructed write participates in fallback: " + key);
        Check(!WildMatches(wildHs, wildArena.Replace("狂野", "标准")), "Constructed sidecar mode must agree too");
        Check(WildMatches(wildHs.Replace("卡组名称 = 复制机械骑", "卡组名称 = 其它卡组").Replace("卡组ID = 0", "卡组ID = 99"), wildArena, ""),
            "An empty requested deck preserves the existing native selection");
        Check(ConfigurationApplyGuard.MatchesConfiguration(hs.Replace("Standard", "Battlegrounds") + "场数限制 = 0\n开启或关闭脚本 = F12\n",
            "[竞技场脚本]\n脚本模式 = 酒馆战棋\n", "酒馆战棋", "地下竞技场", 0, 0, false, true, "ignored"), "Battlegrounds never invents a deck requirement");
    }

    private static void LeaseReadback()
    {
        var prior = "{\"restartEnabled\":true,\"scriptActive\":true,\"unrelated\":{\"restartEnabled\":true},\"large\":9007199254740993,\"array\":[1,null,{\"scriptActive\":true}],\"decimal\":1.2300e+25,\"text\":\"保留\\n\\\"值\\\"\"}";
        var changed = ConfigurationApplyGuard.SetWatchdogLeaseValue(prior, false);
        using (var document = JsonDocument.Parse(changed))
        {
            Check(ConfigurationApplyGuard.WatchdogLeaseMatches(changed, false) &&
                document.RootElement.GetProperty("unrelated").GetProperty("restartEnabled").GetBoolean() &&
                document.RootElement.GetProperty("large").GetRawText() == "9007199254740993",
                "Production strict lease writer changes only the two root flags and retains nested flags and exact unknown integers");
            Check(document.RootElement.GetProperty("array")[0].GetInt32() == 1 &&
                document.RootElement.GetProperty("array")[1].ValueKind == JsonValueKind.Null &&
                document.RootElement.GetProperty("array")[2].GetProperty("scriptActive").GetBoolean() &&
                document.RootElement.GetProperty("decimal").GetRawText() == "1.2300e+25" &&
                document.RootElement.GetProperty("text").GetString() == "保留\n\"值\"",
                "Unknown arrays, nulls, nested flags, decimal precision and strings survive strict lease update");
        }
        var events = new List<string>();
        var stored = prior;
        ConfigurationApplyGuard.WriteWatchdogLease(prior, false,
            value => { events.Add("write"); stored = value; }, () => { events.Add("readback"); return stored; });
        Check(events.SequenceEqual(new[] { "write", "readback" }) && ConfigurationApplyGuard.WatchdogLeaseMatches(stored, false),
            "The production strict lease path writes then confirms both flags");
        foreach (var failure in new Exception[] { new UnauthorizedAccessException("lease denied"), Io(0x80070020) })
        {
            events.Clear();
            try
            {
                ConfigurationApplyGuard.WriteWatchdogLease(prior, false, _ => { events.Add("write"); },
                    () => { events.Add("readback"); throw failure; });
                throw new Exception("Readback failure must propagate");
            }
            catch (Exception error) when (ReferenceEquals(error, failure))
            { Check(events.SequenceEqual(new[] { "write", "readback" }), "Unreadable strict lease cannot claim startup ownership"); }
            events.Clear();
            try
            {
                ConfigurationApplyGuard.WriteWatchdogLease(prior, false, _ => { events.Add("write"); throw failure; },
                    () => { events.Add("readback"); return changed; });
                throw new Exception("Write failure must propagate");
            }
            catch (Exception error) when (ReferenceEquals(error, failure))
            { Check(events.SequenceEqual(new[] { "write" }), "A denied/locked strict lease write never reads a false success"); }
        }
        foreach (var readback in new[] { "{", prior, "{\"restartEnabled\":false,\"scriptActive\":true}" })
        {
            try
            {
                ConfigurationApplyGuard.WriteWatchdogLease(prior, false, _ => { }, () => readback);
                throw new Exception("Mismatched readback must fail");
            }
            catch (InvalidDataException) { Check(true, "Malformed or mismatched strict lease readback fails startup"); }
        }
        Check(ConfigurationApplyGuard.WatchdogLeaseHasRequiredFlags("{\"restartEnabled\":true,\"scriptActive\":false}"), "Mixed prior lease flags are writable states");
        Check(ConfigurationApplyGuard.WatchdogLeaseMatches("{\"restartEnabled\":false,\"scriptActive\":false,\"unknown\":1}", false), "Both disabled flags read back and preserve unrelated lease data");
        foreach (var json in new[] { "{", "{}", "{\"restartEnabled\":false}", "{\"restartEnabled\":false,\"scriptActive\":\"false\"}",
            "{\"restartEnabled\":false,\"restartEnabled\":true,\"scriptActive\":false}" })
            Check(!ConfigurationApplyGuard.WatchdogLeaseHasRequiredFlags(json), "Malformed/missing/ambiguous lease must not be reported as written");
        Check(!ConfigurationApplyGuard.WatchdogLeaseMatches("{\"restartEnabled\":false,\"scriptActive\":true}", false), "A partially disabled lease cannot authorize start");
    }
}
