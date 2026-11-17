using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArenaControl.Contracts;

using ArenaController.Ui.Design;

namespace ArenaController;

internal sealed partial class MainForm : Form
{

    private static bool IsKnownDefaultGamePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return new[]
            {
                Path.GetFullPath(@"D:\Hearthstone").TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(@"C:\Program Files (x86)\Hearthstone").TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(@"C:\Program Files\Hearthstone").TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(@"E:\Hearthstone").TrimEnd(Path.DirectorySeparatorChar)
            }.Any(candidate => string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }


    private static bool IsKnownDefaultBoxPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            var defaults = new[]
            {
                Path.GetFullPath(@"D:\炉石传说盒子\HSAng.exe"),
                Path.GetFullPath(@"C:\炉石传说盒子\HSAng.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "炉石传说盒子", "HSAng.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "炉石传说盒子", "HSAng.exe")
            };
            return defaults.Any(candidate => string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }


    private void InstallPlugin()
    {
        var report = new List<string>();
        try
        {
            if (_running || _starting)
            {
                report.Add("[失败] 当前任务正在运行，请先停止并等待炉石退出。");
                ShowInstallReport(report);
                return;
            }

            // Install always consumes the files beside hsmodPlus.exe.
            // This keeps the package reproducible and prevents stale
            // development builds from being installed accidentally.
            var source = Path.Combine(AppContext.BaseDirectory, "ArenaBoxBot.dll");
            if (!File.Exists(source)) throw new FileNotFoundException("安装包缺少内置 ArenaBoxBot.dll", source);
            var service = new NativeInstallService();
            var result = service.Install(new NativeInstallRequest
            {
                HearthstoneDirectory = _settings.HearthstoneDirectory,
                BoxExecutablePath = _settings.BoxExecutablePath,
                PluginSourcePath = source,
                CleanupKnownLegacyFiles = true
            }, null);
            if (!string.IsNullOrWhiteSpace(result.ResolvedHearthstoneDirectory) &&
                !string.Equals(_settings.HearthstoneDirectory, result.ResolvedHearthstoneDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _settings.HearthstoneDirectory = result.ResolvedHearthstoneDirectory!;
                DerivePathsFromHearthstoneRoot(_settings.HearthstoneDirectory);
                RefreshPathBoxes();
                Append("已将炉石位置规范化为实际安装目录：" + _settings.HearthstoneDirectory);
            }
            if (!result.Success)
            {
                ShowInstallReport(result);
                return;
            }

            report.AddRange(result.Lines.Where(line => !report.Contains(line, StringComparer.Ordinal)));
            DerivePathsFromHearthstoneRoot(_settings.HearthstoneDirectory);
            EnsureSectionKey(_settings.HsModConfigPath, "自动脚本", "推荐来源", "盒子文件");
            report.Add(VerifySectionKey(_settings.HsModConfigPath, "自动脚本", "推荐来源", "盒子文件") ? "[通过] HsMod 推荐来源 = 盒子文件" : "[失败] HsMod 推荐来源写入校验失败");
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "本轮最多对局数", _settings.MaxGamesPerRun.ToString());
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "竞技场模式", _settings.ArenaMode);
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "门票不足自动停止", _settings.StopOnTicketShortage ? "true" : "false");
            EnsureSectionKey(_settings.ArenaConfigPath, "竞技场脚本", "金币不足自动停止", _settings.StopOnGoldShortage ? "true" : "false");
            report.Add("[通过] 已写入当前中控运行配置；以后可关闭中控，插件仍由 HsMod 原生模式/F12 控制。");
            SaveSettings();
        }
        catch (Exception ex)
        {
            report.Add("[失败] " + ex.GetType().Name + "：" + ex.Message);
            var diagnosticPath = NativeInstallService.WriteControllerDeveloperLog("InstallPlugin", ex);
            ShowInstallReport(new NativeInstallResult
            {
                Success = false,
                Lines = report,
                DeveloperLines = new[] { ex.ToString() },
                DeveloperLogPath = diagnosticPath
            });
            return;
        }
        ShowInstallReport(report);
    }


    private void RestoreOriginal()
    {
        var result = new NativeInstallService().RestoreOriginal(_settings.HearthstoneDirectory, _settings.BoxExecutablePath, Path.Combine(AppContext.BaseDirectory, "HsMod.dll"));
        ShowInstallReport(result);
    }


    private void ShowInstallReport(NativeInstallResult result)
    {
        var lines = result.Lines.ToList();
        if (!result.Success)
        {
            if (!string.IsNullOrWhiteSpace(result.DeveloperLogPath))
                lines.Add("开发者诊断日志：" + result.DeveloperLogPath);
            if (!string.IsNullOrWhiteSpace(result.UserLogPath))
                lines.Add("用户操作日志：" + result.UserLogPath);
            if (!string.IsNullOrWhiteSpace(result.OperationId))
                lines.Add("操作编号：" + result.OperationId + (string.IsNullOrWhiteSpace(result.FailureStage) ? "" : "；失败阶段：" + result.FailureStage));
            if (result.DeveloperLines.Count > 0)
            {
                lines.Add("");
                lines.Add("===== 开发者诊断（仅失败时显示） =====");
                lines.AddRange(result.DeveloperLines);
            }
        }
        ShowInstallReport(lines);
    }


    private void ShowInstallReport(IEnumerable<string> report)
    {
        var lines = report.ToArray();
        foreach (var line in lines) Append(line);

        // A MessageBox cannot scroll long paths and dependency diagnostics;
        // on a narrow display it silently clips the useful part of the
        // report.  Use a resizable report window with an explicit scrollable
        // text area so every result remains inspectable.
        using var dialog = new Form
        {
            Text = "插件安装/修复报告",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(620, 360),
            Size = new Size(760, 520),
            Font = new Font("Microsoft YaHei UI", 9F),
            ShowInTaskbar = false
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var output = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9F),
            Text = string.Join(Environment.NewLine, lines)
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var ok = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(12, 3, 12, 3) };
        buttons.Controls.Add(ok);
        layout.Controls.Add(output, 0, 0); layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout); dialog.AcceptButton = ok; dialog.CancelButton = ok;
        dialog.ShowDialog(this);
    }


    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }


    private static void EnsureSectionKey(string path, string section, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8).ToList() : new List<string>();
        var header = "[" + section + "]";
        var sectionIndex = lines.FindIndex(x => string.Equals(x.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0) { if (lines.Count > 0 && lines[^1].Length != 0) lines.Add(""); lines.Add(header); sectionIndex = lines.Count - 1; }
        var end = lines.Count;
        for (var i = sectionIndex + 1; i < lines.Count; i++) if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal)) { end = i; break; }
        var keyIndex = -1;
        for (var i = sectionIndex + 1; i < end; i++) if (Regex.IsMatch(lines[i], @"^\s*" + Regex.Escape(key) + @"\s*=")) { keyIndex = i; break; }
        if (keyIndex >= 0) lines[keyIndex] = key + " = " + value; else lines.Insert(end, key + " = " + value);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }


    private static bool VerifySectionKey(string path, string section, string key, string expected)
    {
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var header = "[" + section + "]"; var start = Array.FindIndex(lines, x => string.Equals(x.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return false;
        for (var i = start + 1; i < lines.Length && !lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal); i++)
            if (Regex.Match(lines[i], @"^\s*" + Regex.Escape(key) + @"\s*=\s*(?<v>.*)$") is Match match && match.Success)
                return string.Equals(match.Groups["v"].Value.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        return false;
    }


    private static void EnsureSectionKeyIfMissing(string path, string section, string key, string value)
    {
        if (!HasSectionKey(path, section, key)) EnsureSectionKey(path, section, key, value);
    }


    private static bool HasSectionKey(string path, string section, string key)
    {
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var header = "[" + section + "]";
        var start = Array.FindIndex(lines, x => string.Equals(x.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return false;
        for (var i = start + 1; i < lines.Length && !lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal); i++)
            if (Regex.IsMatch(lines[i], @"^\s*" + Regex.Escape(key) + @"\s*=")) return true;
        return false;
    }


    private bool HasNativeArenaPatch()
    {
        try
        {
            var plugin = Path.Combine(_settings.HearthstoneDirectory, "BepInEx", "plugins", "ArenaBoxBot.dll");
            var hsmod = Path.Combine(_settings.HearthstoneDirectory, "BepInEx", "plugins", "HsMod.dll");
            return File.Exists(plugin) && File.Exists(hsmod) && new FileInfo(plugin).Length > 32 * 1024 && new FileInfo(hsmod).Length > 100 * 1024;
        }
        catch { return false; }
    }

}
