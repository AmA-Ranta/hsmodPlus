using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ArenaController;

/// <summary>Inputs for the one-click, offline installation.</summary>
public sealed class NativeInstallRequest
{
    public string? HearthstoneDirectory { get; set; }
    public string? BoxExecutablePath { get; set; }
    public string? PluginSourcePath { get; set; }
    public string? HsModSourcePath { get; set; }
    public string? UniverseLibSourcePath { get; set; }
    public string? VersionDllSourcePath { get; set; }
    public bool CleanupKnownLegacyFiles { get; set; } = true;
}

public sealed record NativeInstallDiscovery
{
    public string? HearthstoneDirectory { get; init; }
    public string? BoxExecutablePath { get; init; }
    public string? PluginSourcePath { get; init; }
    public string? HsModSourcePath { get; init; }
    public string? UniverseLibSourcePath { get; init; }
    public string? VersionDllSourcePath { get; init; }
    public string? RankBypassTemplatePath { get; init; }
    public string? BepInExSourcePath { get; init; }
    public string? DoorstopSourcePath { get; init; }
    public string? DoorstopConfigSourcePath { get; init; }
    public IReadOnlyList<string> LegacyFiles { get; init; } = Array.Empty<string>();
    /// <summary>Machine-readable path probes retained for the developer log.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Path to a developer diagnostic written when discovery cannot resolve a
    /// required location.  The normal UI keeps this hidden unless discovery
    /// fails, but the file is always available for support.
    /// </summary>
    public string? DeveloperLogPath { get; init; }
    public string? UserLogPath { get; init; }
    public string? PathFailureReason { get; init; }
}

public sealed class NativeInstallResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    /// <summary>
    /// Detailed installer diagnostics. These are deliberately kept out of the
    /// normal user report and are shown only when installation fails.
    /// </summary>
    public IReadOnlyList<string> DeveloperLines { get; init; } = Array.Empty<string>();
    public string? DeveloperLogPath { get; init; }
    public string? UserLogPath { get; init; }
    public string? OperationId { get; init; }
    public string? FailureStage { get; init; }
    public string? BackupDirectory { get; init; }
    public string? ResolvedHearthstoneDirectory { get; init; }
    public string? Error => Success ? null : Lines.LastOrDefault(x => x.StartsWith("[失败]", StringComparison.Ordinal));
}

