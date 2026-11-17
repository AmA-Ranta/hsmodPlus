using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using ArenaController;

static class Smoke
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("用法：InstallerSmoke.exe <完整安装包目录或 ZIP>");
            return 2;
        }

        var packageInput = Path.GetFullPath(args[0]);
        var package = packageInput;
        string? extractedPackage = null;
        if (File.Exists(packageInput) && string.Equals(Path.GetExtension(packageInput), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Extraction belongs in the system temp directory. Keeping it
            // beside the ZIP used to leave a hidden directory in whatever
            // folder the caller selected (including the root of D:\).
            extractedPackage = Path.Combine(Path.GetTempPath(),
                "ArenaBoxBot-installer-smoke-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractedPackage);
            ZipFile.ExtractToDirectory(packageInput, extractedPackage);
            package = extractedPackage;
        }
        var controllerExe = Path.Combine(package, "hsmodPlus.exe");
        if (!Directory.Exists(package) || !File.Exists(controllerExe))
            return Fail("安装包缺少 hsmodPlus.exe：" + package);

        // A release package must be runnable and auditable, not a copied build
        // tree.  Debug symbols and compiler staging directories never belong
        // in the user-facing ZIP.
        Require(!Directory.EnumerateDirectories(package, "bin", SearchOption.AllDirectories).Any(), "发布包包含 bin 编译目录");
        Require(!Directory.EnumerateDirectories(package, "obj", SearchOption.AllDirectories).Any(), "发布包包含 obj 编译目录");
        Require(!Directory.EnumerateFiles(package, "*.pdb", SearchOption.AllDirectories).Any(), "发布包包含 PDB 调试符号");

        Require(HsModModeMapping.ToNative("标准") == "Standard", "标准模式枚举映射错误");
        Require(HsModModeMapping.ToNative("狂野") == "Wild", "狂野模式枚举映射错误");
        Require(HsModModeMapping.ToNative("休闲") == "Casual", "休闲模式枚举映射错误");
        Require(HsModModeMapping.ToNative("酒馆战棋") == "Battlegrounds", "酒馆战棋模式枚举映射错误");
        Require(HsModModeMapping.ToNative("竞技场") == "Standard", "竞技场虚拟模式原生值错误");
        Require(HsModModeMapping.ToDisplay("Battlegrounds") == "酒馆战棋", "酒馆战棋显示映射错误");

        // Keep test fixtures in the system temp directory. Never create a
        // fixture beside the package or at a drive root; this test is run
        // repeatedly during release builds and must leave user folders clean.
        var root = Path.Combine(Path.GetTempPath(),
            "ArenaBoxBot-installer-smoke-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "Hearthstone");
        var box = Path.Combine(root, "Box");
        try
        {
            Directory.CreateDirectory(game);
            Directory.CreateDirectory(box);
            File.WriteAllBytes(Path.Combine(game, "Hearthstone.exe"), new byte[] { 0x4D, 0x5A });
            File.WriteAllBytes(Path.Combine(box, "HSAng.exe"), new byte[] { 0x4D, 0x5A });
            var oldPlugins = Path.Combine(game, "BepInEx", "plugins");
            Directory.CreateDirectory(oldPlugins);
            File.WriteAllText(Path.Combine(oldPlugins, "OldThirdPartyPlugin.dll"), "old plugin");
            File.WriteAllText(Path.Combine(oldPlugins, "UniverseLib.Mono.dll"), "stale dependency");
            var oldState = Path.Combine(game, "BepInEx", "HsMod");
            Directory.CreateDirectory(oldState);
            var oldScoringBytes = new byte[] { 0x11, 0x22, 0x33 };
            File.WriteAllBytes(Path.Combine(oldState, "boxai-direct-profile.dpapi"), oldScoringBytes);
            File.WriteAllText(Path.Combine(oldState, "boxai-direct-profile.invalid"), "probe_rejected\n");
            File.WriteAllText(Path.Combine(oldState, "arena-stats.json"), "preserved-entry-ledger");
            File.WriteAllText(Path.Combine(oldState, "boxai-direct-capture.log"), "preserved-capture-history");

            // Deliberately point the request at an unrelated fake DLL. The
            // service must still use the package beside the controller.
            var external = Path.Combine(root, "wrong-ArenaBoxBot.dll");
            File.WriteAllText(external, "wrong source");
            var request = new NativeInstallRequest
            {
                HearthstoneDirectory = game,
                BoxExecutablePath = Path.Combine(box, "HSAng.exe"),
                PluginSourcePath = external,
                HsModSourcePath = external,
                UniverseLibSourcePath = external,
                VersionDllSourcePath = external
            };
            var service = new NativeInstallService(package);
            var discovery = service.Discover(request);
            Require(discovery.PluginSourcePath == Path.Combine(package, "ArenaBoxBot.dll"), "插件源未锁定到安装包");
            Require(discovery.HsModSourcePath == Path.Combine(package, "HsMod.dll"), "HsMod 源未锁定到安装包");
            Require(discovery.BepInExSourcePath == Path.Combine(package, "BepInEx"), "安装包缺少 BepInEx 源");
            Require(discovery.DoorstopSourcePath == Path.Combine(package, "winhttp.dll"), "安装包缺少 winhttp.dll");
            Require(discovery.RankBypassTemplatePath == Path.Combine(package, "RankBypass.ini.template"), "安装包缺少 RankBypass.ini.template");

            var boxFromDirectory = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = game,
                BoxExecutablePath = box
            });
            Require(string.Equals(boxFromDirectory.BoxExecutablePath, Path.Combine(box, "HSAng.exe"), StringComparison.OrdinalIgnoreCase), "选择盒子目录未规范化为 HSAng.exe");

            // Some Box installations expose a versioned child directory in
            // the picker.  A bounded nested probe must resolve the exact
            // HSAng.exe without accepting an unrelated executable.
            var nestedBox = Path.Combine(root, "BoxNested");
            var nestedBoxChild = Path.Combine(nestedBox, "runtime", "current");
            Directory.CreateDirectory(nestedBoxChild);
            File.WriteAllBytes(Path.Combine(nestedBoxChild, "HSAng.exe"), new byte[] { 0x4D, 0x5A });
            var fromNestedBox = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = game,
                BoxExecutablePath = nestedBox
            });
            Require(string.Equals(fromNestedBox.BoxExecutablePath, Path.Combine(nestedBoxChild, "HSAng.exe"), StringComparison.OrdinalIgnoreCase), "嵌套盒子目录未解析 HSAng.exe");

            // A missing box must not erase a successfully resolved game path
            // or be reported as “炉石不存在”.
            var gameWithoutBox = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = game,
                BoxExecutablePath = Path.Combine(root, "missing-box", "HSAng.exe")
            });
            Require(string.Equals(gameWithoutBox.HearthstoneDirectory, game, StringComparison.OrdinalIgnoreCase), "盒子缺失时不应丢失已识别的炉石目录");
            Require(gameWithoutBox.PathFailureReason?.Contains("HSAng.exe", StringComparison.OrdinalIgnoreCase) == true, "盒子缺失时用户错误未明确指出 HSAng.exe");

            // The UI accepts a directory, Hearthstone.exe, or the Unity
            // *_Data directory. All three forms must resolve to the same
            // installation root instead of producing a misleading "not
            // found" error during installation.
            var fromExe = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = Path.Combine(game, "Hearthstone.exe"),
                BoxExecutablePath = Path.Combine(box, "HSAng.exe")
            });
            Require(string.Equals(fromExe.HearthstoneDirectory, game, StringComparison.OrdinalIgnoreCase), "直接选择 Hearthstone.exe 未规范化为游戏目录");
            var dataDirectory = Path.Combine(game, "Hearthstone_Data");
            Directory.CreateDirectory(dataDirectory);
            var fromData = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = dataDirectory,
                BoxExecutablePath = Path.Combine(box, "HSAng.exe")
            });
            Require(string.Equals(fromData.HearthstoneDirectory, game, StringComparison.OrdinalIgnoreCase), "选择 Hearthstone_Data 未规范化为游戏目录");
            var managedDirectory = Path.Combine(dataDirectory, "Managed");
            Directory.CreateDirectory(managedDirectory);
            var fromManaged = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = managedDirectory,
                BoxExecutablePath = Path.Combine(box, "HSAng.exe")
            });
            Require(string.Equals(fromManaged.HearthstoneDirectory, game, StringComparison.OrdinalIgnoreCase), "选择 Hearthstone_Data\\Managed 未向上规范化为游戏目录");
            var fromFileUri = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = new Uri(game).AbsoluteUri,
                BoxExecutablePath = new Uri(Path.Combine(box, "HSAng.exe")).AbsoluteUri
            });
            Require(string.Equals(fromFileUri.HearthstoneDirectory, game, StringComparison.OrdinalIgnoreCase), "file URI 形式的炉石路径未规范化为游戏目录");
            Require(string.Equals(fromFileUri.BoxExecutablePath, Path.Combine(box, "HSAng.exe"), StringComparison.OrdinalIgnoreCase), "file URI 形式的盒子路径未规范化为 HSAng.exe");

            var commandLineGame = Path.Combine(root, "Game With Spaces");
            Directory.CreateDirectory(Path.Combine(commandLineGame, "Hearthstone_Data"));
            File.WriteAllBytes(Path.Combine(commandLineGame, "Hearthstone.exe"), new byte[] { 0x4D, 0x5A });
            var fromCommandLine = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = "\"" + Path.Combine(commandLineGame, "Hearthstone.exe") + "\" --launcher=hearthstone",
                BoxExecutablePath = Path.Combine(box, "HSAng.exe")
            });
            Require(string.Equals(fromCommandLine.HearthstoneDirectory, commandLineGame, StringComparison.OrdinalIgnoreCase), "带引号和启动参数的炉石路径未规范化");
            var fromQuotedFileUri = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = "\"" + new Uri(commandLineGame).AbsoluteUri + "\" --launcher=hearthstone",
                BoxExecutablePath = "\"" + new Uri(Path.Combine(box, "HSAng.exe")).AbsoluteUri + "\""
            });
            Require(string.Equals(fromQuotedFileUri.HearthstoneDirectory, commandLineGame, StringComparison.OrdinalIgnoreCase), "带引号 file URI 的炉石路径未规范化");
            Require(string.Equals(fromQuotedFileUri.BoxExecutablePath, Path.Combine(box, "HSAng.exe"), StringComparison.OrdinalIgnoreCase), "带引号 file URI 的盒子路径未规范化");

            var dataOnlyGame = Path.Combine(root, "DataOnly");
            var dataOnly = Path.Combine(dataOnlyGame, "Hearthstone_Data");
            Directory.CreateDirectory(dataOnly);
            File.WriteAllBytes(Path.Combine(dataOnly, "globalgamemanagers"), new byte[] { 0x01 });
            var fromDataOnly = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = dataOnlyGame,
                BoxExecutablePath = Path.Combine(box, "HSAng.exe")
            });
            Require(string.Equals(fromDataOnly.HearthstoneDirectory, dataOnlyGame, StringComparison.OrdinalIgnoreCase), "更新中的 Data-only 炉石目录未被识别");

            // During a launcher repair Hearthstone.exe can be temporarily
            // absent while the official launcher and Unity data are already
            // present.  This is still the correct installation target for a
            // loader repair and must not be reported as a missing game.
            var launcherOnly = Path.Combine(root, "LauncherOnly");
            Directory.CreateDirectory(Path.Combine(launcherOnly, "Hearthstone_Data"));
            File.WriteAllBytes(Path.Combine(launcherOnly, "Hearthstone Beta Launcher.exe"), new byte[] { 0x4D, 0x5A });
            var fromLauncher = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = launcherOnly,
                BoxExecutablePath = Path.Combine(box, "HSAng.exe")
            });
            Require(string.Equals(fromLauncher.HearthstoneDirectory, launcherOnly, StringComparison.OrdinalIgnoreCase), "只有官方炉石启动器时未识别安装目录");

            var missingDiscovery = service.Discover(new NativeInstallRequest
            {
                HearthstoneDirectory = Path.Combine(root, "missing-game"),
                BoxExecutablePath = Path.Combine(root, "missing-box", "HSAng.exe")
            });
            Require(missingDiscovery.HearthstoneDirectory == null, "不存在的显式路径不应被识别为炉石目录");
            Require(!string.IsNullOrWhiteSpace(missingDiscovery.DeveloperLogPath) && File.Exists(missingDiscovery.DeveloperLogPath), "探测失败未生成开发者诊断日志");
            Require(!string.IsNullOrWhiteSpace(missingDiscovery.UserLogPath) && File.Exists(missingDiscovery.UserLogPath), "探测失败未生成用户操作日志");
            Require(File.ReadAllText(missingDiscovery.UserLogPath!).Contains("[失败]", StringComparison.Ordinal), "探测失败的用户日志缺少简明错误");
            Require(missingDiscovery.Diagnostics.Any(x => x.Contains("game.candidate.result=not-a-game-root", StringComparison.Ordinal)), "探测日志缺少失败候选结果");
            Require(missingDiscovery.Diagnostics.Any(x => x.Contains("game.candidate.probe=", StringComparison.Ordinal)), "探测日志缺少游戏文件证据");

            // Exercise the normalizer with the kind of stale template that
            // caused the original HSAExternal process block: wrong and
            // duplicate keys must be replaced, not merely followed by another
            // value.
            var rankTemplatePath = Path.Combine(package, "RankBypass.ini.template");
            var originalRankTemplate = File.ReadAllText(rankTemplatePath);
            File.WriteAllText(rankTemplatePath,
                "[rank_bypass]\nEnableCollector=0\nOutputDir=C:\\old\\HsMod\nBlockProcesses=2\nBlockProcesses=0\n",
                new UTF8Encoding(false));

            NativeInstallResult install;
            try { install = service.Install(request); }
            finally { File.WriteAllText(rankTemplatePath, originalRankTemplate, new UTF8Encoding(false)); }
            if (!install.Success) return Fail(string.Join(Environment.NewLine, install.Lines));
            Require(!string.IsNullOrWhiteSpace(install.DeveloperLogPath) && File.Exists(install.DeveloperLogPath), "安装器未生成开发者诊断日志");
            Require(install.DeveloperLines.Any(x => x.Contains("resolved.game=", StringComparison.Ordinal)), "开发者日志缺少路径解析信息");

            var plugins = Path.Combine(game, "BepInEx", "plugins");
            Require(Hash(Path.Combine(package, "HsMod.dll")) == "CF2DDED0A8968366A5DF6949DAAD255E2E219D20EF3B7D8DA2C6F96C1C513F97", "HsMod.dll 不是 1031 更新源");
            Require(Hash(Path.Combine(package, "version.dll")) == "A43EBBAFB792DFB6E816F4973048692A3BB2E61EDC19FD82D5F36BC83A566B87", "version.dll 不是 1031 更新源");
            Require(File.Exists(Path.Combine(game, "BepInEx", "core", "BepInEx.dll")), "未安装 BepInEx 核心");
            Require(File.Exists(Path.Combine(game, "winhttp.dll")), "未安装 winhttp.dll");
            Require(File.Exists(Path.Combine(game, "doorstop_config.ini")), "未安装 doorstop_config.ini");
            Require(File.Exists(Path.Combine(game, "BepInEx", "config", "HsMod.cfg")), "首次安装未创建 HsMod.cfg");
            var installedHsModConfig = File.ReadAllText(Path.Combine(game, "BepInEx", "config", "HsMod.cfg"));
            Require(!File.Exists(Path.Combine(oldState, "boxai-direct-profile.dpapi")) && !File.Exists(Path.Combine(oldState, "boxai-direct-profile.invalid")), "旧评分缓存仍被当成当前会话保留在活动目录");
            Require(!string.IsNullOrEmpty(install.BackupDirectory) && File.ReadAllBytes(Path.Combine(install.BackupDirectory!, "boxai-direct-profile.dpapi")).SequenceEqual(oldScoringBytes), "旧保护资料未按原字节备份");
            Require(File.ReadAllText(Path.Combine(install.BackupDirectory!, "boxai-direct-profile.invalid")) == "probe_rejected\n", "旧评分失效标记未保留回退备份");
            Require(File.ReadAllText(Path.Combine(oldState, "arena-stats.json")) == "preserved-entry-ledger", "刷新评分缓存不应清除真实轮次台账");
            Require(File.ReadAllText(Path.Combine(oldState, "boxai-direct-capture.log")) == "preserved-capture-history", "刷新评分缓存不应清除采集历史");
            using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "BepInEx", "HsMod", "native-arena-install.json"))))
            {
                var installedNative = Path.Combine(game, "BepInEx", "plugins", "HsMod.dll");
                Require(manifest.RootElement.GetProperty("hsModVersion").GetString() == System.Reflection.AssemblyName.GetAssemblyName(installedNative).Version?.ToString(), "安装记录未使用实际原版程序集版本");
                var modes = manifest.RootElement.GetProperty("mode").GetString() ?? "";
                Require(modes.Contains("休闲", StringComparison.Ordinal) && modes.Contains("酒馆战棋", StringComparison.Ordinal), "安装记录遗漏已支持的休闲和酒馆模式");
            }
            Require(installedHsModConfig.Contains("推荐来源 = 盒子文件", StringComparison.Ordinal), "HsMod.cfg 未写入盒子文件来源");
            Require(installedHsModConfig.Contains("HsMod Language = zhCN", StringComparison.Ordinal), "HsMod.cfg 未固定为简体中文界面");
            Require(installedHsModConfig.Contains("HsMod.Init.Language = zhCN", StringComparison.Ordinal), "HsMod.cfg 的初始化语言仍为英文");
            Require(installedHsModConfig.Contains(Path.Combine(game, "BepInEx", "HsMod", "Instances", "auto-1", "match.log"), StringComparison.OrdinalIgnoreCase), "HsMod.cfg 的 Match Log 未指向当前游戏目录");
            var localizedConfig = Path.Combine(root, "localized-HsMod.cfg");
            File.WriteAllText(localizedConfig, "[HsMod]\nHsMod.Init.Language = enUS\n\n[全局]\nHsMod语言 = enUS\n", new UTF8Encoding(false));
            NativeInstallService.EnsureChineseLanguage(localizedConfig);
            var localizedText = File.ReadAllText(localizedConfig);
            Require(localizedText.Contains("HsMod.Init.Language = zhCN", StringComparison.Ordinal), "中文 HsMod 配置未同步初始化语言");
            Require(localizedText.Contains("HsMod语言 = zhCN", StringComparison.Ordinal), "中文 HsMod 配置未同步界面语言");
            Require(!localizedText.Contains("[Global]", StringComparison.Ordinal), "中文 HsMod 配置被追加了英文 Global 节");
            Require(!File.Exists(Path.Combine(oldPlugins, "OldThirdPartyPlugin.dll")), "安装后仍残留未授权的旧插件");
            Require(Directory.GetFiles(Path.Combine(game, "BepInEx", "HsMod", "InstallBackups"), "OldThirdPartyPlugin.dll", SearchOption.AllDirectories).Length == 1, "旧插件未进入可恢复备份");
            foreach (var file in new[] { "HsMod.dll", "ArenaBoxBot.dll", "UniverseLib.Mono.dll" })
                Require(Hash(Path.Combine(package, file)) == Hash(Path.Combine(plugins, file)), file + " 哈希不一致");
            Require(Hash(Path.Combine(package, "version.dll")) == Hash(Path.Combine(box, "version.dll")), "version.dll 哈希不一致");
            var rankBypass = Path.Combine(box, "RankBypass.ini");
            Require(File.Exists(rankBypass), "盒子目录未生成 RankBypass.ini");
            var rankBypassText = File.ReadAllText(rankBypass);
            Require(rankBypassText.Contains("EnableCollector=1", StringComparison.OrdinalIgnoreCase), "RankBypass.ini 未启用盒子采集");
            Require(rankBypassText.Contains("CaptureCallback=onUpdateArenaRecommend", StringComparison.Ordinal), "RankBypass.ini 未配置竞技场推荐事件");
            Require(rankBypassText.Contains("EnableLog=1", StringComparison.OrdinalIgnoreCase), "RankBypass.ini 未启用竞技场推荐日志输出");
            Require(Regex.IsMatch(rankBypassText, @"(?im)^\s*BlockProcesses\s*=\s*$"), "RankBypass.ini 未关闭进程拦截");
            Require(!rankBypassText.Contains("BlockProcesses=2", StringComparison.OrdinalIgnoreCase), "RankBypass.ini 仍保留旧的进程拦截值");
            Require(rankBypassText.Split('\n').Count(x => x.TrimStart().StartsWith("BlockProcesses=", StringComparison.OrdinalIgnoreCase)) == 1, "RankBypass.ini 存在重复 BlockProcesses 键");
            Require(rankBypassText.Contains(Path.Combine(game, "BepInEx", "HsMod"), StringComparison.OrdinalIgnoreCase), "RankBypass.ini 输出目录未指向 HsMod");

            var restore = service.RestoreOriginal(game, Path.Combine(box, "HSAng.exe"), null);
            if (!restore.Success) return Fail(string.Join(Environment.NewLine, restore.Lines));
            Require(!Directory.Exists(Path.Combine(game, "BepInEx")), "纯净还原后仍有 BepInEx");
            Require(!File.Exists(Path.Combine(game, "winhttp.dll")), "纯净还原后仍有 winhttp.dll");
            Require(!File.Exists(Path.Combine(game, "doorstop_config.ini")), "纯净还原后仍有 doorstop_config.ini");
            Require(!File.Exists(Path.Combine(box, "version.dll")), "纯净还原后仍有盒子 version.dll");
            Require(!File.Exists(rankBypass), "纯净还原后仍有 RankBypass.ini");

            var invalid = service.Install(new NativeInstallRequest
            {
                HearthstoneDirectory = Path.Combine(root, "does-not-exist"),
                BoxExecutablePath = Path.Combine(box, "HSAng.exe")
            });
            Require(!invalid.Success, "明确指定不存在的炉石路径时不应静默安装到其他默认目录");
            Require(invalid.Lines.Any(x => x.StartsWith("[失败]", StringComparison.Ordinal)), "失败安装缺少用户摘要");
            Require(!string.IsNullOrWhiteSpace(invalid.DeveloperLogPath) && File.Exists(invalid.DeveloperLogPath), "失败安装未生成开发者诊断日志");
            Require(invalid.DeveloperLines.Any(x => x.Contains("game.candidate", StringComparison.Ordinal)), "开发者日志缺少候选路径诊断");
            Console.WriteLine("PASS: clean install and pure restore");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            try { if (extractedPackage != null && Directory.Exists(extractedPackage)) Directory.Delete(extractedPackage, true); } catch { }
        }
    }

    private static string Hash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("FAIL: " + message);
        return 1;
    }
}
