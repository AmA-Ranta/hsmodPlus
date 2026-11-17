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

    private void LoadSettings()
    {
        _suppressSettingsDirty = true;
        _settings = _settingsStore.Load();
        DetectInstallPaths();
        RefreshPathBoxes();
        if (_settings.Mode == "地下竞技场") _settings.Mode = "竞技场";
        _mode.SelectedItem = _settings.Mode; if (_mode.SelectedIndex < 0) _mode.SelectedIndex = 0; _deckName.Text = _settings.DeckName; _arenaMode.SelectedItem = _settings.ArenaMode; if (_arenaMode.SelectedIndex < 0) _arenaMode.SelectedIndex = 0; UpdateModeUi();
        _runGames.Value = Math.Clamp(_settings.MaxGamesPerRun, 0, 1000); _totalGames.Value = Math.Clamp(_settings.MaxTotalGames, 0, 100000); _maxRuns.Value = Math.Clamp(_settings.MaxRuns, 0, 10000);
        _heartbeatRequired.Checked = _settings.RequireBoxHeartbeat; _scheduleEnabled.Checked = _settings.AutoSchedule; _closeGame.Checked = _settings.CloseGameAfterStop;
        _ticketStop.Checked = _settings.StopOnTicketShortage; _goldStop.Checked = _settings.StopOnGoldShortage; _autoReconnect.Checked = _settings.AutoReconnect;
        _startTime.Value = ParseTime(_settings.StartTime, 8, 0); _stopTime.Value = ParseTime(_settings.StopTime, 23, 0);
        _suppressSettingsDirty = false;
        _pendingSettings = null;
        _settingsDirty = false;
        UpdateApplyUi();
        RefreshStats();
    }


    private void SaveSettings()
    {
        // Only the applied snapshot is persisted; UI edits stay pending until
        // the user presses 应用设置.
        _settingsStore.Save(_settings);
    }


    private ControllerSettings CloneSettings(ControllerSettings source) =>
        ControllerSettingsStore.Clone(source);


    private ControllerSettings CaptureUiSettings()
    {
        var staged = CloneSettings(_settings);
        staged.Mode = _mode.SelectedItem?.ToString() ?? _settings.Mode;
        staged.ArenaMode = _arenaMode.SelectedItem?.ToString() ?? _settings.ArenaMode;
        staged.DeckName = _deckName.Text.Trim();
        staged.MaxGamesPerRun = (int)_runGames.Value;
        staged.MaxTotalGames = (int)_totalGames.Value;
        staged.MaxRuns = (int)_maxRuns.Value;
        staged.RequireBoxHeartbeat = _heartbeatRequired.Checked;
        staged.AutoSchedule = _scheduleEnabled.Checked;
        staged.CloseGameAfterStop = _closeGame.Checked;
        staged.StopOnTicketShortage = _ticketStop.Checked;
        staged.StopOnGoldShortage = _goldStop.Checked;
        staged.AutoReconnect = _autoReconnect.Checked;
        staged.StartTime = _startTime.Value.ToString("HH:mm");
        staged.StopTime = _stopTime.Value.ToString("HH:mm");
        return staged;
    }


    private void MarkSettingsDirty()
    {
        if (_suppressSettingsDirty) return;
        _pendingSettings = null;
        _settingsDirty = true;
        UpdateApplyUi();
    }


    private void UpdateApplyUi()
    {
        if (_settingsState.IsDisposed) return;
        _settingsState.Text = _pendingSettings != null ? "已暂存：本局结束后应用"
            : _settingsDirty ? "有未应用的修改" : "设置已应用";
        _settingsState.ForeColor = _pendingSettings != null ? Color.FromArgb(92, 168, 220)
            : _settingsDirty ? Color.FromArgb(214, 170, 74) : Color.FromArgb(137, 163, 188);
        _applySettings.Enabled = _settingsDirty || _pendingSettings != null;
    }


    private void ApplyStagedSettings()
    {
        var staged = CaptureUiSettings();
        if (_running && !_stopRequested && IsMatchInProgress())
        {
            _pendingSettings = staged;
            _settingsDirty = true;
            UpdateApplyUi();
            Append("设置已暂存：当前对局结束后自动应用，本局不受影响。");
            if (!string.Equals(staged.Mode, _activeRunMode, StringComparison.Ordinal))
                QueueModeSwitchStart("应用设置切换模式");
            return;
        }
        CommitSettings(staged, false);
        if (_running && !string.Equals(_settings.Mode, _activeRunMode, StringComparison.Ordinal) &&
            (!_stopRequested || _pendingModeSwitchStart))
            QueueModeSwitchStart("应用设置切换模式");
        else if (_running && _pendingModeSwitchStart)
            QueueModeSwitchStart("应用设置切换模式");
    }


    private void CommitSettings(ControllerSettings staged, bool afterMatch)
    {
        _settings = staged;
        _pendingSettings = null;
        _settingsDirty = false;
        SaveSettings();
        UpdateApplyUi();
        Append((afterMatch ? "本局已结束，暂存设置已应用：" : "设置已应用：") + DescribeSettings(_settings));
    }


    private void WireSettingsTracking()
    {
        _mode.SelectedIndexChanged += (_, _) => MarkSettingsDirty();
        _arenaMode.SelectedIndexChanged += (_, _) => MarkSettingsDirty();
        _deckName.TextChanged += (_, _) => MarkSettingsDirty();
        _runGames.ValueChanged += (_, _) => MarkSettingsDirty();
        _totalGames.ValueChanged += (_, _) => MarkSettingsDirty();
        _maxRuns.ValueChanged += (_, _) => MarkSettingsDirty();
        _heartbeatRequired.CheckedChanged += (_, _) => MarkSettingsDirty();
        _scheduleEnabled.CheckedChanged += (_, _) => MarkSettingsDirty();
        _startTime.ValueChanged += (_, _) => MarkSettingsDirty();
        _stopTime.ValueChanged += (_, _) => MarkSettingsDirty();
        _ticketStop.CheckedChanged += (_, _) => MarkSettingsDirty();
        _goldStop.CheckedChanged += (_, _) => MarkSettingsDirty();
        _closeGame.CheckedChanged += (_, _) => MarkSettingsDirty();
        _autoReconnect.CheckedChanged += (_, _) => MarkSettingsDirty();
    }


    private static DateTime ParseTime(string value, int hour, int minute)
    { return DateTime.TryParse(value, out var parsed) ? DateTime.Today.Add(parsed.TimeOfDay) : DateTime.Today.AddHours(hour).AddMinutes(minute); }


    private void RefreshPathBoxes()
    {
        SetPathBox("炉石位置", _settings.HearthstoneDirectory);
        SetPathBox("盒子位置", _settings.BoxExecutablePath);
        SetPathBox("内置插件源", Path.Combine(AppContext.BaseDirectory, "ArenaBoxBot.dll"));
        SetPathBox("HsMod目录", _settings.HsModDirectory);
        SetPathBox("Arena配置", _settings.ArenaConfigPath);
        SetPathBox("HsMod配置", _settings.HsModConfigPath);
    }


    private void DetectInstallPaths()
    {
        var preferredGamePath = _settings.HearthstoneDirectory;
        // A typed path is authoritative even when it is currently invalid.
        // Passing it through lets the resolver explain the exact miss in the
        // developer log instead of silently probing another drive and making
        // the user think the visible installation was ignored.
        // The one exception is the untouched built-in default: it is a probe
        // hint, not a user selection, so a standard C: or E: installation can
        // still be found on first launch.
        var explicitGamePath = string.IsNullOrWhiteSpace(preferredGamePath) || IsKnownDefaultGamePath(preferredGamePath)
            ? null
            : preferredGamePath;
        var discovery = new NativeInstallService().Discover(new NativeInstallRequest
        {
            HearthstoneDirectory = explicitGamePath,
            BoxExecutablePath = string.IsNullOrWhiteSpace(_settings.BoxExecutablePath) || IsKnownDefaultBoxPath(_settings.BoxExecutablePath)
                ? null
                : _settings.BoxExecutablePath
        });
        var gameDir = discovery.HearthstoneDirectory;
        if (!string.IsNullOrEmpty(gameDir))
        {
            _settings.HearthstoneDirectory = gameDir;
            Append("已探测炉石目录：" + gameDir);
        }
        else
        {
            Append("未找到可识别的炉石安装目录：" + (discovery.PathFailureReason ?? "请填写游戏目录、Hearthstone.exe、官方启动器或 Hearthstone_Data 目录后重新探测。"));
            if (!string.IsNullOrWhiteSpace(discovery.DeveloperLogPath))
                Append("开发者诊断日志：" + discovery.DeveloperLogPath);
            if (!string.IsNullOrWhiteSpace(discovery.UserLogPath))
                Append("用户操作日志：" + discovery.UserLogPath);
            return;
        }
        var root = _settings.HearthstoneDirectory;
        _settings.HsModDirectory = Path.Combine(root, "BepInEx", "HsMod");
        _settings.ArenaConfigPath = Path.Combine(root, "BepInEx", "config", "local.hsmod.arenaboxbot.cfg");
        _settings.HsModConfigPath = Path.Combine(root, "BepInEx", "config", "HsMod.cfg");
        _settings.PluginSourcePath = Path.Combine(AppContext.BaseDirectory, "ArenaBoxBot.dll");
        var box = discovery.BoxExecutablePath;
        if (!string.IsNullOrEmpty(box)) _settings.BoxExecutablePath = box;
        if (box == null)
        {
            Append(discovery.PathFailureReason ?? "未找到炉石盒子 HSAng.exe（可填写盒子安装目录或 HSAng.exe 的完整路径）");
            if (!string.IsNullOrWhiteSpace(discovery.DeveloperLogPath))
                Append("开发者诊断日志：" + discovery.DeveloperLogPath);
            if (!string.IsNullOrWhiteSpace(discovery.UserLogPath))
                Append("用户操作日志：" + discovery.UserLogPath);
        }
        else Append("已探测炉石盒子：" + box);
    }

}
