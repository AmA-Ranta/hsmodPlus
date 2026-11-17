using System.Windows.Forms;

namespace ArenaController;

/// <summary>
/// Public facade used by the WPF front-end.  The WinForms form remains the
/// single owner of controller logic; the host only exposes a stable, typed
/// surface and raises <see cref="Changed"/> whenever visible state may differ.
/// </summary>
public sealed class ControllerHost : IDisposable
{
    private readonly MainForm _form;

    public ControllerHost()
    {
        _form = new MainForm(false);
        _form.ShowInTaskbar = false;
        _form.WindowState = FormWindowState.Minimized;
        _form.Opacity = 0;
        _form.HostChanged += () => Changed?.Invoke(this, EventArgs.Empty);
        _form.HostStartEngine();
    }

    public event EventHandler? Changed;

    public string StatusText => _form.HostStatusText;
    public string RuntimeText => _form.HostRuntimeText;
    public string StatsText => _form.HostStatsText;
    public string LogText => _form.HostLogText;
    public bool Running => _form.HostRunning;
    public bool PendingApply => _form.HostPendingApply;
    public bool Starting => _form.HostStarting;
    public int InstanceCount => _form.HostInstanceCount;
    public bool GameRunning => _form.HostGameRunning;
    public bool BoxRunning => _form.HostBoxRunning;
    public bool PluginReady => _form.HostPluginReady;

    public ControllerSettingsSnapshot Settings => _form.HostSettingsSnapshot();

    public Task StartAsync() => _form.HostStartAsync();
    public void Stop() => _form.HostStop();
    public void ApplySettings() => _form.HostApplySettings();
    public void BeginInstall() => _form.HostInstall();
    public void BeginRestore() => _form.HostRestore();
    public void DetectPaths() => _form.HostDetectPaths();
    public void AppendLog(string text) => _form.HostAppend(text);

    public void SetMode(string mode) => _form.HostSetMode(mode);
    public void SetDeckName(string deck) => _form.HostSetDeckName(deck);
    public void SetArenaMode(string arena) => _form.HostSetArenaMode(arena);
    public void SetLimits(int perRun, int total, int runs) => _form.HostSetLimits(perRun, total, runs);
    public void SetSchedule(bool enabled, DateTime start, DateTime stop) => _form.HostSetSchedule(enabled, start, stop);
    public void SetHeartbeatRequired(bool required) => _form.HostSetHeartbeatRequired(required);
    public void SetStops(bool ticket, bool gold, bool closeGame, bool reconnect) => _form.HostSetStops(ticket, gold, closeGame, reconnect);
    public void SetPaths(string hearthstoneDirectory, string boxExecutable) => _form.HostSetPaths(hearthstoneDirectory, boxExecutable);
    public System.Collections.Generic.IReadOnlyList<string> DeckOptions => _form.HostDeckOptions();
    public System.Collections.Generic.IReadOnlyList<string> RecentActivity => _form.HostRecentActivity();
    public bool ArenaStopAvailable => _form.HostArenaStopAvailable;
    public void RequestArenaRunStop() => _form.HostRequestArenaRunStop();
    public void CancelArenaRunStop() => _form.HostCancelArenaRunStop();
    public void RefreshDeckOptions() => _form.HostRefreshDeckOptions();

    public void Dispose() => _form.HostShutdown();
}

/// <summary>Applied settings snapshot for the WPF pages.</summary>
public sealed record ControllerSettingsSnapshot(
    string Mode,
    string DeckName,
    string ArenaMode,
    int MaxGamesPerRun,
    int MaxTotalGames,
    int MaxRuns,
    bool StopOnTicketShortage,
    bool StopOnGoldShortage,
    bool CloseGameAfterStop,
    bool AutoReconnect,
    bool AutoSchedule,
    bool HeartbeatRequired,
    DateTime StartTime,
    DateTime StopTime,
    string HearthstoneDirectory,
    string BoxExecutablePath);
