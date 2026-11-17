using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppContext.SetData("ArenaController.DataDirectory", Path.Combine(Path.GetTempPath(),
            "controller-regression-data-" + Guid.NewGuid().ToString("N")));
        if (args.SequenceEqual(new[] { "run-stop-binding" }))
        {
            ArenaRunStopBindingTests.Run();
            return;
        }
        AssertArenaStopBoundary();
        AssertArenaStopEvidence();
        AssertConstructedStopBoundary();
        ConstructedStopTests.Run();
        NativeActionDiagnosticsTests.Run();
        RuntimeLimitTests.Run().GetAwaiter().GetResult();
        ArenaRuntimeConfigurationTests.Run().GetAwaiter().GetResult();
        NativeArenaStopTests.Run();
        ArenaRoundLimitTests.Run();
        ArenaRunStopUiTests.Run();
        ShellLayoutTests.Run();
        ArenaRunStopBindingTests.Run();
        AssertArenaStatusText();
        AssertRecoveryProgress();
        AssertConnectionRecovery();
        AssertConstructedMatchmakingRecovery();
        Require(ArenaController.HsModModeMapping.ToNative("竞技场") == "Standard", "Original native enum excludes Arena; persisted config remains valid");
        Require(ArenaController.HsModModeMapping.ToRuntime("竞技场") == "Arena", "Arena bridge status must match Arena without restarting the same mode");
        foreach (var mode in new[] { "标准", "狂野", "休闲", "酒馆战棋" })
            Require(ArenaController.HsModModeMapping.ToRuntime(mode) == ArenaController.HsModModeMapping.ToNative(mode), "Constructed/BG mode identities stay consistent: " + mode);
        Require(!ArenaController.BattleNetWindowPolicy.HasExplicitLoginWindow(Array.Empty<string>()), "Background Battle.net without windows is not a login page");
        Require(!ArenaController.BattleNetWindowPolicy.HasExplicitLoginWindow(new[] { "", "战网", "Battle.net" }), "Normal launcher titles do not block the official launch request");
        foreach (var title in new[] { "战网登录", "Battle.net - Log in", "Battle.net - Sign in" })
            Require(ArenaController.BattleNetWindowPolicy.HasExplicitLoginWindow(new[] { "战网", title }), "A visible login page waits for human authentication: " + title);
        AssertActivationGate().GetAwaiter().GetResult();
        var root = Path.Combine(Path.GetTempPath(), "arenabox-mode-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var form = (IDisposable?)null;
        try
        {
            AssertDurableLogFallback(root);
            AssertNativeLogReader(root);
            var assembly = Assembly.Load("hsmodPlus.Core");
            var formType = assembly.GetType("ArenaController.MainForm") ?? throw new InvalidOperationException("MainForm 类型不存在");
            var settingsType = assembly.GetType("ArenaController.ControllerSettings") ?? throw new InvalidOperationException("ControllerSettings 类型不存在");
            // Optional C# parameters are not honored by Activator.CreateInstance
            // when no argument array is supplied.  Construct the test form with
            // the explicit false value so the test exercises the real startup
            // contract without opening a task window.
            form = (IDisposable?)Activator.CreateInstance(formType, new object[] { false }) ?? throw new InvalidOperationException("无法创建 MainForm");
            var settings = Activator.CreateInstance(settingsType) ?? throw new InvalidOperationException("无法创建 ControllerSettings");
            settingsType.GetProperty("HsModDirectory")!.SetValue(settings, root);
            formType.GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, settings);
            AssertStartupLogSessionIdentity(form, formType, settings, settingsType, root);
            AssertStopReceiptDeliveryBeforeApiOff(form, formType, root);
            AssertSnapshotReadFailure(form, formType, root);
            AssertCancellationFence(form, formType);
            AssertLoginWaitCancellation(form, formType);
            AssertLongGameStillActive(form, formType, root);
            AssertBattlegroundGameEndBoundary(form, formType, root);
            var read = formType.GetMethod("ReadLadderSummary", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("ReadLadderSummary 不存在");
            AssertLadderReadRecoveryDoesNotCompleteGame(form, read, root);
            AssertArenaLongGameSnapshotFailure(form, formType, root);
            var mode = (ComboBox)formType.GetField("_mode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var deck = (ComboBox)formType.GetField("_deckName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var arenaMode = (ComboBox)formType.GetField("_arenaMode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var updateMode = formType.GetMethod("UpdateModeUi", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var currentLadderMode = formType.GetProperty("CurrentLadderMode", BindingFlags.Instance | BindingFlags.NonPublic)!;
            mode.SelectedItem = "标准"; updateMode.Invoke(form, null); AssertDeckState(deck, true, "标准");
            AssertModeMapping(form, currentLadderMode, "标准", "standard");
            mode.SelectedItem = "休闲"; updateMode.Invoke(form, null); AssertDeckState(deck, true, "休闲");
            AssertModeMapping(form, currentLadderMode, "休闲", "casual");
            mode.SelectedItem = "狂野"; updateMode.Invoke(form, null); AssertDeckState(deck, true, "狂野");
            AssertModeMapping(form, currentLadderMode, "狂野", "wild");
            mode.SelectedItem = "酒馆战棋"; updateMode.Invoke(form, null); AssertDeckState(deck, false, "酒馆战棋");
            AssertModeMapping(form, currentLadderMode, "酒馆战棋", "battlegrounds");
            mode.SelectedItem = "竞技场"; updateMode.Invoke(form, null);
            if (!arenaMode.Enabled) throw new InvalidOperationException("竞技场类型控件未启用");
            AssertConfiguredDeckIdentity(formType);
            AssertConstructedDeckConfiguration(form, formType, settings, settingsType, root);

            Write(root, "ladder_session.csv", "time;mode;result;rank_before;stars_before;legend_before;rank_after;stars_after;legend_after;note;account\n2026-10-05 04:00:00;standard;1;1;0;0;2;0;0;;test\n");
            AssertSummary(read.Invoke(form, new object[] { "standard" })!, games: 1, wins: 1, losses: 0, mmr: "");

            File.Delete(Path.Combine(root, "ladder_session.csv"));
            Write(root, "match.log", "2026/10/5<br />04:00:30,胜利,-,FT_WILD,Opponent\n2026/10/5<br />04:00:40,平局,-,FT_WILD,Opponent\n");
            AssertSummary(read.Invoke(form, new object[] { "wild" })!, games: 2, wins: 1, losses: 0, draws: 1, mmr: "");

            Write(root, "match.log", "2026/10/5<br />04:01:00,胜利,-,GT_CASUAL,Opponent\n2026/10/5<br />04:02:00,失败,-,GT_CASUAL,Opponent\n");
            AssertSummary(read.Invoke(form, new object[] { "casual" })!, games: 2, wins: 1, losses: 1, mmr: "");

            Write(root, "match.log", "2026/10/5<br />04:03:00,胜利,-,GT_BATTLEGROUNDS,Opponent\n");
            Write(root, "bg_last_mmr.txt", "144115198130930503x923717734.solo=171\n144115198130930503x923717734.duo=0\n");
            AssertSummary(read.Invoke(form, new object[] { "battlegrounds" })!, games: 1, wins: 1, losses: 0, mmr: "单人 171 / 双人 0");
            Write(root, "bg_session.csv", "time;mode;place;rating_before;rating_after;delta;note;hero;account\n2026-10-13 17:00:00;solo;3;5646;5690;44;;Hero;Test\n2026-10-13 17:15:00;duo;2;1000;1020;20;;Hero;Test\n2026-10-13 17:38:38;solo;0;5741;5646;-95;synced;;Test\n");
            AssertSummary(read.Invoke(form, new object[] { "battlegrounds" })!, games: 2, wins: 0, losses: 0, mmr: "单人 171 / 双人 0");
            Write(root, "bg_session.csv", "time;mode;place;rating_before;rating_after;delta;note;hero;account\n2026-10-13 17:00:00;solo;3;5646;5690;44;;Hero;Test\n2026-10-13 17:15:00;duo");
            var incomplete = read.Invoke(form, new object[] { "battlegrounds" })!;
            Require(!(bool)incomplete.GetType().GetProperty("Reliable")!.GetValue(incomplete)!, "Partial BG settlement must not silently lower the stop counter");
            File.Delete(Path.Combine(root, "bg_session.csv"));
            Console.WriteLine("PASS: controller mode statistics");
        }
        finally
        {
            form?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void AssertConstructedMatchmakingRecovery()
    {
        var now = new DateTime(2026, 10, 11, 4, 43, 0, DateTimeKind.Local);
        var first = now.AddMinutes(-5);
        var second = now.AddSeconds(-1);
        Require(ArenaController.MatchmakingTimeoutPolicy.IsConstructedTimeout("匹配已持续 301 秒，判定为匹配卡死，尝试主动取消"),
            "构筑匹配超时日志被识别");
        Require(ArenaController.MatchmakingTimeoutPolicy.ShouldRecover(new[] { first, second }, now, DateTime.MinValue,
            scriptEnabled: true, gameplayActive: false),
            "构筑连续两次超时且不在对局时触发恢复");
        Require(!ArenaController.MatchmakingTimeoutPolicy.ShouldRecover(new[] { first, second }, now, DateTime.MinValue,
            scriptEnabled: true, gameplayActive: true),
            "构筑对局中不因匹配超时触发重启");
        Require(!ArenaController.MatchmakingTimeoutPolicy.ShouldRecover(new[] { now.AddMinutes(-11), second }, now, DateTime.MinValue,
            scriptEnabled: true, gameplayActive: false),
            "超出观察窗口的旧超时不触发恢复");
        Console.WriteLine("PASS: constructed matchmaking timeout recovery policy");
    }

    private static void Write(string root, string name, string content) => File.WriteAllText(Path.Combine(root, name), content, new UTF8Encoding(false));

    private static void AssertConfiguredDeckIdentity(Type formType)
    {
        var optionType = formType.GetNestedType("HsModDeckOption", BindingFlags.NonPublic)!;
        var snapshotType = formType.GetNestedType("HsModDeckSnapshot", BindingFlags.NonPublic)!;
        var resolve = formType.GetMethod("ResolveConfiguredConstructedDeck", BindingFlags.Static | BindingFlags.NonPublic)!;
        object Snapshot(params (string Value, string Label)[] choices)
        {
            var values = Array.CreateInstance(optionType, choices.Length);
            for (var i = 0; i < choices.Length; i++)
            {
                var option = Activator.CreateInstance(optionType, true)!;
                optionType.GetProperty("Value")!.SetValue(option, choices[i].Value);
                optionType.GetProperty("Label")!.SetValue(option, choices[i].Label);
                values.SetValue(option, i);
            }
            var snapshot = Activator.CreateInstance(snapshotType, true)!;
            snapshotType.GetProperty("Options")!.SetValue(snapshot, values);
            return snapshot;
        }
        void Select(object snapshot, string name, string mode, string expected)
        {
            var option = resolve.Invoke(null, new[] { snapshot, name, mode })!;
            Require((string)optionType.GetProperty("Value")!.GetValue(option)! == expected,
                "配置名称必须映射到该卡组的原生稳定 ID：" + name);
        }
        void Reject(object snapshot, string name, string mode)
        {
            try { resolve.Invoke(null, new[] { snapshot, name, mode }); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { return; }
            throw new InvalidOperationException("不明确或当前模式不存在的名称不得静默选择其他卡组：" + name);
        }
        var standard = Snapshot(("deck:101", "机械骑  (标准)"), ("deck:102", "宇宙骑  (标准)"));
        Select(standard, "机械骑", "标准", "deck:101");
        Reject(standard, "机械", "标准");
        Reject(standard, "狂野骑", "标准");
        var duplicate = Snapshot(("deck:101", "机械骑  (标准)  #101"), ("deck:202", "机械骑  (标准)  #202"));
        Reject(duplicate, "机械骑", "标准");
        Select(duplicate, "机械骑  (标准)  #202", "标准", "deck:202");
        var casual = Snapshot(("deck:101", "机械骑  (标准)  #101"), ("deck:303", "机械骑  (狂野)  #303"));
        Reject(casual, "机械骑", "休闲");
        Select(casual, "机械骑  (狂野)  #303", "休闲", "deck:303");
        var wild = Snapshot(("deck:303", "机械骑  (狂野)"));
        Select(wild, "机械骑", "狂野", "deck:303");
    }

    private static void AssertConstructedDeckConfiguration(object form, Type formType, object settings, Type settingsType, string root)
    {
        var hsModPath = settingsType.GetProperty("HsModConfigPath")!;
        var arenaPath = settingsType.GetProperty("ArenaConfigPath")!;
        var oldHsModPath = hsModPath.GetValue(settings);
        var oldArenaPath = arenaPath.GetValue(settings);
        var mode = (ComboBox)formType.GetField("_mode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var deck = (ComboBox)formType.GetField("_deckName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var oldMode = mode.SelectedItem;
        var oldDeck = deck.Text;
        var oldAppliedMode = settingsType.GetProperty("Mode")!.GetValue(settings);
        var oldAppliedDeck = settingsType.GetProperty("DeckName")!.GetValue(settings);
        var config = Path.Combine(root, "deck-selection-HsMod.cfg");
        var apply = formType.GetMethod("ApplyConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var verify = formType.GetMethod("VerifySectionKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool IsConfig(string key, string expected) => (bool)verify.Invoke(null, new object[] { config, "自动脚本", key, expected })!;
        void Prepare(string selectedMode, string name)
        {
            File.WriteAllText(config, "[自动脚本]\n卡组名称 = 同名卡组\n卡组ID = 9530174145\n", new UTF8Encoding(false));
            mode.SelectedItem = selectedMode;
            deck.Text = name;
            // ApplyConfiguration reads the applied settings snapshot; the fixture
            // simulates a completed 应用设置, so mirror the selection into it.
            settingsType.GetProperty("Mode")!.SetValue(settings, selectedMode);
            settingsType.GetProperty("DeckName")!.SetValue(settings, name);
            apply.Invoke(form, null);
        }
        try
        {
            hsModPath.SetValue(settings, config);
            arenaPath.SetValue(settings, Path.Combine(root, "deck-selection-Arena.cfg"));
            foreach (var selectedMode in new[] { "标准", "狂野", "休闲" })
            {
                Prepare(selectedMode, "");
                Require(IsConfig("卡组名称", "同名卡组") && IsConfig("卡组ID", "9530174145"),
                    selectedMode + "未指定卡组时必须完整保留原生选择身份，不能把 ID 清零");
            }
            foreach (var selectedMode in new[] { "酒馆战棋", "竞技场" })
            {
                Prepare(selectedMode, "不应用的构筑卡组");
                Require(IsConfig("卡组名称", "同名卡组") && IsConfig("卡组ID", "9530174145"),
                    selectedMode + "不得重写或依赖构筑卡组选择");
            }
            Prepare("标准", "明确指定卡组");
            Require(IsConfig("卡组名称", "明确指定卡组") && IsConfig("卡组ID", "0"),
                "明确指定新名称时必须释放旧 ID，随后通过原生列表确认新身份");
        }
        finally
        {
            hsModPath.SetValue(settings, oldHsModPath);
            arenaPath.SetValue(settings, oldArenaPath);
            settingsType.GetProperty("Mode")!.SetValue(settings, oldAppliedMode);
            settingsType.GetProperty("DeckName")!.SetValue(settings, oldAppliedDeck);
            mode.SelectedItem = oldMode;
            deck.Text = oldDeck;
        }
    }

    private static void AssertDurableLogFallback(string root)
    {
        var blocked = Path.Combine(root, "blocked-log-directory");
        File.WriteAllText(blocked, "This is a file, so the primary directory cannot be created.");
        var fallback = Path.Combine(root, "fallback", "controller.log");
        var log = new ArenaController.DurableLogWriter(Path.Combine(blocked, "controller.log"), fallback);
        Require(log.Write("first diagnostic")?.Contains("日志写入失败") == true, "主日志写入失败必须提供可见原因");
        Require(log.CurrentPath == fallback && File.ReadAllText(fallback).Contains("first diagnostic"), "首条失败记录必须保存在备用文件");
        Require(log.Write("second diagnostic") == null && File.ReadAllText(fallback).Contains("second diagnostic"), "写失败后应持续写入备用日志且不重复报警");
    }

    private static void AssertStopReceiptDeliveryBeforeApiOff(object form, Type type, string root)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var mode = (ComboBox)type.GetField("_mode", flags)!.GetValue(form)!;
        var settingsField = type.GetField("_settings", flags)!;
        var settings = settingsField.GetValue(form)!;
        var modeProperty = settings.GetType().GetProperty("Mode")!;
        var priorMode = mode.SelectedItem;
        var priorAppliedMode = modeProperty.GetValue(settings);
        var requestField = type.GetField("_constructedStopRequest", flags)!;
        var statusField = type.GetField("_hsModStatus", flags)!;
        var successField = type.GetField("_lastHsModSuccessAt", flags)!;
        var priorRequest = requestField.GetValue(form);
        var priorStatus = statusField.GetValue(form);
        var priorSuccess = successField.GetValue(form);
        try
        {
            mode.SelectedItem = "狂野";
            // Runtime behaviour reads the applied snapshot, so a test that
            // simulates a finished mode change must apply it too.
            modeProperty.SetValue(settings, "狂野");
            var started = DateTime.UtcNow.AddMinutes(-5);
            var request = new ArenaControl.Contracts.ConstructedStopRequest
            {
                RequestId = Guid.NewGuid().ToString("N"), GameProcessId = 123,
                GameStartedAtUtcTicks = started.Ticks, Mode = "狂野",
                RequestedAtUtcTicks = DateTime.UtcNow.AddSeconds(-10).Ticks
            };
            requestField.SetValue(form, request);
            var statusType = type.GetNestedType("HsModStatusSnapshot", BindingFlags.NonPublic)!;
            var status = Activator.CreateInstance(statusType)!;
            statusType.GetProperty("Ready")!.SetValue(status, true);
            statusType.GetProperty("ScriptEnabled")!.SetValue(status, true);
            statusField.SetValue(form, status);
            successField.SetValue(form, DateTime.UtcNow);
            Require((bool)type.GetMethod("IsHsModStatusFresh", flags)!.Invoke(form, null)!, "Fixture API ON is fresh");
            var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
            var complete = System.Text.Json.JsonSerializer.Deserialize<ArenaControl.Contracts.ConstructedStopRequest>(System.Text.Json.JsonSerializer.Serialize(request, options), options)!;
            complete.CompletedAtUtcTicks = DateTime.UtcNow.AddSeconds(-5).Ticks;
            var path = Path.Combine(root, ArenaControl.Contracts.ConstructedStopContract.ReceiptFile);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(complete, options));
            var matching = type.GetMethod("HasMatchingConstructedStopReceipt", flags)!;
            Require((bool)matching.Invoke(form, new object[] { 123, started })!, "Exact receipt suppresses duplicate delivery before the API OFF poll arrives");
            Require(!(bool)type.GetMethod("HasConstructedStopReceipt", flags)!.Invoke(form, null)!, "Receipt alone cannot complete a task while fresh API still says ON");
            complete.RequestId = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(complete, options));
            Require(!(bool)matching.Invoke(form, new object[] { 123, started })!, "Other request receipt does not suppress this request");
            File.WriteAllText(path, "{\"SchemaVersion\":1}");
            Require(!(bool)matching.Invoke(form, new object[] { 123, started })!, "Incomplete receipt cannot suppress stop delivery");
        }
        finally
        {
            mode.SelectedItem = priorMode;
            modeProperty.SetValue(settings, priorAppliedMode);
            requestField.SetValue(form, priorRequest);
            statusField.SetValue(form, priorStatus);
            successField.SetValue(form, priorSuccess);
        }
    }

    private static void AssertStartupLogSessionIdentity(object form, Type formType, object settings, Type settingsType, string root)
    {
        settingsType.GetProperty("HearthstoneDirectory")!.SetValue(settings, root);
        var since = new DateTime(2026, 10, 13, 20, 21, 7, 373, DateTimeKind.Local);
        var logs = Path.Combine(root, "Logs");
        var old = Path.Combine(logs, "Hearthstone_2026_10_13_19_36_47");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "Hearthstone.log"), "E 20:19:54 Error.AddFatal() - idle disconnected\n");
        File.WriteAllText(Path.Combine(old, "Login.log"), "I 19:37:05 We are now logged in\n");
        foreach (var path in Directory.GetFiles(old)) File.SetLastWriteTime(path, since.AddSeconds(-1));
        var failure = formType.GetMethod("ReadHearthstoneStartupFailure", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loggedIn = formType.GetMethod("HasHearthstoneLoggedIn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Require(failure.Invoke(form, new object[] { since }) == null, "Fresh writes in an old session cannot reject a new process before its log exists");
        Require(!(bool)loggedIn.Invoke(form, new object[] { since })!, "Old login success cannot authenticate a new process");
        var current = Path.Combine(logs, "Hearthstone_2026_10_13_20_21_14");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "Login.log"), "I 20:21:32 We are now logged in\n");
        Require((bool)loggedIn.Invoke(form, new object[] { since })!, "Delayed current-process login log is accepted");
        File.WriteAllText(Path.Combine(current, "Hearthstone.log"), "E Failed to get token\n");
        Require(failure.Invoke(form, new object[] { since }) != null, "Current-process token failure still blocks activation");
    }

    private static void AssertNativeLogReader(string root)
    {
        var path = Path.Combine(root, "native-reader.log");
        File.WriteAllText(path, "old Arena matchmaking handshake timed out\n", Encoding.UTF8);
        var reader = new ArenaController.NativeLogReader();
        Require(reader.Read(path).Count == 0, "附加观察时旧超时记录不得触发新恢复");
        File.AppendAllText(path, "[Info] same timestamp A\n[Info] same timestamp B\n部分", Encoding.UTF8);
        var first = reader.Read(path);
        Require(first.Count == 2 && first[1].Contains("B"), "相同时间或没有日期的机制记录均应保留");
        File.AppendAllText(path, "行\n", Encoding.UTF8);
        Require(reader.Read(path).Single() == "部分行", "跨采样分段写入必须合并完整中文行");
        Require(reader.Read(path).Count == 0, "同一日志不能重复读取触发恢复");
        File.WriteAllText(path, "rotated\n", Encoding.UTF8);
        Require(reader.Read(path).Count == 0, "日志截断后应重新建立本次观察起点");
        File.AppendAllText(path, "new\n", Encoding.UTF8);
        Require(reader.Read(path).Single() == "new", "日志轮转后必须继续读取新记录");
    }

    private static void AssertSnapshotReadFailure(object form, Type type, string root)
    {
        var path = Path.Combine(root, "arena-runtime-state.json");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(path, "{\"updatedAtUnixMs\":" + now + ",\"arenaSelected\":true,\"runtimeEnabled\":true,\"scene\":\"DRAFT\",\"arenaState\":\"DRAFTING\"}");
        var read = type.GetMethod("TryReadArenaRuntimeState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Require((bool)read.Invoke(form, new object?[] { null })!, "应读取有效原生运行快照");
        File.WriteAllText(path, "{\"updatedAtUnixMs\":");
        Require(!(bool)read.Invoke(form, new object?[] { null })!, "写入中快照不能作为停止或恢复决策的有效证据");
        Require(type.GetField("_lastArenaRuntime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form) != null, "读取短暂失败不能清除最近有效显示状态");
        File.WriteAllText(path, "{\"updatedAtUnixMs\":" + now + ",\"arenaSelected\":true,\"runtimeEnabled\":false,\"scene\":\"DISABLED\"}");
        Require((bool)read.Invoke(form, new object?[] { null })!, "明确新关闭状态必须及时读取");
    }

    private static void AssertCancellationFence(object form, Type type)
    {
        var generation = type.GetField("_taskGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var check = type.GetMethod("ThrowIfTaskInterrupted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        generation.SetValue(form, 7);
        check.Invoke(form, new object[] { 7 });
        generation.SetValue(form, 8);
        try { check.Invoke(form, new object[] { 7 }); throw new InvalidOperationException("旧启动回调应被停止操作阻断"); }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { }
    }

    private static void AssertLoginWaitCancellation(object form, Type type)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        type.GetField("_awaitingBattleNetLogin", flags)!.SetValue(form, true);
        type.GetField("_running", flags)!.SetValue(form, false);
        type.GetField("_starting", flags)!.SetValue(form, false);
        type.GetField("_stopRequested", flags)!.SetValue(form, false);
        var generation = type.GetField("_taskGeneration", flags)!;
        var before = (int)generation.GetValue(form)!;
        type.GetMethod("RequestStop", flags)!.Invoke(form, new object[] { "取消登录等待测试" });
        Require(!(bool)type.GetField("_awaitingBattleNetLogin", flags)!.GetValue(form)!, "Cancel stops automatic resumption after human login");
        Require((int)generation.GetValue(form)! > before, "Cancel fences previous startup callbacks");
        Require((bool)type.GetField("_scheduleStopLatched", flags)!.GetValue(form)!, "Daily schedule cannot immediately restart cancelled login wait");
        Require(!((Button)type.GetField("_stop", flags)!.GetValue(form)!).Enabled, "Cancelled login wait unlocks the task UI");
    }

    private static void AssertLongGameStillActive(object form, Type type, string root)
    {
        var log = Path.Combine(root, "Instances", "auto-1", "LogOutput.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "Gameplay.Awake\n" + string.Join("\n", Enumerable.Repeat("normal diagnostic", 1000)) + "\n", Encoding.UTF8);
        var check = type.GetMethod("IsConstructedGameplayActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Require((bool)check.Invoke(form, null)!, "大量正常机制日志不能挤掉对局开始标记并被误判为空闲");
        File.AppendAllText(log, "WildLadderBot对局总结\n", Encoding.UTF8);
        Require(!(bool)check.Invoke(form, null)!, "明确结算后应退出活对局保护");
    }

    private static void AssertBattlegroundGameEndBoundary(object form, Type type, string root)
    {
        var log = Path.Combine(root, "Instances", "auto-1", "LogOutput.log");
        var check = type.GetMethod("IsConstructedGameplayActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        File.WriteAllText(log, "Gameplay.Awake() - CurrentMode=GAMEPLAY, PrevMode=BACON\nBgAI站位: 随从 -> 3\n", Encoding.UTF8);
        Require((bool)check.Invoke(form, null)!, "战棋站位或回合间战斗不能被当作整局结束");
        File.AppendAllText(log, "Network.DisconnectFromGameServer() - Reason: EndGameScreen\n", Encoding.UTF8);
        Require(!(bool)check.Invoke(form, null)!, "战棋原生终局离开服务器应结束对局保护");
        File.WriteAllText(log, "Gameplay.Awake() - CurrentMode=GAMEPLAY, PrevMode=BACON\nBgSessionStats: place 3, rating 5646 -> 5690 (+44)\n", Encoding.UTF8);
        Require(!(bool)check.Invoke(form, null)!, "战棋原生整局排名结算应确认局后边界");
        File.AppendAllText(log, "Gameplay.Awake() - CurrentMode=GAMEPLAY, PrevMode=BACON\n", Encoding.UTF8);
        Require((bool)check.Invoke(form, null)!, "下一局开始必须覆盖旧战棋结算，不能关闭新对局");
        File.WriteAllText(log, "Gameplay.Awake\nNetwork.OnGameServerDisconnectEvent() - BNet error: ERROR_OK\n", Encoding.UTF8);
        Require((bool)check.Invoke(form, null)!, "没有终局原因的普通断线不能授权停止当前对局");
    }

    private static void AssertLadderReadRecoveryDoesNotCompleteGame(object form, MethodInfo read, string root)
    {
        var path = Path.Combine(root, "ladder_session.csv");
        const string complete = "time;mode;result\n2026-10-07 12:00:00;test-reliability;1\n2026-10-07 12:10:00;test-reliability;0\n";
        File.WriteAllText(path, complete, Encoding.UTF8);
        object Read() => read.Invoke(form, new object[] { "test-reliability" })!;
        bool Reliable(object sample) => (bool)sample.GetType().GetProperty("Reliable")!.GetValue(sample)!;
        int Games(object sample) => (int)sample.GetType().GetProperty("Games")!.GetValue(sample)!;
        var initial = Read(); Require(Reliable(initial) && Games(initial) == 2, "完整战绩应提供可信局数");
        object failed;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) failed = Read();
        Require(!Reliable(failed), "读取共享冲突必须保留未知，不能把0当可信战绩");
        int? stopBaseline = Reliable(failed) ? Games(failed) : null;
        var restored = Read();
        Require(Reliable(restored) && Games(restored) == 2, "IO恢复后应重新读回原有历史");
        Require(!ArenaController.ConstructedStopBoundary.CounterConfirmsCompletion(true, false, stopBaseline, Games(restored)), "恢复旧历史总数不能被当成停止请求之后的新结算");
        File.WriteAllText(path, complete + "2026-10-07 12:20:00;test-reliability", Encoding.UTF8);
        Require(!Reliable(Read()), "尾行半写时不可使用部分已解析战绩");
        File.WriteAllText(path, "", Encoding.UTF8);
        Require(!Reliable(Read()), "文件重写中的空内容不能变成0基线");
        File.WriteAllText(path, complete + "2026-10-07 12:20:00;test-reliability;1\n", Encoding.UTF8);
        var next = Read(); Require(Reliable(next) && Games(next) == 3, "完整追加后应恢复可信统计");
        Require(!ArenaController.ConstructedStopBoundary.CounterConfirmsCompletion(true, true, 2, 3), "即使胜负数量上涨，实际GAMEPLAY也必须阻止关闭");
        Require(!ArenaController.ConstructedStopBoundary.CounterConfirmsCompletion(false, false, 2, 3), "无法排除GAMEPLAY时计数上涨也不能停止");
        Require(ArenaController.ConstructedStopBoundary.CounterConfirmsCompletion(true, false, 2, 3), "可信新结算且明确离开GAMEPLAY后才允许完成停止");
        File.Delete(path);
    }

    private static void AssertArenaLongGameSnapshotFailure(object form, Type type, string root)
    {
        var runtime = Path.Combine(root, "arena-runtime-state.json");
        var log = Path.Combine(root, "arena-box-bot.log");
        File.WriteAllText(runtime, "{partial", Encoding.UTF8);
        File.WriteAllText(log, "entered Arena gameplay\n" + string.Join("\n", Enumerable.Repeat("normal bridge diagnostic", 1200)) + "\n", Encoding.UTF8);
        var check = type.GetMethod("IsArenaGameplayActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Require((bool)check.Invoke(form, null)!, "竞技场JSON失败且大量日志挤掉旧100行开始标记时仍必须保护当前对局");
        var monitor = new ArenaController.ClientRecoveryMonitor();
        var now = DateTime.Now;
        Require(monitor.ObserveGameplay(now, true, false) == null && monitor.ObserveGameplay(now.AddMinutes(10), true, false) == null,
            "正常响应的竞技场仅运行文件持续失联不能触发重启");
        File.AppendAllText(log, "returned from Arena gameplay; reset page-cycle gates\n", Encoding.UTF8);
        Require(!(bool)check.Invoke(form, null)!, "明确离开竞技场对局后应结束活对局保护");
        var readRuntime = type.GetMethod("TryReadArenaRuntimeState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        File.WriteAllText(runtime, "{\"updatedAtUnixMs\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ",\"runtimeEnabled\":true,\"inGameplay\":true,\"scene\":\"GAMEPLAY\"}", Encoding.UTF8);
        Require((bool)readRuntime.Invoke(form, new object?[] { null })!, "缓存最近明确GAMEPLAY证据");
        File.WriteAllText(runtime, "{partial", Encoding.UTF8);
        using (var locked = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require((bool)check.Invoke(form, null)!, "日志和运行快照同时不可读时要保留最近明确活对局状态");
    }

    private static void AssertRecoveryProgress()
    {
        var now = DateTime.Now;
        var monitor = new ArenaController.ArenaProgressMonitor();
        string? Sample(int seconds, bool managed = true, bool stopping = false, bool enabled = true,
            bool gameplay = false, string scene = "DRAFT", string state = "DRAFTING", int slot = 1) =>
            monitor.Observe(now.AddSeconds(seconds), managed, stopping, true, enabled, gameplay, scene, state, slot, 30, 0);
        Require(Sample(0) == null && Sample(19) == null && Sample(359) == null, "视觉重选等待和慢机器选牌不得立即重启");
        Require(Sample(360, slot: 2) == null && Sample(719, slot: 2) == null, "真正选牌进度必须重置卡死时间");
        Require(Sample(721, slot: 2) != null, "持续六分钟没有选择推进应提供恢复原因");
        Require(Sample(1000, gameplay: true, scene: "GAMEPLAY") == null && Sample(5000, gameplay: true, scene: "GAMEPLAY") == null, "正常长对局不能触发按时长重启");
        Require(Sample(6000, managed: false) == null && Sample(7000, managed: false) == null, "未托管不得启动恢复");
        Require(Sample(7100, stopping: true) == null && Sample(8000, enabled: false) == null, "主动停止必须取消进度故障计时");
        Require(Sample(9000, scene: "TOURNAMENT") == null && Sample(9180, scene: "TOURNAMENT")?.Contains("天梯") == true, "错误场景持续三分钟应触发兜底恢复");
        var client = new ArenaController.ClientRecoveryMonitor();
        Require(client.ObserveGameplay(now, true, false) == null && client.ObserveGameplay(now.AddMinutes(10), true, false) == null, "活对局单纯接口失联不能重启");
        Require(client.ObserveGameplay(now.AddMinutes(11), false, false) == null && client.ObserveGameplay(now.AddMinutes(12), false, false) == null, "窗口短时无响应与丢失状态要等待确认");
        Require(client.ObserveGameplay(now.AddMinutes(13), false, false) != null, "窗口及运行状态长期同时失去响应才允许活对局恢复");
        Require(client.ObserveGameplay(now.AddMinutes(14), true, true) == null, "实际恢复应立即清除故障计时");
    }

    private static async Task AssertActivationGate()
    {
        var enabled = false;
        var pending = new TaskCompletionSource<bool>();
        var start = ArenaController.ScriptActivationGate.EnableAsync(() => pending.Task, () => { }, () => enabled = true);
        Require(!enabled && !start.IsCompleted, "收藏尚未加载和卡组未确认时绝不能先开脚本");
        pending.SetResult(true);
        Require(await start && enabled, "确认卡组后才应开启脚本");
        enabled = false;
        try
        {
            await ArenaController.ScriptActivationGate.EnableAsync(() => Task.FromException(new InvalidOperationException("missing deck")), () => { }, () => enabled = true);
            throw new Exception("缺卡组必须阻止激活");
        }
        catch (InvalidOperationException) { Require(!enabled, "缺失指定卡组不得调用脚本开启"); }
        var canceled = false;
        pending = new TaskCompletionSource<bool>();
        start = ArenaController.ScriptActivationGate.EnableAsync(() => pending.Task,
            () => { if (canceled) throw new OperationCanceledException(); }, () => enabled = true);
        canceled = true; pending.SetResult(true);
        try { await start; throw new Exception("恢复等待期间停止必须阻止激活"); }
        catch (OperationCanceledException) { Require(!enabled, "恢复回调不得重新开启已停止的任务"); }
    }

    private static void AssertConnectionRecovery()
    {
        var t = DateTime.Now;
        var monitor = new ArenaController.ConnectionRecoveryMonitor();
        const string invalid = "SendUtilPacket could not send, connection not valid";
        monitor.Observe(t, "Not connected"); monitor.Observe(t, invalid);
        Require(monitor.RecoveryReason(t.AddMinutes(10)) == null, "结算 Not connected 或普通发送错误不能证明会话持续离线");
        monitor.Observe(t, "Error.AddFatal() - message=游戏连接中断");
        for (var i = 1; i <= 12; i++) monitor.Observe(t.AddSeconds(i * 14), invalid);
        Require(monitor.RecoveryReason(t.AddSeconds(179)) == null, "明确掉线仍应给予原生恢复三分钟");
        Require(monitor.RecoveryReason(t.AddSeconds(180)) != null, "持续确认的离线状态应允许恢复");
        monitor.Observe(t.AddSeconds(181), "BoxAI链路Options: optionsId=2, optionCount=6, turn=3");
        Require(monitor.RecoveryReason(t.AddSeconds(182)) == null, "新对局操作状态应取消旧离线判断");
        monitor.Observe(t.AddSeconds(200), "Error.AddFatal() - message=游戏连接中断");
        monitor.Observe(t.AddSeconds(201), "[Login] We are now logged in");
        Require(monitor.RecoveryReason(t.AddMinutes(20)) == null, "重新登录后旧网络错误不得触发重启");

        var dns = new ArenaController.ConnectionRecoveryMonitor();
        dns.Observe(t, "Exception within GetHostAddress for target server eu.actual.battle.net : Could not resolve host");
        dns.Observe(t.AddSeconds(1), "GLOBAL_ERROR_NETWORK_NO_CONNECTION");
        for (var i = 1; i <= 12; i++)
            dns.Observe(t.AddSeconds(i * 14), "SendUtilPacket could not send, connection not valid");
        Require(dns.RecoveryReason(t.AddSeconds(179)) == null, "DNS 断线仍应给予三分钟原生恢复窗口");
        Require(dns.RecoveryReason(t.AddSeconds(180)) != null, "DNS/全局网络失败持续存在时应允许安全恢复");
        dns.Observe(t.AddSeconds(181), "[Login] We are now logged in");
        Require(dns.RecoveryReason(t.AddMinutes(20)) == null, "DNS 恢复登录后不得继续触发重启");

        var primed = new ArenaController.ConnectionRecoveryMonitor();
        primed.PrimeNetworkFailure(t);
        Require(primed.RecoveryReason(t.AddSeconds(179)) == null, "重启后从 FATAL 历史证据预热仍应保留三分钟窗口");
        Require(primed.RecoveryReason(t.AddSeconds(180)) != null, "重启后已处于安全 FATAL 边界时应能继续恢复");

        //04:00 live regression: network errors stop after the first burst,
        // but the same client continues publishing FATAL_ERROR for minutes.
        var silentFatal = new ArenaController.ConnectionRecoveryMonitor();
        for (var i = 0; i < 3; i++) silentFatal.Observe(t.AddSeconds(i), "GLOBAL_ERROR_NETWORK_NO_CONNECTION");
        Require(silentFatal.RecoveryReason(t.AddSeconds(180)) == null, "无当前fatal证据时，旧网络错误仍须过期");
        Require(silentFatal.RecoveryReason(t.AddSeconds(179), true) == null, "当前fatal也必须保留三分钟原生恢复窗口");
        Require(silentFatal.RecoveryReason(t.AddSeconds(180), true) != null, "当前进程的新鲜fatal不能因错误停止刷屏而永久漏恢复");
        silentFatal.Observe(t.AddSeconds(181), "BoxAI链路Options: optionsId=27, optionCount=14, turn=9");
        Require(silentFatal.RecoveryReason(t.AddSeconds(182), true) == null, "新的对局动作包必须清除旧fatal序列，即使快照尚未更新");
        var noNetwork = new ArenaController.ConnectionRecoveryMonitor();
        Require(noNetwork.RecoveryReason(t.AddMinutes(10), true) == null, "仅fatal页面不能替代明确的网络失败序列");

        var startedUtc = t.ToUniversalTime();
        var updatedUtc = startedUtc.AddSeconds(10);
        Require(ArenaController.ConnectionRecoveryMonitor.IsCurrentFatalScene(34972, 34972, true, true, false, "FATAL_ERROR", updatedUtc, startedUtc), "当前PID与新鲜非对局fatal可以确认断线仍未恢复");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentFatalScene(34972, 26428, true, true, false, "FATAL_ERROR", updatedUtc, startedUtc), "旧PID的fatal不能授权恢复新客户端");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentFatalScene(34972, 34972, false, true, false, "FATAL_ERROR", updatedUtc, startedUtc), "过期API不能绑定当前fatal");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentFatalScene(34972, 34972, true, false, false, "FATAL_ERROR", updatedUtc, startedUtc), "过期场景不能延长错误有效期");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentFatalScene(34972, 34972, true, true, true, "FATAL_ERROR", updatedUtc, startedUtc), "活动对局不能被fatal快照关闭");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentFatalScene(34972, 34972, true, true, false, "GAMEPLAY", updatedUtc, startedUtc), "健康场景不能延长错误有效期");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentFatalScene(34972, 34972, true, true, false, "FATAL_ERROR", startedUtc.AddSeconds(-1), startedUtc), "进程启动前发布的fatal不能用于新实例");

        var loginIdle = new ArenaController.ConnectionRecoveryMonitor();
        for (var i = 0; i < 3; i++) loginIdle.Observe(t.AddSeconds(i), "Network.OnFatalBnetError: ERROR_SDK_SOCKET_CLOSED", true);
        loginIdle.Observe(t.AddSeconds(3), "[Login] We are now logged in", true);
        Require(loginIdle.RecoveryReason(t.AddSeconds(182), false, true) == null, "重新登录后仍给予三分钟恢复进展窗口");
        Require(loginIdle.RecoveryReason(t.AddSeconds(183), false, false) == null, "没有当前安全菜单证据不能恢复登录停滞");
        Require(loginIdle.RecoveryReason(t.AddSeconds(183), false, true) != null, "运行任务的登录成功日志不能掩盖没有新排队或操作的停滞");
        loginIdle.Observe(t.AddSeconds(184), "GameMgr.ChangeFindGameState() - state: BNET_QUEUE_DELAYED", true);
        Require(loginIdle.RecoveryReason(t.AddMinutes(10), true, true) == null, "服务端排队响应应清除登录前的断线证据");
        Require(ArenaController.ConnectionRecoveryMonitor.IsCurrentIdleScene(34972, 34972, true, true, false, "TOURNAMENT", updatedUtc, startedUtc), "当前安全菜单可以确认登录后的停滞");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentIdleScene(34972, 34972, true, true, true, "TOURNAMENT", updatedUtc, startedUtc), "活动对局不能以菜单快照重启");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentIdleScene(34972, 26428, true, true, false, "TOURNAMENT", updatedUtc, startedUtc), "旧PID菜单不能授权当前客户端恢复");
        Require(!ArenaController.ConnectionRecoveryMonitor.IsCurrentIdleScene(34972, 34972, true, true, false, "REWARDS", updatedUtc, startedUtc), "领奖不是允许登录停滞恢复的菜单");

        var timeout = new ArenaController.ConnectionRecoveryMonitor();
        var timeoutLine = "BoxAI链路超时: type=new_recommendation_unavailable_or_unmappable, waitedSeconds=8.1, optionsId=20";
        Require(timeout.ObserveRecommendationTimeout(t, timeoutLine)?.Contains("单次超时") == true, "单次推荐超时应被记录");
        timeout.ObserveRecommendationTimeout(t.AddSeconds(1), timeoutLine);
        timeout.Observe(t.AddSeconds(2), "BoxAI链路Options: optionsId=22, optionCount=16, turn=19");
        Require(timeout.ObserveRecommendationTimeout(t.AddSeconds(2), timeoutLine.Replace("optionsId=20", "optionsId=22"))?.Contains("连续超时=3") == true, "跨 options 的连续超时不能被新包清零");
        timeout.ObserveRecommendationTimeout(t.AddSeconds(3), "BoxAI链路读取: requestId=box-2-22-end_turn, optionsId=22");
        Require(timeout.RecommendationTimeoutStreak == 3, "读到可能无法映射的推荐不代表恢复");
        Require(timeout.ObserveRecommendationTimeout(t.AddSeconds(4), "BoxAI链路提交: requestId=box-2-22-play_minion, optionsId=22")?.Contains("动作提交") == true && timeout.RecommendationTimeoutStreak == 0, "动作提交后记录恢复并清零");
        timeout.ObserveRecommendationTimeout(t.AddSeconds(5), timeoutLine);
        timeout.ObserveRecommendationTimeout(t.AddSeconds(6), "WildLadderBot对局总结: result=失败");
        Require(timeout.RecommendationTimeoutStreak == 0 && timeout.RecoveryReason(t.AddHours(1)) == null, "对局边界清零，超时本身不能授权断线重启");
    }

    private static void AssertSummary(object summary, int games, int wins, int losses, int draws = 0, string mmr = "")
    {
        var type = summary.GetType();
        var actualGames = (int)type.GetProperty("Games")!.GetValue(summary)!;
        var actualWins = (int)type.GetProperty("Wins")!.GetValue(summary)!;
        var actualLosses = (int)type.GetProperty("Losses")!.GetValue(summary)!;
        var actualDraws = (int)type.GetProperty("Draws")!.GetValue(summary)!;
        var actualMmr = (string)type.GetProperty("Mmr")!.GetValue(summary)!;
        if (actualGames != games || actualWins != wins || actualLosses != losses || actualDraws != draws || !string.Equals(actualMmr, mmr, StringComparison.Ordinal))
            throw new InvalidOperationException($"统计不匹配：games={actualGames}, wins={actualWins}, losses={actualLosses}, draws={actualDraws}, mmr={actualMmr}");
    }

    private static void AssertDeckState(ComboBox deck, bool expected, string mode)
    {
        if (deck.Enabled != expected) throw new InvalidOperationException($"{mode} 卡组控件状态错误：Enabled={deck.Enabled}");
    }

    private static void AssertArenaStopBoundary()
    {
        Require(ArenaController.ArenaStopBoundary.IsKnownSafe(false, false, false, false, "", ""),
            "炉石已退出时应允许收尾");
        Require(ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, true, false, "DRAFT", "DRAFTING"),
            "选牌页应允许安全停止");
        Require(ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, true, false, "DRAFT", "MIDRUN"),
            "套牌完成但尚未匹配时应允许安全停止");
        Require(ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, true, false, "DRAFT", "REDRAFTING"),
            "重选牌页应允许安全停止");
        Require(ArenaController.ArenaStopBoundary.IsKnownSafe(true, false, false, false, "DISABLED", ""),
            "明确禁用快照应允许安全停止");
        Require(!ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, true, true, "GAMEPLAY", "MIDRUN"),
            "对局中即使状态文本异常也不得安全停止");
        Require(!ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, true, false, "GAMEPLAY", "MIDRUN"),
            "GAMEPLAY场景必须覆盖错误的false对局标记及MIDRUN状态");
        Require(!ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, false, false, "GAMEPLAY", "DISABLED"),
            "GAMEPLAY场景下脚本关闭不代表当前对局已经结束");
        Require(ArenaController.ArenaStopBoundary.IsKnownSafe(false, true, false, false, "GAMEPLAY", "DISABLED"),
            "进程确已退出时陈旧GAMEPLAY场景不应阻止任务收尾");
        Require(!ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, true, false, "", ""),
            "炉石运行但状态快照缺失时必须等待");
        Require(!ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, true, false, "DRAFT", "REWARDS"),
            "奖励页必须等待奖励收尾信号");
    }

    private static void AssertArenaStopEvidence()
    {
        var request = new DateTime(2026, 10, 7, 14, 0, 0, DateTimeKind.Local);
        var evidence = new ArenaController.ArenaStopLogEvidence(request, request.AddHours(-1), 123);
        string Record(int seconds, string message) => request.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message;
        evidence.Observe(Record(-30, "controller stop-after-game request completed"), 123);
        var rewardsSafe = ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, true, false, "DRAFT", "REWARDS");
        Require(!ArenaController.ArenaStopBoundary.CompleteFromEvidence(true, rewardsSafe, true),
            "当前奖励快照不安全时，即使日志含完成记录也不能覆盖当前结论");
        Require(!ArenaController.ArenaStopBoundary.CompleteFromEvidence(true, null, evidence.IsCompleteFor(123)),
            "快照过期时上次任务完成记录不能替代本次停止确认");
        evidence.Observe(Record(2, "returned from Arena gameplay"), 123);
        evidence.Observe(Record(3, "underground arena state=REWARDS"), 123);
        Require(!evidence.IsCompleteFor(123), "退出对局但奖励未完成时仍应等待");
        evidence.Observe(Record(5, "controller stop-after-game request completed"), 999);
        Require(!evidence.IsCompleteFor(123), "其他炉石进程的完成记录不能确认本次请求");
        evidence.Observe(Record(6, "controller stop-after-game request completed"), 123);
        Require(ArenaController.ArenaStopBoundary.CompleteFromEvidence(true, null, evidence.IsCompleteFor(123)),
            "快照不可读但本次本进程在奖励之后明确确认完成，应正常结束停止");
        Require(!ArenaController.ArenaStopBoundary.CompleteFromEvidence(true, rewardsSafe, evidence.IsCompleteFor(123)),
            "当前有效奖励快照仍优先于任何日志推断");
        var stoppedSafe = ArenaController.ArenaStopBoundary.IsKnownSafe(true, true, false, false, "STOPPED", "STOPPED");
        Require(ArenaController.ArenaStopBoundary.CompleteFromEvidence(true, stoppedSafe, false), "原生确认停止后无需旧日志即可完成");
        evidence.Observe(Record(7, "entered Arena gameplay"), 123);
        Require(!evidence.IsCompleteFor(123), "更新的对局开始事件必须撤销之前日志的完成状态");
    }

    private static void AssertConstructedStopBoundary()
    {
        Require(ArenaController.ConstructedStopBoundary.IsKnownSafe("idle"),
            "HsMod 空闲状态应允许收尾");
        Require(ArenaController.ConstructedStopBoundary.IsKnownSafe("等待主菜单"),
            "HsMod 主菜单状态应允许收尾");
        Require(!ArenaController.ConstructedStopBoundary.IsKnownSafe(""),
            "HsMod 状态未知时必须等待");
        Require(!ArenaController.ConstructedStopBoundary.IsKnownSafe("GAMEPLAY"),
            "HsMod 对局中不得安全停止");
        Require(!ArenaController.ConstructedStopBoundary.IsKnownSafe("对局进行中"),
            "中文对局状态不得安全停止");
        Require(!ArenaController.ConstructedStopBoundary.IsKnownSafe("等待盒子建议"),
            "对局中等待盒子建议不得安全停止");
        Require(ArenaController.ConstructedStopBoundary.IsKnownSafe("等待盒子建议", false),
            "脚本已关闭且等待盒子建议应允许配置安全边界");
        Require(!ArenaController.ConstructedStopBoundary.IsKnownSafe("等待盒子建议", true),
            "脚本仍开启时等待盒子建议不得单独授权重启");
    }

    private static void AssertArenaStatusText()
    {
        Require(ArenaController.ArenaStatusText.DescribeRunState(false, false) == "等待新轮次",
            "没有竞技场轮次时状态文本错误");
        Require(ArenaController.ArenaStatusText.DescribeRunState(true, true) == "竞技场进行中",
            "炉石运行时竞技场状态文本错误");
        Require(ArenaController.ArenaStatusText.DescribeRunState(true, false) == "未运行（有未完成轮次）",
            "炉石退出但有未完成轮次时状态文本错误");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertModeMapping(object form, PropertyInfo currentLadderMode, string display, string expected)
    {
        // CurrentLadderMode reads the applied settings snapshot, so the fixture
        // must apply the simulated selection first.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var settings = form.GetType().GetField("_settings", flags)!.GetValue(form)!;
        settings.GetType().GetProperty("Mode")!.SetValue(settings, display);
        var actual = (string?)currentLadderMode.GetValue(form) ?? "";
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{display} 模式映射错误：{actual}");
    }
}