/// <summary>
/// Installs only the native HsMod + ArenaBoxBot bundle.  It intentionally does
/// not copy a second game/box executable and does not require this controller
/// to remain open after installation.
/// </summary>
public sealed class NativeInstallService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr processHandle, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private readonly string _packageDirectory;
    private readonly string _developerLogDirectory;

    public NativeInstallService(string? packageDirectory = null)
    {
        _packageDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(packageDirectory)
            ? AppContext.BaseDirectory
            : packageDirectory);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        _developerLogDirectory = Path.Combine(localAppData, "hsmodPlus", "logs");
    }

    private NativeInstallResult Result(bool success, List<string> lines, DiagnosticLogSession trace, string? backupDirectory, string? resolvedGame = null)
    {
        trace.Complete(success);
        if (trace.WriteErrors.Count > 0) lines.Add("[提示] 部分日志写入失败，报告中仍保留排查详情；请复制本报告。");
        return new NativeInstallResult
        {
            Success = success,
            Lines = lines.ToArray(),
            DeveloperLines = trace.DeveloperLines.Concat(trace.WriteErrors).ToArray(),
            DeveloperLogPath = trace.DeveloperPath,
            UserLogPath = trace.UserPath,
            OperationId = trace.Id,
            FailureStage = success ? null : trace.Stage,
            BackupDirectory = backupDirectory,
            ResolvedHearthstoneDirectory = resolvedGame
        };
    }

    private string? WriteDeveloperLog(IReadOnlyList<string> lines, string outcome)
    {
        try
        {
            Directory.CreateDirectory(_developerLogDirectory);
            var path = Path.Combine(_developerLogDirectory, $"installer-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{outcome}.log");
            var header = new[]
            {
                "hsmodPlus installer developer diagnostics",
                $"timeLocal={DateTime.Now:O}",
                $"timeUtc={DateTime.UtcNow:O}",
                $"packageDirectory={_packageDirectory}",
                $"baseDirectory={AppContext.BaseDirectory}",
                $"workingDirectory={Environment.CurrentDirectory}",
                $"outcome={outcome}",
                $"processId={Environment.ProcessId}",
                $"processName={Environment.ProcessPath ?? "<unknown>"}",
                $"runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}",
                $"architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
                $"user={Environment.UserDomainName}\\{Environment.UserName}",
                $"os={Environment.OSVersion}",
                ""
            };
            File.WriteAllLines(path, header.Concat(lines), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes a controller-side diagnostic for failures outside the installer.</summary>
    public static string? WriteControllerDeveloperLog(string context, Exception exception)
    {
        using var trace = new DiagnosticLogSession("controller-error");
        trace.Step(context);
        trace.DeveloperLines.Add(exception.ToString());
        trace.User("操作失败：" + exception.Message);
        trace.Complete(false);
        return trace.DeveloperPath;
    }

    /// <summary>Normalizes a user-entered directory, executable, or *_Data path.</summary>
    public static string? ResolveGameRootPath(string? input)
        => ResolveGameRoot(input, null, allowDataOnly: true);

    public NativeInstallResult RestoreOriginal(string hearthstoneDirectory, string? boxExecutablePath, string? hsModSourcePath)
    {
        var lines = new List<string>();
        using var trace = new DiagnosticLogSession("restore", _packageDirectory, _developerLogDirectory);
        var developer = trace.DeveloperLines;
        try
        {
            trace.Step("resolve-paths");
            developer.Add($"restore.gameInput={hearthstoneDirectory}");
            developer.Add($"restore.boxInput={boxExecutablePath ?? "<null>"}");
            var normalizedRoot = ResolveGameRoot(hearthstoneDirectory, developer, allowDataOnly: true);
            if (normalizedRoot == null)
                throw new DirectoryNotFoundException("未找到可识别的炉石安装目录；需要 Hearthstone.exe、官方启动器或带 Unity 元数据的 Hearthstone_Data 目录；还原不会在错误路径创建目录");
            hearthstoneDirectory = normalizedRoot;
            var runningGame = FindHearthstoneProcesses(hearthstoneDirectory);
            if (runningGame.Count != 0)
                throw new InvalidOperationException("还原前请先退出炉石传说（包括后台 Hearthstone.exe）");
            var boxDirectoryForRestore = string.IsNullOrWhiteSpace(boxExecutablePath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(boxExecutablePath));
            var boxProcessesForRestore = FindBoxProcesses(boxDirectoryForRestore);
            if (boxProcessesForRestore.Count != 0)
                throw new InvalidOperationException("还原前请先退出炉石传说盒子及其辅助进程：" +
                    string.Join("、", boxProcessesForRestore.Select(DescribeProcess)));
            var bepinex = Path.Combine(hearthstoneDirectory, "BepInEx");
            var plugins = Path.Combine(bepinex, "plugins");
            // Pure restore means no injected plugins remain in the live game.
            // The bundled HsMod.dll is kept in the controller package and is
            // copied back only by Install, never by this cleanup action.
            Directory.CreateDirectory(plugins);
            var removedPlugins = 0;
            if (Directory.Exists(plugins))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(plugins))
                {
                    if (Directory.Exists(entry)) DeleteDirectoryForce(entry); else DeleteFileForce(entry);
                    removedPlugins++;
                }
            }
            lines.Add("[通过] 已清空 BepInEx/plugins（移除 " + removedPlugins + " 个插件文件或目录）");
            var manifest = Path.Combine(hearthstoneDirectory, "BepInEx", "HsMod", "native-arena-install.json"); DeleteFileForce(manifest);
            foreach (var generated in new[] {
                Path.Combine(hearthstoneDirectory, "BepInEx", "config", "local.hsmod.arenaboxbot.cfg"),
                Path.Combine(hearthstoneDirectory, "BepInEx", "HsMod", "arena-box-bot.log"),
                Path.Combine(hearthstoneDirectory, "BepInEx", "HsMod", "hsmod-arena-mode.json"),
                Path.Combine(hearthstoneDirectory, "BepInEx", "HsMod", "arena-runtime-state.json")
            }) DeleteFileForce(generated);
            var configDir = Path.Combine(bepinex, "config");
            if (Directory.Exists(configDir))
                foreach (var dir in Directory.EnumerateDirectories(configDir, "auto-*")) DeleteDirectoryForce(dir);
            var backups = Path.Combine(hearthstoneDirectory, "BepInEx", "HsMod", "InstallBackups");
            DeleteDirectoryForce(backups);
            foreach (var configFile in new[] { "HsMod.cfg", "HsSkins.cfg" })
            {
                var file = Path.Combine(configDir, configFile);
                DeleteFileForce(file);
            }
            // Remove the complete loader footprint so the game directory is
            // equivalent to a fresh install. This includes BepInEx itself
            // and UnityDoorstop bootstrap files at the game root.
            DeleteDirectoryForce(bepinex);
            foreach (var bootstrap in new[] { "winhttp.dll", "doorstop_config.ini" })
            {
                var file = Path.Combine(hearthstoneDirectory, bootstrap);
                if (File.Exists(file)) { DeleteFileForce(file); lines.Add("[通过] 已移除炉石加载器文件：" + bootstrap); }
            }
            if (!string.IsNullOrWhiteSpace(boxExecutablePath))
            {
                var boxDirectory = Path.GetDirectoryName(boxExecutablePath)!;
                foreach (var boxFile in new[] { "version.dll", "RankBypass.ini" })
                {
                    var path = Path.Combine(boxDirectory, boxFile);
                    if (File.Exists(path)) { DeleteFileForce(path); lines.Add("[通过] 已移除盒子 " + boxFile); }
                }
            }
            lines.Add("[完成] 已还原为原版炉石与原版盒子文件");
            foreach (var line in lines) trace.User(line);
            return Result(true, lines, trace, null);
        }
        catch (Exception ex) { lines.Add("[失败] " + ex.Message); developer.Add(ex.ToString()); foreach (var line in lines) trace.User(line); return Result(false, lines, trace, null); }
    }
    /// <summary>Windows refuses to delete or overwrite a read-only file. The box's
    /// version.dll ships read-only, so install/restore must clear that flag first
    /// instead of failing halfway through with Access denied.</summary>
    internal static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    internal static void DeleteFileForce(string path)
    {
        if (!File.Exists(path)) return;
        ClearReadOnly(path);
        File.Delete(path);
    }

    internal static void DeleteDirectoryForce(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                ClearReadOnly(file);
        }
        catch { }
        Directory.Delete(path, true);
    }
    private static readonly string[] LegacyPluginNames =
    {
        // Explicitly retired custom plugin. UniverseLib is a dependency and is
        // deliberately not listed here.
        "PirateWarHsMod.dll"
    };

    private static readonly string[] LegacyMarkerNames =
    {
        // These files were used by older controller builds as a second start
        // authority. They are archived during install so HsMod's native mode
        // and F12 remain the only way to start a script.
        "arena-bot.enabled",
        "arena-bot.disabled",
        "controller-stop-after-game.marker",
        "constructed-script.enabled"
    };
    private static readonly string[] LegacyStateNames =
    {
        "arena-bot.enabled",
        "controller-stop-after-game.marker"
    };
    private static readonly HashSet<string> BundledPluginNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HsMod.dll",
        "ArenaBoxBot.dll",
        "UniverseLib.Mono.dll"
    };

    public NativeInstallDiscovery Discover(NativeInstallRequest? request = null)
    {
        request ??= new NativeInstallRequest();
        using var trace = new DiagnosticLogSession("discovery", _packageDirectory, _developerLogDirectory);
        try
        {
            var result = DiscoverCore(request, trace.DeveloperLines);
            var success = result.HearthstoneDirectory != null && result.BoxExecutablePath != null;
            if (!success)
                trace.User("[失败] " + (result.PathFailureReason ?? "未找到可识别的炉石或盒子路径。"));
            else
                trace.User("[通过] 已识别炉石与炉石盒子路径");
            trace.Complete(success);
            return result with { DeveloperLogPath = trace.DeveloperPath, UserLogPath = trace.UserPath };
        }
        catch (Exception ex)
        {
            trace.Step("discover-error");
            trace.DeveloperLines.Add(ex.ToString());
            var reason = "路径探测发生异常：" + ex.Message + "；请查看开发者日志。";
            trace.User("[失败] " + reason);
            trace.Complete(false);
            return new NativeInstallDiscovery
            {
                Diagnostics = trace.DeveloperLines.ToArray(),
                DeveloperLogPath = trace.DeveloperPath,
                UserLogPath = trace.UserPath,
                PathFailureReason = reason
            };
        }
    }

    private NativeInstallDiscovery DiscoverCore(NativeInstallRequest request, DiagnosticLogSession.DiagnosticLines diagnostics)
    {
        diagnostics.Add("operation=discover");
        diagnostics.Add($"request.gameInput={request.HearthstoneDirectory ?? "<null>"}");
        diagnostics.Add($"request.boxInput={request.BoxExecutablePath ?? "<null>"}");
        // An explicitly entered path is authoritative. Never silently fall
        // back to another installation when the user supplied a typo or an
        // executable path that cannot be resolved; doing so could install
        // into the wrong game directory. Automatic probing is used only when
        // no path was supplied by the caller.
        var explicitGamePath = !string.IsNullOrWhiteSpace(request.HearthstoneDirectory);
        IEnumerable<string?> gameCandidates = explicitGamePath
            ? new[] { request.HearthstoneDirectory }
            : new[]
            {
                RunningGameInstallPath(diagnostics),
                Environment.GetEnvironmentVariable("HEARTHSTONE_DIR"),
                @"D:\Hearthstone",
                @"C:\Program Files (x86)\Hearthstone",
                @"C:\Program Files\Hearthstone",
                @"E:\Hearthstone"
            }.Concat(RegistryGameInstallPaths(diagnostics));
        // Registry/launcher candidates are trusted installation hints.  Keep
        // the Data-only fallback enabled for them too: during a Battle.net
        // repair the executable can be temporarily absent while the Unity
        // data directory is already the correct write target.
        var game = ResolveGameRoot(gameCandidates, diagnostics, allowDataOnly: true);

        var baseDir = _packageDirectory;
        // The release package is self-contained. Do not fall back to a
        // developer checkout or arbitrary DLL supplied through settings.
        var plugin = FirstFileWithDiagnostics(new[] { Path.Combine(baseDir, "ArenaBoxBot.dll") }, "package.plugin", diagnostics);
        var hsModSource = FirstFileWithDiagnostics(new[] { Path.Combine(baseDir, "HsMod.dll") }, "package.hsmod", diagnostics);
        var universeSource = FirstFileWithDiagnostics(new[] { Path.Combine(baseDir, "UniverseLib.Mono.dll") }, "package.universe", diagnostics);
        var versionDllSource = FirstFileWithDiagnostics(new[] { Path.Combine(baseDir, "version.dll") }, "package.version", diagnostics);
        var rankBypassTemplate = FirstFileWithDiagnostics(new[] { Path.Combine(baseDir, "RankBypass.ini.template") }, "package.rankBypass", diagnostics);
        var bepinexSource = FirstDirectoryWithDiagnostics(new[] { Path.Combine(baseDir, "BepInEx") }, "package.bepinex", diagnostics);
        var doorstopSource = FirstFileWithDiagnostics(new[] { Path.Combine(baseDir, "winhttp.dll") }, "package.winhttp", diagnostics);
        var doorstopConfigSource = FirstFileWithDiagnostics(new[] { Path.Combine(baseDir, "doorstop_config.ini") }, "package.doorstopConfig", diagnostics);
        var boxCandidates = !string.IsNullOrWhiteSpace(request.BoxExecutablePath)
            ? new[] { request.BoxExecutablePath }
            : new[] {
                @"D:\炉石传说盒子\HSAng.exe",
                @"C:\炉石传说盒子\HSAng.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "炉石传说盒子", "HSAng.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "炉石传说盒子", "HSAng.exe") };
        var box = FirstBoxExecutableWithDiagnostics(boxCandidates, diagnostics);
        var legacy = game == null ? Array.Empty<string>() : LegacyPluginNames
            .Select(x => Path.Combine(game, "BepInEx", "plugins", x))
            .Concat(LegacyMarkerNames.Select(x => Path.Combine(game, "BepInEx", "HsMod", x)))
            .Where(File.Exists).ToArray();
        diagnostics.Add($"resolved.game={game ?? "<none>"}");
        diagnostics.Add($"resolved.box={box ?? "<none>"}");
        diagnostics.Add($"package.plugin={plugin ?? "<none>"}");
        diagnostics.Add($"package.hsmod={hsModSource ?? "<none>"}");
        diagnostics.Add($"package.bepinex={bepinexSource ?? "<none>"}");
        diagnostics.Add($"package.version={versionDllSource ?? "<none>"}");
        var missing = new List<string>();
        if (game == null) missing.Add("Hearthstone.exe");
        if (box == null) missing.Add("HSAng.exe");
        if (plugin == null) missing.Add("ArenaBoxBot.dll");

        if (bepinexSource == null) missing.Add("BepInEx");
        if (doorstopSource == null) missing.Add("winhttp.dll");
        if (doorstopConfigSource == null) missing.Add("doorstop_config.ini");

        if (rankBypassTemplate == null) missing.Add("RankBypass.ini.template");
        if (missing.Count > 0) diagnostics.Add("discover.missing=" + string.Join(",", missing));
        // Keep path failures separate from package failures.  Previously a
        // missing box or a missing bundled DLL could be surfaced as the
        // generic “炉石不存在”, even when Hearthstone.exe had been proven to
        // exist.  The UI can now tell the user exactly which prerequisite is
        // missing while the developer log still contains every probe.
        var pathFailures = new List<string>();
        if (game == null) pathFailures.Add(ExplainGamePathFailure(request.HearthstoneDirectory, diagnostics));
        if (box == null) pathFailures.Add(ExplainBoxPathFailure(request.BoxExecutablePath, diagnostics));
        var packageMissing = missing
            .Where(x => !string.Equals(x, "Hearthstone.exe", StringComparison.Ordinal) &&
                        !string.Equals(x, "HSAng.exe", StringComparison.Ordinal))
            .ToArray();
        if (packageMissing.Length > 0)
            pathFailures.Add("炉石和盒子路径已探测，但安装包缺少内置材料：" + string.Join("、", packageMissing) + "。请使用完整的 hsmodPlus 安装包目录。");
        var pathFailure = pathFailures.Count == 0 ? null : string.Join(" ", pathFailures);
        return new NativeInstallDiscovery { HearthstoneDirectory = game, BoxExecutablePath = box, PluginSourcePath = plugin, HsModSourcePath = hsModSource, UniverseLibSourcePath = universeSource, VersionDllSourcePath = versionDllSource, RankBypassTemplatePath = rankBypassTemplate, BepInExSourcePath = bepinexSource, DoorstopSourcePath = doorstopSource, DoorstopConfigSourcePath = doorstopConfigSource, LegacyFiles = legacy, Diagnostics = diagnostics.ToArray(), PathFailureReason = pathFailure };
    }

    public NativeInstallResult Install(NativeInstallRequest request, IProgress<string>? progress = null)
    {
        var lines = new List<string>();
        using var trace = new DiagnosticLogSession("installer", _packageDirectory, _developerLogDirectory);
        var developer = trace.DeveloperLines;
        void Log(string text)
        {
            // Per-file copies, hashes and backup paths belong in the mechanism log.
            if (text.StartsWith("[备份]", StringComparison.Ordinal) || text.StartsWith("[通过] 已安装 ", StringComparison.Ordinal) || !text.StartsWith("[", StringComparison.Ordinal))
            { developer.Add(text); return; }
            lines.Add(text); trace.User(text); progress?.Report(text);
        }
        string? root = null;
        string? backupDir = null;
        string? staged = null;
        var moved = new List<(string Target, string Backup)>();
        try
        {
        trace.Step("resolve-paths");
        var discovery = DiscoverCore(request, developer);
        developer.Add($"install.request.gameInput={request.HearthstoneDirectory ?? "<null>"}");
        developer.Add($"install.request.boxInput={request.BoxExecutablePath ?? "<null>"}");
        developer.Add($"install.request.cleanupKnownLegacyFiles={request.CleanupKnownLegacyFiles}");
        root = discovery.HearthstoneDirectory;
        var source = discovery.PluginSourcePath;
        var hsModSource = discovery.HsModSourcePath;
        var universeSource = discovery.UniverseLibSourcePath;
        var versionDllSource = discovery.VersionDllSourcePath;
        var rankBypassTemplate = discovery.RankBypassTemplatePath;
        backupDir = root == null ? null : Path.Combine(root, "BepInEx", "HsMod", "InstallBackups", trace.Id);
        staged = root == null ? null : Path.Combine(root, "BepInEx", ".arena-install-" + Guid.NewGuid().ToString("N"));
            if (root == null) throw new DirectoryNotFoundException(discovery.PathFailureReason);
            trace.Step("check-running-processes");
            // Only block installation when the running game owns the selected
            // target directory.  A different Hearthstone installation must
            // not prevent a safe install into this explicitly chosen root;
            // the old global process check made the installer fail its own
            // isolated smoke test and made multi-install setups unusable.
            var gameProcesses = FindHearthstoneProcesses(root);
            developer.Add("install.runningGameProcesses=" + gameProcesses.Count);
            foreach (var process in gameProcesses)
            {
                developer.Add("install.runningGame=" + DescribeProcess(process));
                process.Dispose();
            }
            if (gameProcesses.Count != 0)
                throw new InvalidOperationException("炉石传说正在运行，请完全退出后再安装（包括后台 Hearthstone.exe）");
            var boxProcesses = FindBoxProcesses(discovery.BoxExecutablePath == null
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(discovery.BoxExecutablePath)));
            developer.Add("install.runningBoxProcesses=" + boxProcesses.Count);
            foreach (var process in boxProcesses)
                developer.Add("install.runningBox=" + DescribeProcess(process));
            if (boxProcesses.Count != 0)
                throw new InvalidOperationException("炉石传说盒子或辅助进程仍在运行，请完全退出后再安装：" +
                    string.Join("、", boxProcesses.Select(DescribeProcess)));
            trace.Step("verify-package");
            if (source == null) throw new FileNotFoundException("未找到 ArenaBoxBot.dll 安装源");
            if (discovery.BepInExSourcePath == null) throw new FileNotFoundException("安装包缺少 BepInEx 基础运行时");
            if (discovery.DoorstopSourcePath == null || discovery.DoorstopConfigSourcePath == null) throw new FileNotFoundException("安装包缺少炉石加载器文件 winhttp.dll 或 doorstop_config.ini");
            if (discovery.BoxExecutablePath == null) throw new FileNotFoundException("未找到炉石传说盒子 HSAng.exe；请在中控中指定盒子位置");
            var versionTargetPath = Path.Combine(Path.GetDirectoryName(discovery.BoxExecutablePath)!, "version.dll");
            // Plugin independence: a user-managed version.dll is kept as-is.
            // Only demand the bundled copy when the target has none.
            if (versionDllSource == null && !(File.Exists(versionTargetPath) && new FileInfo(versionTargetPath).Length >= 100 * 1024))
                throw new FileNotFoundException("未找到盒子所需的 version.dll；请把 version.dll 放入中控目录或盒子目录后重试");
            if (rankBypassTemplate == null) throw new FileNotFoundException("安装包缺少 RankBypass.ini.template，无法配置盒子采集链路");
            var packageIntegrity = VerifyPackagePayloads();
            developer.Add("install.packageIntegrity=" + (packageIntegrity.Success ? "ok" : packageIntegrity.Message));
            if (!packageIntegrity.Success) throw new InvalidDataException(packageIntegrity.Message);
            developer.Add($"install.target.root={root}");
            developer.Add($"install.target.plugins={Path.Combine(root, "BepInEx", "plugins")}");
            developer.Add($"install.target.boxDirectory={Path.GetDirectoryName(discovery.BoxExecutablePath)}");
            developer.Add($"install.backupDirectory={backupDir}");
            developer.Add($"install.stagingDirectory={staged}");
            if (!File.Exists(source) || new FileInfo(source).Length < 32 * 1024) throw new InvalidDataException("ArenaBoxBot.dll 文件不完整");
            if (versionDllSource != null && new FileInfo(versionDllSource).Length < 100 * 1024) throw new InvalidDataException("version.dll 文件不完整或不是盒子兼容版本");
            var plugins = Path.Combine(root, "BepInEx", "plugins");
            var hsMod = Path.Combine(plugins, "HsMod.dll");
            var arena = Path.Combine(plugins, "ArenaBoxBot.dll");
            if (!File.Exists(hsMod))
            {
                if (hsModSource == null) throw new FileNotFoundException("找不到 HsMod.dll，且安装包未提供原始 HsMod.dll", hsMod);
                Log("[信息] 目标目录没有 HsMod.dll，将使用安装包内原始 HsMod.dll");
            }
            developer.Add($"install.existing.hsmod={File.Exists(hsMod)};sha256={(File.Exists(hsMod) ? Sha256(hsMod) : "<none>")}");
            developer.Add($"install.existing.arena={File.Exists(arena)};sha256={(File.Exists(arena) ? Sha256(arena) : "<none>")}");
            developer.Add($"install.existing.universe={File.Exists(Path.Combine(plugins, "UniverseLib.Mono.dll"))}");
            if (!File.Exists(Path.Combine(plugins, "UniverseLib.Mono.dll")) && universeSource == null)
                throw new FileNotFoundException("目标目录缺少 UniverseLib.Mono.dll，且安装包未提供依赖");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(backupDir!);
            Directory.CreateDirectory(staged!);
            Log("[通过] 已确认炉石、盒子均已退出");
            Log("[通过] 炉石目录：" + root);
            Log("[通过] 盒子位置：" + discovery.BoxExecutablePath);
            developer.Add("source.plugin=" + source + ";sha256=" + Sha256(source));
            developer.Add("source.boxHook=" + versionDllSource + ";sha256=" + Sha256(versionDllSource));
            Log("[通过] 内置安装材料校验通过");
            developer.Add($"install.root={root}");
            developer.Add($"install.source={source}");

            trace.Step("install-loader");
            InstallBepInExRuntime(root, discovery.BepInExSourcePath!, discovery.DoorstopSourcePath!, discovery.DoorstopConfigSourcePath!, backupDir!, moved, Log);
            Log("[通过] 游戏插件加载器已安装");
            trace.Step("configure-hsmod");

            // A fresh Hearthstone directory has no generated HsMod.cfg yet.
            // The controller must be usable immediately after one-click
            // installation, so materialize the shipped template before the
            // controller writes its mode/source values. HsMod will continue
            // to update this normal config file on its first launch.
            var hsModTemplate = Path.Combine(root, "BepInEx", "config", "HsMod.cfg.template");
            var hsModConfig = Path.Combine(root, "BepInEx", "config", "HsMod.cfg");
            if (!File.Exists(hsModConfig) && File.Exists(hsModTemplate))
            {
                ReplaceWithBackup(hsModConfig, hsModTemplate, backupDir!, moved, Log);
                Log("[通过] 已从安装包模板创建 HsMod.cfg，可直接启动中控任务");
            }
            if (File.Exists(hsModConfig))
            {
                // HsMod keeps the effective first-start locale separately from
                // the user-facing Global locale.  A stale enUS init value can
                // leave the entire native settings page in English even when
                // "HsMod Language" already says zhCN.  Keep both values in
                // sync during installation so a clean install starts in the
                // requested Simplified Chinese UI.
                EnsureChineseLanguage(hsModConfig);
                EnsureSectionKey(hsModConfig, "自动脚本", "推荐来源", "盒子文件");
                // The shipped template is portable, but its historical
                // default points at the old Program Files installation. Keep
                // HsMod's match-log reader beside the actual installed game
                // so result/statistics parsing does not emit a stale-path
                // DirectoryNotFoundException after a clean install.
                NormalizeHsModLogPaths(hsModConfig, root);
                Log("[通过] HsMod 推荐来源已设置为盒子文件");
            }

            // HsMod remains byte-for-byte original. ArenaBoxBot provides the
            // virtual Arena mode through its own Harmony bridge; rewriting the
            // HsMod enum makes this vendor build exit during plugin startup.
            trace.Step("install-plugins-and-box-hook");
            // Plugin independence: never downgrade/replace a user-managed
            // HsMod.dll.  The controller only depends on the plugin and the
            // local HsMod bridge, so any compatible HsMod build is accepted;
            // the bundled copy is used only for a clean target.
            if (!File.Exists(hsMod))
            {
                var stagedHsMod = Path.Combine(staged!, "HsMod.dll");
                File.Copy(hsModSource!, stagedHsMod, true);
                ReplaceWithBackup(hsMod, stagedHsMod, backupDir!, moved, Log);
                Log("[信息] 目标目录没有 HsMod.dll，已使用安装包内原始 HsMod.dll");
            }
            else
            {
                Log("[信息] 检测到现有 HsMod.dll（用户版本），保留不替换");
            }
            var stagedArena = Path.Combine(staged!, "ArenaBoxBot.dll");
            File.Copy(source, stagedArena, true);
            if (!string.Equals(Sha256(source), Sha256(stagedArena), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("插件复制后的 SHA-256 不一致");
            var boxDirectory = Path.GetDirectoryName(discovery.BoxExecutablePath)!;
            var versionTarget = Path.Combine(boxDirectory, "version.dll");
            var rankBypassTarget = Path.Combine(boxDirectory, "RankBypass.ini");
            var rankBypassText = File.ReadAllText(rankBypassTemplate, Encoding.UTF8)
                .Replace("{OUTPUT_DIR}", Path.Combine(root, "BepInEx", "HsMod"), StringComparison.OrdinalIgnoreCase);
            // Normalize required keys instead of appending duplicate lines.
            // The native hook treats BlockProcesses as a process-name list;
            // even "0" is a non-empty list entry and can still activate the
            // block path. An empty value disables process blocking so the
            // stock HSAExternal.exe collector can start.
            rankBypassText = EnsureIniValue(rankBypassText, "EnableCollector", "1");
            // The bundled collector's existing custom callback capture carries
            // Arena draft/discard recommendations in hsang-recommend-call.log.
            // Keep this enabled for standalone in-game use after setup.
            rankBypassText = EnsureIniValue(rankBypassText, "CaptureCallback", "onUpdateArenaRecommend");
            rankBypassText = EnsureIniValue(rankBypassText, "EnableLog", "1");
            rankBypassText = EnsureIniValue(rankBypassText, "OutputDir", Path.Combine(root, "BepInEx", "HsMod"));
            rankBypassText = EnsureIniValue(rankBypassText, "BlockProcesses", string.Empty);
            var stagedRankBypass = Path.Combine(staged!, "RankBypass.ini");
            File.WriteAllText(stagedRankBypass, rankBypassText, new UTF8Encoding(false));
            var keepExistingVersion = File.Exists(versionTarget) && new FileInfo(versionTarget).Length >= 100 * 1024;
            var stagedVersion = Path.Combine(staged!, "version.dll");
            if (!keepExistingVersion)
            {
                File.Copy(versionDllSource!, stagedVersion, true);
                if (!string.Equals(Sha256(versionDllSource!), Sha256(stagedVersion), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("version.dll 复制后的 SHA-256 不一致");
            }

            ReplaceWithBackup(arena, stagedArena, backupDir!, moved, Log);
            if (keepExistingVersion) Log("[信息] 检测到盒子目录已有 version.dll（用户版本），保留不替换");
            else ReplaceWithBackup(versionTarget, stagedVersion, backupDir!, moved, Log);
            ReplaceWithBackup(rankBypassTarget, stagedRankBypass, backupDir!, moved, Log);
            Log("[通过] 盒子采集配置已生成：BlockProcesses 为空（不拦截盒子子进程），输出目录=" + Path.Combine(root, "BepInEx", "HsMod"));
            var universeTarget = Path.Combine(plugins, "UniverseLib.Mono.dll");
            if (universeSource != null &&
                (!File.Exists(universeTarget) ||
                 !string.Equals(Sha256(universeTarget), Sha256(universeSource), StringComparison.OrdinalIgnoreCase)))
            {
                var stagedUniverse = Path.Combine(staged!, "UniverseLib.Mono.dll");
                File.Copy(universeSource, stagedUniverse, true);
                ReplaceWithBackup(universeTarget, stagedUniverse, backupDir!, moved, Log);
            }
            trace.Step("cleanup-legacy");
            if (request.CleanupKnownLegacyFiles)
            {
                foreach (var extra in Directory.EnumerateFiles(plugins, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => !BundledPluginNames.Contains(Path.GetFileName(path))))
                    MoveToBackup(extra, backupDir!, moved, Log);
                foreach (var legacy in LegacyPluginNames.Select(n => Path.Combine(plugins, n)).Where(File.Exists))
                    MoveToBackup(legacy, backupDir!, moved, Log);
                var hsModRoot = Path.Combine(root, "BepInEx", "HsMod");
                foreach (var legacy in LegacyMarkerNames.Select(n => Path.Combine(hsModRoot, n)).Where(File.Exists))
                    MoveToBackup(legacy, backupDir!, moved, Log);
            }
            if (request.CleanupKnownLegacyFiles)
            {
                var stateDir = Path.Combine(root, "BepInEx", "HsMod");
                foreach (var legacy in LegacyStateNames.Select(n => Path.Combine(stateDir, n)).Where(File.Exists))
                    MoveToBackup(legacy, backupDir!, moved, Log);
                // A captured scoring session is a local cache, not proof of
                // the current Box login. Preserve it in the same rollback
                // backup while Game and Box are both verified closed, then
                // let the original capture publish its next native login.
                // Never inspect/edit credentials or remove real run ledgers.
                var oldScoringCaches = new[] { "boxai-direct-profile.dpapi", "boxai-direct-profile.invalid" }
                    .Select(n => Path.Combine(stateDir, n)).Where(File.Exists).ToArray();
                foreach (var cache in oldScoringCaches) MoveToBackup(cache, backupDir!, moved, Log);
                if (oldScoringCaches.Length > 0)
                    Log("[信息] 已备份旧盒子评分缓存；下次启动由盒子重新采集当前登录会话");
            }
            trace.Step("verify-installed-files");
            var nativeVersion = System.Reflection.AssemblyName.GetAssemblyName(hsMod).Version?.ToString() ?? "unknown";
            var manifest = new { installedAt = DateTimeOffset.UtcNow, hsModSha256 = Sha256(hsMod), arenaBoxBotSha256 = Sha256(arena), versionDllSha256 = Sha256(versionTarget), rankBypassIniSha256 = Sha256(rankBypassTarget), hsModVersion = nativeVersion, mode = "标准/狂野/休闲/酒馆战棋/竞技场", legacyCleaned = request.CleanupKnownLegacyFiles };
            developer.Add("installed.manifest=" + JsonSerializer.Serialize(manifest));
            File.WriteAllText(Path.Combine(root, "BepInEx", "HsMod", "native-arena-install.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            Log("[完成] 已安装原版 HsMod + ArenaBoxBot；竞技场由独立插件接管，关闭中控后仍可在炉石插件中运行");
            return Result(true, lines, trace, backupDir, root);
        }
        catch (Exception ex)
        {
            developer.Add("failure.stage=" + trace.Stage + ";hresult=0x" + ex.HResult.ToString("X8"));
            Log("[失败] " + ex.Message);
            developer.Add(ex.ToString());
            for (var i = moved.Count - 1; i >= 0; i--)
                try
                {
                    if (File.Exists(moved[i].Target)) DeleteFileForce(moved[i].Target);
                    else if (Directory.Exists(moved[i].Target)) DeleteDirectoryForce(moved[i].Target);
                    if (File.Exists(moved[i].Backup)) File.Move(moved[i].Backup, moved[i].Target);
                    else if (Directory.Exists(moved[i].Backup)) Directory.Move(moved[i].Backup, moved[i].Target);
                    Log("[恢复] " + moved[i].Target);
                }
                catch (Exception restore) { developer.Add("rollback.failure=" + restore); Log("[失败] 回滚 " + moved[i].Target + "：" + restore.Message); }
            developer.Add("rollback.completed;entries=" + moved.Count);
            return Result(false, lines, trace, backupDir, root);
        }
        finally { try { if (staged != null && Directory.Exists(staged)) Directory.Delete(staged, true); } catch (Exception ex) { developer.Add("staging.cleanup-failure=" + ex); } }
    }

    private static string ExpandPath(string value)
    {
        var cleaned = (value ?? string.Empty).Trim().Trim('\'', '`', '“', '”', '‘', '’', '\uFEFF', '\u200B', '\u200E', '\u200F').Trim();
        // Explorer, browser based file pickers, and a few launcher dialogs
        // can return a file URI instead of a Windows path.  Treat it as a
        // path before probing; otherwise File.Exists silently returns false
        // and the UI used to report a valid installation as missing.
        if (cleaned.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(cleaned, UriKind.Absolute, out var uri) &&
            uri.IsFile && !string.IsNullOrWhiteSpace(uri.LocalPath))
            cleaned = uri.LocalPath;
        // Some launcher dialogs copy a complete command line rather than a
        // pure path.  Keep only the first quoted token or the token ending in
        // .exe; otherwise Path.GetFullPath treats the arguments as part of a
        // filename and the valid game is reported as missing.
        if (cleaned.Length > 0 && cleaned[0] == '"')
        {
            var endQuote = cleaned.IndexOf('"', 1);
            if (endQuote > 1) cleaned = cleaned[1..endQuote];
        }
        else
        {
            var exeMatch = System.Text.RegularExpressions.Regex.Match(
                cleaned, "\\.exe(?=\\s|['\\\"]|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (exeMatch.Success && exeMatch.Index + exeMatch.Length < cleaned.Length)
                cleaned = cleaned[..(exeMatch.Index + exeMatch.Length)];
        }
        // Re-run URI decoding after extracting a quoted token.  This covers
        // values such as `"file:///C:/Program%20Files/Hearthstone"` copied
        // from a browser or a launcher dialog.
        if (cleaned.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(cleaned, UriKind.Absolute, out var tokenUri) &&
            tokenUri.IsFile && !string.IsNullOrWhiteSpace(tokenUri.LocalPath))
            cleaned = tokenUri.LocalPath;
        return Environment.ExpandEnvironmentVariables(cleaned.Trim().Trim('"'));
    }

    private static string? NormalizeCandidate(string? candidate, ICollection<string>? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var expanded = ExpandPath(candidate);
        if (expanded.Length == 0) return null;
        try
        {
            if (File.Exists(expanded)) return Path.GetDirectoryName(Path.GetFullPath(expanded));
            // Preserve D:\ and UNC share roots; trimming turns D:\ into the
            // drive-relative D: and resolves against an unrelated working directory.
            if (Directory.Exists(expanded)) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) { diagnostics?.Add("game.candidate.normalize-error=" + ex); return null; }
    }

    private static string? ResolveGameRoot(string? input, ICollection<string>? diagnostics = null, bool allowDataOnly = false)
        => ResolveGameRoot(new[] { input }, diagnostics, allowDataOnly);

    private static string? ResolveGameRoot(IEnumerable<string?> candidates, ICollection<string>? diagnostics = null, bool allowDataOnly = false)
    {
        foreach (var raw in candidates)
        {
            diagnostics?.Add("game.candidate.raw=" + raw);
            diagnostics?.Add("game.candidate.rawJson=" + JsonSerializer.Serialize(raw));
            var candidate = NormalizeCandidate(raw, diagnostics);
            if (candidate == null) continue;
            diagnostics?.Add($"game.candidate.normalized={candidate}");
            diagnostics?.Add("game.candidate.probe=" + DescribeGameRootProbe(candidate));
            if (IsGameRoot(candidate) || (allowDataOnly && IsDataOnlyGameRoot(candidate)))
            {
                diagnostics?.Add("game.candidate.result=exact-root;" + DescribeGameRootProbe(candidate) + ";allowDataOnly=" + allowDataOnly);
                return candidate;
            }
            if (!Directory.Exists(candidate))
            {
                diagnostics?.Add("game.candidate.result=not-a-game-root;directory-missing-or-inaccessible;probe=" + ProbeEntry(candidate, true));
                // Do not turn a nonexistent child or typo into its valid parent.
                continue;
            }
            var dataName = Path.GetFileName(candidate);
            if (dataName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(candidate);
                if (parent != null && (IsGameRoot(parent) || (allowDataOnly && IsDataOnlyGameRoot(parent))))
                {
                    diagnostics?.Add("game.candidate.result=parent-of-data;" + DescribeGameRootProbe(parent));
                    return parent;
                }
            }
            // Folder pickers and Explorer often return a child of the actual
            // game directory (for example Hearthstone_Data\\Managed). Walk
            // ancestors before searching downward so a valid installation is
            // never rejected merely because the user selected a subfolder.
            var ancestor = candidate;
            for (var depth = 0; depth < 8; depth++)
            {
                ancestor = Path.GetDirectoryName(ancestor);
                if (string.IsNullOrWhiteSpace(ancestor)) break;
                diagnostics?.Add($"game.ancestor[{depth + 1}]={ancestor}");
                diagnostics?.Add($"game.ancestor[{depth + 1}].probe=" + DescribeGameRootProbe(ancestor));
                if (IsGameRoot(ancestor) || (allowDataOnly && IsDataOnlyGameRoot(ancestor)))
                {
                    diagnostics?.Add("game.candidate.result=ancestor-root:" + ancestor + ";" + DescribeGameRootProbe(ancestor));
                    return ancestor;
                }
            }
            if (!IsGameRoot(candidate) && !(allowDataOnly && IsDataOnlyGameRoot(candidate)))
            {
                var expectedExe = Path.Combine(candidate, "Hearthstone.exe");
                diagnostics?.Add($"game.expectedExe={expectedExe};directoryExists={Directory.Exists(candidate)};probe={DescribeGameRootProbe(candidate)}");
                try
                {
                    var nearby = Directory.Exists(candidate)
                        ? Directory.EnumerateFiles(candidate, "*Hearthstone*", SearchOption.TopDirectoryOnly).Take(8).ToArray()
                        : Array.Empty<string>();
                    diagnostics?.Add("game.nearby=" + (nearby.Length == 0 ? "<none>" : string.Join("|", nearby)));
                }
                catch (Exception ex) { diagnostics?.Add("game.nearby-error=" + ex.GetType().Name + ":" + ex.Message); }
            }
            // Some launchers expose a versioned subdirectory. Probe only two
            // levels below the requested directory to avoid scanning an entire
            // drive while still handling the common nested layout.
            var found = new List<string>();
            var scanCount = 0;
            var queue = new Queue<(string Directory, int Depth)>();
            queue.Enqueue((candidate, 0));
            while (queue.Count > 0 && scanCount < 256)
            {
                var item = queue.Dequeue();
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(item.Directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (++scanCount > 256) break;
                        diagnostics?.Add("game.scan=" + dir + ";probe=" + DescribeGameRootProbe(dir));
                        if (IsGameRoot(dir) || (allowDataOnly && IsDataOnlyGameRoot(dir))) found.Add(dir);
                        else if (item.Depth < 1 && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
                            queue.Enqueue((dir, item.Depth + 1));
                    }
                }
                catch (Exception ex) { diagnostics?.Add("game.candidate.scan-error=" + item.Directory + ";" + ex); }
                // A blocked child never prevents scanning the remaining siblings.
            }
            if (scanCount >= 256) diagnostics?.Add("game.scan.limit=256");
            if (found.Count == 1 && scanCount < 256)
            { diagnostics?.Add("game.candidate.result=nested-root:" + found[0]); return found[0]; }
            if (found.Count > 1)
            { diagnostics?.Add("game.candidate.result=ambiguous;roots=" + string.Join("|", found)); return null; }
            diagnostics?.Add("game.candidate.result=not-a-game-root");
        }
        return null;
    }

    private static string ExplainGamePathFailure(string? input, IEnumerable<string> diagnostics)
    {
        var evidence = string.Join("\n", diagnostics);
        if (evidence.Contains("result=ambiguous", StringComparison.Ordinal))
            return "所选位置包含多个炉石安装目录，请直接选择要安装的那一份 Hearthstone.exe 所在目录。";
        if (evidence.Contains("UnauthorizedAccessException", StringComparison.Ordinal))
            return "无法读取所选炉石位置，访问被拒绝。请检查该目录的访问权限；开发者日志记录了被拒绝的具体路径。";
        if (evidence.Contains("normalize-error", StringComparison.Ordinal))
            return "炉石路径格式无效，请粘贴 Windows 文件夹路径或 Hearthstone.exe 的完整路径。";
        if (string.IsNullOrWhiteSpace(input)) return "未自动找到炉石，请指定 Hearthstone.exe 所在目录。";
        var path = NormalizeCandidate(input);
        if (path == null || !Directory.Exists(path))
            return "所选炉石位置不存在或当前不可访问：" + input + "。请重新选择实际游戏文件夹；具体访问错误见开发者日志。";
        var probe = DescribeGameRootProbe(path);
        return "已读取所选目录但未识别为炉石安装：" + path + "。检查结果：" + probe + "。请选择包含 Hearthstone.exe、官方启动器或 Hearthstone_Data 的目录。";
    }

    private static string ExplainBoxPathFailure(string? input, IEnumerable<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "未自动找到炉石盒子，请指定 HSAng.exe 或包含它的盒子目录。";
        var path = NormalizeCandidate(input);
        if (path == null || !Directory.Exists(path))
            return "所选盒子位置不存在或当前不可访问：" + input + "。请重新选择 HSAng.exe 或盒子目录。";
        var evidence = diagnostics.LastOrDefault(x => x.StartsWith("box.candidate=", StringComparison.Ordinal));
        return "已读取所选位置但未找到 HSAng.exe：" + path + "。" +
               (string.IsNullOrWhiteSpace(evidence) ? "请直接选择盒子目录或 HSAng.exe。" : "探测证据已写入开发者日志。" );
    }

    private static bool IsGameRoot(string p)
    {
        // A normal installation has Hearthstone.exe.  During a launcher
        // repair/update the executable can be temporarily absent while the
        // launcher and Unity data are already present.  Accept that stable
        // layout as well so installation can repair the loader without
        // forcing the user to wait for the launcher to finish patching.
        if (PathExistsOrInaccessible(Path.Combine(p, "Hearthstone.exe"), expectDirectory: false)) return true;
        var launcher = PathExistsOrInaccessible(Path.Combine(p, "Hearthstone Beta Launcher.exe"), expectDirectory: false);
        var data = Directory.Exists(Path.Combine(p, "Hearthstone_Data"));
        var unity = PathExistsOrInaccessible(Path.Combine(p, "UnityPlayer.dll"), expectDirectory: false);
        return launcher || (data && unity);
    }

    private static bool PathExistsOrInaccessible(string path, bool expectDirectory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (((attributes & FileAttributes.Directory) != 0) == expectDirectory);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException)
        {
            // File.Exists hides access-denied as false.  Treat an inaccessible
            // named target as a candidate so the installer reports the real
            // ACL/lock failure instead of claiming that Hearthstone is absent.
            return true;
        }
        catch (IOException) { return false; }
    }

    private static IEnumerable<string?> RegistryGameInstallPaths(ICollection<string>? diagnostics)
    {
        var result = new List<string?>();
        var locations = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Blizzard Entertainment\Hearthstone"),
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Blizzard Entertainment\Battle.net\Launch Points\Hearthstone"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Blizzard Entertainment\Hearthstone"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Blizzard Entertainment\Hearthstone"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\WOW6432Node\Blizzard Entertainment\Hearthstone")
        };
        foreach (var location in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Item1, location.Item2);
                using var key = baseKey.OpenSubKey(location.Item3, writable: false);
                if (key == null)
                {
                    diagnostics?.Add("game.registry.miss=" + location.Item1 + ":" + location.Item2 + ":" + location.Item3);
                    continue;
                }
                foreach (var name in new[] { "InstallPath", "InstallDir", "Path", "GamePath" })
                {
                    var value = key.GetValue(name) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        diagnostics?.Add("game.registry.hit=" + location.Item1 + ":" + location.Item2 + ":" + location.Item3 + ";valueName=" + name + ";value=" + value);
                        result.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics?.Add("game.registry.error=" + location.Item1 + ":" + location.Item2 + ":" + location.Item3 + ";" + ex.GetType().Name + ":" + ex.Message);
            }
        }
        return result;
    }

    private static bool IsDataOnlyGameRoot(string p)
    {
        // During a Battle.net repair, Hearthstone.exe/UnityPlayer.dll can be
        // replaced temporarily.  An explicitly selected directory is still a
        // safe install target when its Unity data directory contains the
        // stable metadata files written by the game itself.
        var data = Path.Combine(p, "Hearthstone_Data");
        if (!Directory.Exists(data)) return false;
        return File.Exists(Path.Combine(data, "globalgamemanagers")) ||
               File.Exists(Path.Combine(data, "resources.assets")) ||
               File.Exists(Path.Combine(data, "app.info"));
    }

    private static string? RunningGameInstallPath(ICollection<string>? diagnostics)
    {
        foreach (var process in FindHearthstoneProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                diagnostics?.Add("game.running-process=" + process.ProcessName + ";pid=" + process.Id + ";path=" + (path ?? "<unavailable>"));
                if (!string.IsNullOrWhiteSpace(path)) return Path.GetDirectoryName(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                diagnostics?.Add("game.running-process-error=" + process.ProcessName + ";pid=" + process.Id + ";" + ex.GetType().Name + ":" + ex.Message);
            }
            finally { process.Dispose(); }
        }
        return null;
    }

    private static string DescribeGameRootProbe(string p)
    {
        try
        {
            var exe = Path.Combine(p, "Hearthstone.exe");
            var launcher = Path.Combine(p, "Hearthstone Beta Launcher.exe");
            var data = Path.Combine(p, "Hearthstone_Data");
            var unity = Path.Combine(p, "UnityPlayer.dll");
            var dataMarkers = Path.Combine(data, "globalgamemanagers");
            var resources = Path.Combine(data, "resources.assets");
            return $"exe={ProbeEntry(exe, false)};launcher={ProbeEntry(launcher, false)};data={ProbeEntry(data, true)};unity={ProbeEntry(unity, false)};globalgamemanagers={ProbeEntry(dataMarkers, false)};resources.assets={ProbeEntry(resources, false)}";
        }
        catch (Exception ex) { return "probe-error=" + ex.GetType().Name + ":" + ex.Message; }
    }

    private static string ProbeEntry(string path, bool directory)
    {
        try
        {
            // Exists() swallows access errors. Attributes reports the actual
            // Windows exception, distinguishing absent files from denied access.
            var attributes = File.GetAttributes(path);
            if (((attributes & FileAttributes.Directory) != 0) != directory) return "false;wrong-kind;attributes=" + attributes;
            if (directory) return "true;attributes=" + attributes;
            var length = new FileInfo(path).Length;
            return "true;length=" + length + ";attributes=" + attributes;
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            var parent = Path.GetDirectoryName(path);
            return "false;parent=" + (parent != null && Directory.Exists(parent));
        }
        catch (Exception ex)
        {
            // File.Exists/Directory.Exists intentionally hide access errors;
            // querying attributes exposes them in the developer log without
            // turning a diagnostic probe into a new installation failure.
            return "error=" + ex.GetType().Name + ";hresult=0x" + ex.HResult.ToString("X8") + ";path=" + path + ";message=" + ex.Message;
        }
    }
    private static string? FirstFileWithDiagnostics(IEnumerable<string?> paths, string label, ICollection<string> diagnostics)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = ExpandPath(raw);
            try
            {
                var full = Path.GetFullPath(path);
                diagnostics.Add($"{label}.candidate={full};exists={File.Exists(full)};probe={ProbeEntry(full, false)}");
                if (File.Exists(full)) return full;
            }
            catch (Exception ex) { diagnostics.Add($"{label}.candidate-error={raw};{ex.GetType().Name}:{ex.Message}"); }
        }
        return null;
    }

    private static string? FirstDirectoryWithDiagnostics(IEnumerable<string?> paths, string label, ICollection<string> diagnostics)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = ExpandPath(raw);
            try
            {
                var full = Path.GetFullPath(path);
                diagnostics.Add($"{label}.candidate={full};exists={Directory.Exists(full)};probe={ProbeEntry(full, true)}");
                if (Directory.Exists(full)) return full;
            }
            catch (Exception ex) { diagnostics.Add($"{label}.candidate-error={raw};{ex.GetType().Name}:{ex.Message}"); }
        }
        return null;
    }

    private static string? FirstBoxExecutableWithDiagnostics(IEnumerable<string?> paths, ICollection<string> diagnostics)
    {
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var expanded = ExpandPath(raw);
            try
            {
                var full = Path.GetFullPath(expanded);
                if (File.Exists(full))
                {
                    diagnostics.Add($"box.candidate={full};exists=true;kind=file");
                    if (string.Equals(Path.GetFileName(full), "HSAng.exe", StringComparison.OrdinalIgnoreCase))
                        return full;
                    diagnostics.Add("box.candidate.result=ignored-non-HSAng-exe");
                    continue;
                }
                if (Directory.Exists(full))
                {
                    var executable = Path.Combine(full, "HSAng.exe");
                    diagnostics.Add($"box.candidate={full};exists=true;kind=directory;expectedExe={executable};fileExists={File.Exists(executable)}");
                    if (File.Exists(executable)) return executable;
                    // A few Box distributions put the launcher below a
                    // versioned child directory.  Only search a bounded
                    // depth and only accept the exact HSAng.exe filename so
                    // an arbitrary executable can never become the install
                    // target.  If more than one is found, keep the result
                    // ambiguous and make the user choose explicitly.
                    var nested = new List<string>();
                    try
                    {
                        var queue = new Queue<(string Directory, int Depth)>();
                        queue.Enqueue((full, 0));
                        var scanned = 0;
                        while (queue.Count > 0 && scanned < 128)
                        {
                            var item = queue.Dequeue();
                            foreach (var child in Directory.EnumerateDirectories(item.Directory, "*", SearchOption.TopDirectoryOnly))
                            {
                                if (++scanned > 128) break;
                                var childExe = Path.Combine(child, "HSAng.exe");
                                diagnostics.Add($"box.scan={child};expectedExe={childExe};fileExists={File.Exists(childExe)}");
                                if (File.Exists(childExe)) nested.Add(childExe);
                                if (item.Depth < 1 && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                                    queue.Enqueue((child, item.Depth + 1));
                            }
                        }
                        if (nested.Count == 1) { diagnostics.Add("box.candidate.result=nested-root:" + nested[0]); return nested[0]; }
                        if (nested.Count > 1) diagnostics.Add("box.candidate.result=ambiguous;executables=" + string.Join("|", nested));
                    }
                    catch (Exception ex) { diagnostics.Add("box.candidate.scan-error=" + full + ";" + ex.GetType().Name + ":" + ex.Message); }
                    continue;
                }
                diagnostics.Add($"box.candidate={full};exists=false;kind=missing");
            }
            catch (Exception ex) { diagnostics.Add($"box.candidate-error={raw};{ex.GetType().Name}:{ex.Message}"); }
        }
        return null;
    }
    private static string Sha256(string path) { using var h = SHA256.Create(); using var s = File.OpenRead(path); return Convert.ToHexString(h.ComputeHash(s)); }

    private (bool Success, string Message) VerifyPackagePayloads()
    {
        var manifestPath = Path.Combine(_packageDirectory, "SHA256SUMS.txt");
        if (!File.Exists(manifestPath))
            return (false, "安装包缺少 SHA256SUMS.txt，拒绝使用未验证的插件源");

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var raw in File.ReadLines(manifestPath, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var separator = line.IndexOf("  ", StringComparison.Ordinal);
                if (separator != 64 || line.Length <= separator + 2)
                    return (false, "安装包 SHA256SUMS.txt 格式错误：" + raw);
                var hash = line[..separator];
                var name = line[(separator + 2)..].Trim();
                if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)) || name.Length == 0)
                    return (false, "安装包 SHA256SUMS.txt 包含无效条目：" + raw);
                expected[name] = hash;
            }
        }
        catch (Exception ex)
        {
            return (false, "读取安装包 SHA256SUMS.txt 失败：" + ex.GetType().Name + "：" + ex.Message);
        }

        var required = new[] { "ArenaBoxBot.dll" };
        foreach (var name in required)
        {
            if (!expected.TryGetValue(name, out var expectedHash))
                return (false, "安装包 SHA256SUMS.txt 缺少 " + name + " 的校验项");
            var path = Path.Combine(_packageDirectory, name);
            if (!File.Exists(path))
                return (false, "安装包缺少清单声明的文件：" + name);
            try
            {
                var actual = Sha256(path);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return (false, name + " 的 SHA-256 与安装包清单不一致（期望 " + expectedHash + "，实际 " + actual + "）");
            }
            catch (Exception ex)
            {
                return (false, "读取 " + name + " 失败，可能被 Windows 安全中心隔离：" + ex.GetType().Name + "：" + ex.Message);
            }
        }
        return (true, "");
    }

    private static List<Process> FindHearthstoneProcesses(string? gameDirectory = null)
    {
        var result = new List<Process>();
        var expectedRoot = string.IsNullOrWhiteSpace(gameDirectory)
            ? null
            : Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var process in Process.GetProcessesByName("Hearthstone"))
        {
            try
            {
                if (process.HasExited) { process.Dispose(); continue; }
                var path = TryGetProcessPath(process);
                if (expectedRoot == null || string.IsNullOrWhiteSpace(path))
                {
                    // Without a target root, or without readable process
                    // metadata, fail closed and treat the process as live.
                    result.Add(process);
                    continue;
                }
                var fullPath = Path.GetFullPath(path);
                var processRoot = Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(processRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
                    result.Add(process);
                else
                    process.Dispose();
            }
            catch
            {
                // An inaccessible process is safer to treat as live than to
                // replace loader files while it may still hold target files
                // open.  This is deliberately conservative.
                result.Add(process);
            }
        }
        return result;
    }

    private static List<Process> FindBoxProcesses(string? boxDirectory)
    {
        var result = new List<Process>();
        var expected = string.IsNullOrWhiteSpace(boxDirectory) ? null : Path.GetFullPath(boxDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var name in new[] { "HSAng", "HSAExternal" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.HasExited) continue;
                    var path = TryGetProcessPath(process);
                    if (expected == null || string.IsNullOrWhiteSpace(path) ||
                        string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                        result.Add(process);
                    else
                        process.Dispose();
                }
                catch
                {
                    // If process metadata is inaccessible, keep it as a blocker
                    // rather than risking a DLL replacement while it is alive.
                    result.Add(process);
                }
            }
        }
        return result;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch { }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        catch { return null; }
        finally { CloseHandle(handle); }
    }

    private static string DescribeProcess(Process process)
    {
        try
        {
            var title = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? "无窗口标题" : process.MainWindowTitle;
            return process.ProcessName + "(PID " + process.Id + "，" + title + ")";
        }
        catch { return "未知盒子进程"; }
    }
    private static void ReplaceWithBackup(string target, string staged, string backupDir, List<(string, string)> moved, Action<string> log)
    {
        if (File.Exists(target)) MoveToBackup(target, backupDir, moved, log);
        var pending = target + ".arena-pending-" + Guid.NewGuid().ToString("N");
        try { ClearReadOnly(target); File.Copy(staged, pending, false); File.Move(pending, target, true); log("[通过] 已安装 " + Path.GetFileName(target)); }
        finally { try { if (File.Exists(pending)) File.Delete(pending); } catch { } }
    }
    private static void MoveToBackup(string target, string backupDir, List<(string, string)> moved, Action<string> log)
    {
        var backup = Path.Combine(backupDir, Path.GetFileName(target));
        if (File.Exists(backup) || Directory.Exists(backup)) backup += "." + moved.Count;
        if (File.Exists(target)) { ClearReadOnly(target); File.Move(target, backup); }
        else if (Directory.Exists(target)) Directory.Move(target, backup);
        else return;
        moved.Add((target, backup));
        log("[备份] " + target + " -> " + backup);
    }

    private static void InstallBepInExRuntime(string root, string sourceDirectory, string doorstopSource, string doorstopConfigSource, string backupDir, List<(string, string)> moved, Action<string> log)
    {
        var targetDirectory = Path.Combine(root, "BepInEx");
        foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, source);
            // The package deliberately contains no plugins or HsMod state;
            // keep this guard so a malformed package cannot overwrite those
            // directories through the loader-copy phase.
            if (relative.StartsWith("plugins" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("HsMod" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(targetDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyIfDifferent(target, source, backupDir, moved, log);
        }
        CopyIfDifferent(Path.Combine(root, "winhttp.dll"), doorstopSource, backupDir, moved, log);
        CopyIfDifferent(Path.Combine(root, "doorstop_config.ini"), doorstopConfigSource, backupDir, moved, log);
    }

    private static void CopyIfDifferent(string target, string source, string backupDir, List<(string, string)> moved, Action<string> log)
    {
        if (File.Exists(target) && string.Equals(Sha256(target), Sha256(source), StringComparison.OrdinalIgnoreCase)) return;
        ReplaceWithBackup(target, source, backupDir, moved, log);
    }

    private static void EnsureSectionKey(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
        var header = "[" + section + "]";
        var sectionIndex = lines.FindIndex(x => string.Equals(x.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add(header);
            sectionIndex = lines.Count - 1;
        }
        var end = lines.Count;
        for (var i = sectionIndex + 1; i < lines.Count; i++)
            if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal)) { end = i; break; }
        var pattern = @"^\s*" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=";
        var keyIndex = -1;
        for (var i = sectionIndex + 1; i < end; i++)
            if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], pattern)) { keyIndex = i; break; }
        if (keyIndex >= 0) lines[keyIndex] = key + " = " + value;
        else lines.Insert(end, key + " = " + value);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    /// <summary>
    /// HsMod translates the Global section and its language key after the
    /// first Chinese startup.  Update either spelling in place so repeated
    /// controller starts do not append a second English section.
    /// </summary>
    public static void EnsureChineseLanguage(string path)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
        EnsureSectionKeyVariants(lines,
            new[] { "Global", "全局" },
            new[] { "HsMod Language", "HsMod语言" },
            "Global", "HsMod Language", "zhCN");
        EnsureSectionKeyVariants(lines,
            new[] { "HsMod" },
            new[] { "HsMod.Init.Language" },
            "HsMod", "HsMod.Init.Language", "zhCN");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static void EnsureSectionKeyVariants(
        List<string> lines,
        string[] sections,
        string[] keys,
        string fallbackSection,
        string fallbackKey,
        string value)
    {
        var sectionIndexes = Enumerable.Range(0, lines.Count)
            .Where(i => lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal) &&
                        lines[i].Trim().TrimStart('[').TrimEnd(']') is var section &&
                        sections.Any(candidate => string.Equals(candidate, section, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var sectionIndex = -1;
        var keyIndex = -1;
        foreach (var candidateSection in sectionIndexes)
        {
            var end = FindSectionEnd(lines, candidateSection);
            for (var i = candidateSection + 1; i < end; i++)
            {
                var left = lines[i].Split('=', 2)[0].Trim();
                if (keys.Any(key => string.Equals(key, left, StringComparison.OrdinalIgnoreCase)))
                {
                    sectionIndex = candidateSection;
                    keyIndex = i;
                    break;
                }
            }
            if (keyIndex >= 0) break;
        }
        if (sectionIndex < 0)
        {
            sectionIndex = sectionIndexes.FirstOrDefault(-1);
            if (sectionIndex < 0)
            {
                if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
                lines.Add("[" + fallbackSection + "]");
                sectionIndex = lines.Count - 1;
            }
        }
        var sectionEnd = FindSectionEnd(lines, sectionIndex);
        if (keyIndex < 0)
        {
            lines.Insert(sectionEnd, fallbackKey + " = " + value);
            return;
        }
        var existingKey = lines[keyIndex].Split('=', 2)[0].Trim();
        lines[keyIndex] = existingKey + " = " + value;
        // Remove duplicate language keys within the same section, retaining
        // the first one that HsMod will read.
        sectionEnd = FindSectionEnd(lines, sectionIndex);
        for (var i = sectionEnd - 1; i > sectionIndex; i--)
        {
            var left = lines[i].Split('=', 2)[0].Trim();
            if (i != keyIndex && keys.Any(key => string.Equals(key, left, StringComparison.OrdinalIgnoreCase)))
                lines.RemoveAt(i);
        }
    }

    private static int FindSectionEnd(List<string> lines, int sectionIndex)
    {
        for (var i = sectionIndex + 1; i < lines.Count; i++)
            if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal)) return i;
        return lines.Count;
    }

    private static void NormalizeHsModLogPaths(string path, string hearthstoneRoot)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        var matchLog = Path.Combine(hearthstoneRoot, "BepInEx", "HsMod", "Instances", "auto-1", "match.log");
        var pattern = new System.Text.RegularExpressions.Regex(@"^\s*Match Log\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!pattern.IsMatch(lines[i])) continue;
            if (!replaced)
            {
                lines[i] = "Match Log = " + matchLog;
                replaced = true;
            }
            else
            {
                lines.RemoveAt(i--);
            }
        }
        if (!replaced) lines.Add("Match Log = " + matchLog);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    private static string EnsureIniValue(string text, string key, string value)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var pattern = new System.Text.RegularExpressions.Regex(@"^\s*" + System.Text.RegularExpressions.Regex.Escape(key) + @"\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var first = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!pattern.IsMatch(lines[i])) continue;
            if (first < 0) { first = i; lines[i] = key + "=" + value; }
            else { lines.RemoveAt(i); i--; }
        }
        if (first < 0) lines.Add(key + "=" + value);
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

}
