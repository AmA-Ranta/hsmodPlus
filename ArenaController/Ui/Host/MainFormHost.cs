using System.Windows.Forms;
using ArenaController.Ui.Design;

namespace ArenaController;

/// <summary>
/// Host-side members on the WinForms form: state readout and setters used by
/// the WPF front-end.  All members run on the form's UI thread.
/// </summary>
internal sealed partial class MainForm
{
    internal event Action? HostChanged;

    internal void HostStartEngine()
    {
        // Constructor already starts the monitor timer; nothing else to wire.
    }

    internal void HostShutdown()
    {
        try
        {
            SaveSettings();
            _timer.Stop();
            _bathIcon?.Dispose();
            _bathIcon = null;
        }
        catch { }
    }

    internal string HostStatusText => _status.Text;
    internal string HostRuntimeText => _runtime.Text;
    internal string HostStatsText => _stats.Text;
    internal string HostLogText
    {
        get
        {
            var text = _log.Text;
            return text.Length <= 40000 ? text : text[^40000..];
        }
    }

    internal bool HostRunning => _running;
    internal bool HostPendingApply => _pendingSettings != null || _settingsDirty;
    internal bool HostStarting => _starting;
    internal bool HostGameRunning => IsHearthstoneRunning();
    internal bool HostBoxRunning => IsBoxRunning();
    internal bool HostPluginReady => _hsModStatus != null && _hsModStatus.Ready && !_hsModProbeUnavailable;

    internal int HostInstanceCount =>
        (HostGameRunning ? 1 : 0) + (HostBoxRunning ? 1 : 0) + (HostPluginReady ? 1 : 0);

    internal ControllerSettingsSnapshot HostSettingsSnapshot()
    {
        DateTime start, stop;
        try { start = ParseTime(_settings.StartTime, 20, 0); } catch { start = DateTime.Today.AddHours(20); }
        try { stop = ParseTime(_settings.StopTime, 23, 0); } catch { stop = DateTime.Today.AddHours(23); }
        return new ControllerSettingsSnapshot(
            _settings.Mode ?? "",
            _settings.DeckName ?? "",
            _settings.ArenaMode ?? "",
            _settings.MaxGamesPerRun,
            _settings.MaxTotalGames,
            _settings.MaxRuns,
            _settings.StopOnTicketShortage,
            _settings.StopOnGoldShortage,
            _settings.CloseGameAfterStop,
            _settings.AutoReconnect,
            _settings.AutoSchedule,
            _heartbeatRequired.Checked,
            start,
            stop,
            _settings.HearthstoneDirectory ?? "",
            _settings.BoxExecutablePath ?? "");
    }

    internal IReadOnlyList<string> HostRecentActivity() =>
        _recentEvents.Select(e => e.At.ToString("HH:mm") + "\u0001" + e.Text).ToList();

    internal bool HostArenaStopAvailable => IsArenaSelectedInUi;

    internal void HostRequestArenaRunStop() => RequestArenaRunStop();

    internal void HostCancelArenaRunStop() => CancelArenaRunStop();

    internal void HostRefreshDeckOptions() => _ = RefreshDeckSuggestionsAsync();

    internal IReadOnlyList<string> HostDeckOptions() => _deckName.Items.Cast<object>().Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();

    internal async Task HostStartAsync()
    {
        await StartTaskAsync("WPF 界面开始");
        HostChanged?.Invoke();
    }

    internal void HostStop()
    {
        RequestStop("WPF 界面停止");
        HostChanged?.Invoke();
    }

    internal void HostApplySettings()
    {
        ApplyStagedSettings();
        HostChanged?.Invoke();
    }

    internal void HostInstall()
    {
        InstallPlugin();
        HostChanged?.Invoke();
    }

    internal void HostRestore()
    {
        RestoreOriginal();
        HostChanged?.Invoke();
    }

    internal void HostDetectPaths()
    {
        DetectInstallPaths();
        RefreshPathBoxes();
        Append("已重新探测安装路径。");
    }

    internal void HostAppend(string text)
    {
        Append(text);
        HostChanged?.Invoke();
    }

    internal void HostSetMode(string mode)
    {
        if (!_mode.Items.Contains(mode)) return;
        _mode.SelectedItem = mode;
        UpdateModeUi();
    }

    internal void HostSetDeckName(string deck) => _deckName.Text = deck ?? "";

    internal void HostSetArenaMode(string arena)
    {
        if (!_arenaMode.Items.Contains(arena)) return;
        _arenaMode.SelectedItem = arena;
    }

    internal void HostSetLimits(int perRun, int total, int runs)
    {
        _runGames.Value = Clamp(_runGames, perRun);
        _totalGames.Value = Clamp(_totalGames, total);
        _maxRuns.Value = Clamp(_maxRuns, runs);
    }

    private static decimal Clamp(NumericUpDown control, int value) =>
        Math.Min(control.Maximum, Math.Max(control.Minimum, value));

    internal void HostSetSchedule(bool enabled, DateTime start, DateTime stop)
    {
        _scheduleEnabled.Checked = enabled;
        _startTime.Value = DateTime.Today.AddHours(start.Hour).AddMinutes(start.Minute);
        _stopTime.Value = DateTime.Today.AddHours(stop.Hour).AddMinutes(stop.Minute);
    }

    internal void HostSetHeartbeatRequired(bool required) => _heartbeatRequired.Checked = required;

    internal void HostSetStops(bool ticket, bool gold, bool closeGame, bool reconnect)
    {
        _ticketStop.Checked = ticket;
        _goldStop.Checked = gold;
        _closeGame.Checked = closeGame;
        _autoReconnect.Checked = reconnect;
    }

    internal void HostSetPaths(string hearthstoneDirectory, string boxExecutable)
    {
        if (_pathBoxes.TryGetValue("炉石位置", out var hs) && !string.IsNullOrWhiteSpace(hearthstoneDirectory)) hs.Text = hearthstoneDirectory;
        if (_pathBoxes.TryGetValue("盒子位置", out var box) && !string.IsNullOrWhiteSpace(boxExecutable)) box.Text = boxExecutable;
    }
}
