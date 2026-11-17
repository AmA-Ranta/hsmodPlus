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


    private static DateTime? SafeGetLastWriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null; }
        catch { return null; }
    }


    private async Task<bool> WaitForBoxReadyAsync(TimeSpan timeout)
    {
        var collectorDiagnostic = GetBoxCollectorDiagnostic();
        if (collectorDiagnostic != null)
        {
            Append("盒子采集链路未就绪：" + collectorDiagnostic);
            return false;
        }
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var blocked = RecentBoxProcessBlock();
            if (blocked != null)
            {
                if (!string.Equals(_lastBoxLinkDiagnostic, blocked, StringComparison.Ordinal))
                {
                    _lastBoxLinkDiagnostic = blocked;
                    Append("盒子采集链路未就绪：" + blocked);
                }
                return false;
            }
            if (IsArenaMode)
            {
                var profileDiagnostic = GetArenaScoringProfileDiagnostic();
                if (profileDiagnostic != null)
                {
                    if (!string.Equals(_lastBoxLinkDiagnostic, profileDiagnostic, StringComparison.Ordinal))
                    {
                        _lastBoxLinkDiagnostic = profileDiagnostic;
                        Append("竞技场盒子评分链路未就绪：" + profileDiagnostic);
                    }
                    await Task.Delay(3000);
                    if (IsDisposed) return false;
                    continue;
                }
            }
            var bridgeStatus = ReadHeartbeatStatus(HsModPath(BoxHeartbeatFileName), HsModPath(BoxRecommendationFileName));
            if (IsBoxFileSourceConfigured() && bridgeStatus.Fresh) return true;
            await Task.Delay(3000);
            if (IsDisposed) return false;
        }
        return false;
    }


    private string? GetArenaScoringProfileDiagnostic()
    {
        try
        {
            var path = HsModPath("boxai-direct-profile.dpapi");
            if (!File.Exists(path)) return "缺少盒子评分会话文件 boxai-direct-profile.dpapi";
            if (new FileInfo(path).Length < 32) return "盒子评分会话文件不完整";

            // The profile is DPAPI-protected by the Box bridge.  A file can
            // exist and still be unreadable after an account change, a stale
            // restore, or a partial write.  Validate the same fields that the
            // Arena plugin needs before allowing the controller to start.
            var cipher = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(
                cipher,
                Encoding.UTF8.GetBytes("HsAuto.NeteaseDirectRequestProfile.v1"),
                DataProtectionScope.CurrentUser);
            using var document = JsonDocument.Parse(plain);
            var root = document.RootElement;
            var endpoint = root.TryGetProperty("Endpoint", out var endpointValue)
                ? endpointValue.GetString() : null;
            var sid = root.TryGetProperty("Sid", out var sidValue)
                ? sidValue.GetString() : null;
            var userAgent = root.TryGetProperty("UserAgent", out var userAgentValue)
                ? userAgentValue.GetString() : null;
            var rc4Text = root.TryGetProperty("Rc4Key", out var rc4Value)
                ? rc4Value.GetString() : null;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttps && endpointUri.Scheme != Uri.UriSchemeHttp))
                return "盒子评分会话 Endpoint 无效";
            if (string.IsNullOrWhiteSpace(sid)) return "盒子评分会话缺少 Sid";
            if (string.IsNullOrWhiteSpace(userAgent)) return "盒子评分会话缺少 UserAgent";
            byte[] rc4;
            try { rc4 = Convert.FromBase64String(rc4Text ?? ""); }
            catch (FormatException) { return "盒子评分会话 Rc4Key 不是有效 Base64"; }
            if (rc4.Length != 16) return "盒子评分会话 Rc4Key 长度无效";
            return null;
        }
        catch (CryptographicException) { return "盒子评分会话无法用当前 Windows 用户解密；请重新登录盒子"; }
        catch (JsonException) { return "盒子评分会话不是有效 JSON"; }
        catch (Exception ex)
        {
            return "读取盒子评分会话失败：" + ex.GetType().Name;
        }
    }


    private string? GetBoxCollectorDiagnostic()
    {
        try
        {
            var boxDirectory = Path.GetDirectoryName(_settings.BoxExecutablePath);
            if (string.IsNullOrWhiteSpace(boxDirectory)) return "盒子路径为空";
            var ini = Path.Combine(boxDirectory, "RankBypass.ini");
            if (!File.Exists(ini))
                return "缺少盒子采集配置 RankBypass.ini；请先执行安装 / 修复插件";
            var lines = File.ReadAllLines(ini, Encoding.UTF8);
            var collector = lines.FirstOrDefault(l => Regex.IsMatch(l, @"^\s*EnableCollector\s*=", RegexOptions.IgnoreCase));
            if (collector == null || !Regex.IsMatch(collector, @"=\s*1\s*$", RegexOptions.IgnoreCase))
                return "RankBypass.ini 未启用采集（EnableCollector=1）";
            var block = lines.FirstOrDefault(l => Regex.IsMatch(l, @"^\s*BlockProcesses\s*=", RegexOptions.IgnoreCase));
            if (block == null || !Regex.IsMatch(block, @"=\s*$", RegexOptions.IgnoreCase))
                return "RankBypass.ini 的 BlockProcesses 必须留空；填写 0 也会拦截 HSAExternal.exe";
            var output = lines.FirstOrDefault(l => Regex.IsMatch(l, @"^\s*OutputDir\s*=", RegexOptions.IgnoreCase));
            var expected = Path.GetFullPath(_settings.HsModDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (output == null || !Regex.IsMatch(output, @"^\s*OutputDir\s*=", RegexOptions.IgnoreCase))
                return "RankBypass.ini 缺少 OutputDir，盒子无法写入 HsMod 采集文件";
            var actual = output[(output.IndexOf('=') + 1)..].Trim().Trim('"');
            if (!string.Equals(Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                return "RankBypass.ini 的 OutputDir 未指向当前炉石 HsMod 目录";
            return null;
        }
        catch (Exception ex)
        {
            return "读取 RankBypass.ini 失败：" + ex.GetType().Name + "：" + ex.Message;
        }
    }


    private string? RecentBoxProcessBlock()
    {
        try
        {
            var boxDirectory = Path.GetDirectoryName(_settings.BoxExecutablePath);
            if (string.IsNullOrWhiteSpace(boxDirectory)) return null;
            var logPath = Path.Combine(boxDirectory, "RankBypass.log");
            if (!File.Exists(logPath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(logPath) > TimeSpan.FromMinutes(5)) return null;
            var lines = File.ReadLines(logPath).Reverse().Take(80);
            var line = lines.FirstOrDefault(x => x.Contains("CreateProcess", StringComparison.OrdinalIgnoreCase) &&
                                                   x.Contains("HSAExternal.exe", StringComparison.OrdinalIgnoreCase) &&
                                                   x.Contains("blocked", StringComparison.OrdinalIgnoreCase));
            return line == null ? null : "version.dll 最近拦截了 HSAExternal.exe；请重新安装以清空 BlockProcesses（记录：" + line.Trim() + "）";
        }
        catch { return null; }
    }


    private bool IsBoxFileSourceConfigured()
    {
        try
        {
            // HsMod's UI displays 盒子文件, while the 1004 build persists the
            // enum as BoxFile. Accept both spellings so a fresh install does
            // not wait forever even though the heartbeat is healthy.
            return File.ReadAllLines(_settings.HsModConfigPath).Any(l =>
                Regex.IsMatch(l, @"^\s*推荐来源\s*=\s*(盒子文件|BoxFile)\s*$", RegexOptions.IgnoreCase));
        }
        catch { return false; }
    }


    private static bool IsFresh(string path, int seconds)
    { try { return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(seconds); } catch { return false; } }


    /// <summary>
    /// HsMod can report "selected deck missing" after a client restart even
    /// though the script is on.  Re-run the exact deck selection gate so the
    /// task does not sit in a running-but-idle state.
    /// </summary>
    /// <summary>
    /// The Box embeds a full-screen Qt overlay above the game. Without
    /// WS_EX_TRANSPARENT it consumes clicks that the user aims at the game or
    /// the in-game plugin settings. Keep it click-through while controlling.
    /// </summary>
    private void EnsureBoxOverlayClickThrough()
    {
        if (DateTime.Now - _lastBoxOverlayPassAt < TimeSpan.FromSeconds(3)) return;
        _lastBoxOverlayPassAt = DateTime.Now;
        try
        {
            var applied = BoxOverlayClickThrough.Apply(BoxOverlayClickThrough.RunningBoxProcessIds().ToArray(),
                message => AppendDeveloper(message));
            if (applied > 0)
            {
                Append("已让盒子悬浮窗点击穿透，游戏内设置与弹窗可以正常点击。");
            }
        }
        catch (Exception ex) { AppendDeveloper("box-overlay.pass.failed=" + ex.Message); }
    }


    private void ObserveInvalidDeck()
    {
        try
        {
            if (_starting || _reconnecting || _stopRequested) return;
            var activity = _hsModStatus?.Activity;
            if (string.IsNullOrWhiteSpace(activity)) return;
            if (!activity.Contains("所选卡组已删除", StringComparison.Ordinal) &&
                !activity.Contains("请重新选择", StringComparison.Ordinal)) return;
            if (DateTime.Now - _lastDeckHealAt < TimeSpan.FromSeconds(30)) return;
            _lastDeckHealAt = DateTime.Now;
            _ = HealInvalidDeckAsync();
        }
        catch (Exception ex) { AppendDeveloper("deck-heal.observe.failed=" + ex.Message); }
    }


    private async Task HealInvalidDeckAsync()
    {
        try
        {
            await EnsureConfiguredConstructedDeckCoreAsync(force: true).ConfigureAwait(true);
            Append("检测到游戏报告卡组无效，已重新选择指定卡组并恢复对局。");
        }
        catch (Exception ex)
        {
            AppendDeveloper("deck-heal.failed=" + ex.GetType().Name + ":" + ex.Message);
            Append("卡组无效且重新选择失败：" + ex.Message);
        }
    }

}
