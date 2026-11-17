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

internal sealed class ArenaStats
{
    public int TotalWins { get; set; }
    public int TotalLosses { get; set; }
    public int CurrentRunWins { get; set; }
    public int CurrentRunLosses { get; set; }
    public int RunCount { get; set; }
    public int HighestWins { get; set; }
    public int RewardsClaimed { get; set; }
    public int TicketsPurchased { get; set; }
    public int GoldSpent { get; set; }
    public bool RunActive { get; set; }
    public string RunKey { get; set; } = "";
    public bool RewardPending { get; set; }
    public bool PurchasePending { get; set; }
    public ArenaRoundIdentity? NormalRun { get; set; }
    public ArenaRoundIdentity? UndergroundRun { get; set; }
    [JsonIgnore] public bool SnapshotKnown { get; set; }
    [JsonIgnore] public string CompletedRunKey => NormalRun?.RunKey == RunKey ? NormalRun.LastCompletedRunKey :
        UndergroundRun?.RunKey == RunKey ? UndergroundRun.LastCompletedRunKey : "";
}

internal sealed class ArenaRoundIdentity
{
    public string RunKey { get; set; } = "";
    public string LastCompletedRunKey { get; set; } = "";
}

internal sealed class LadderSummary
{
    public bool Reliable { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }
    public int Games { get; set; }
    public string LastResult { get; set; } = "无记录";
    public string LastRank { get; set; } = "";
    public string LastTime { get; set; } = "";
    public string Mmr { get; set; } = "";
}

internal sealed class BattleNetAuthenticationRequiredException : InvalidOperationException
{
    public BattleNetAuthenticationRequiredException(string? reason = null)
        : base(reason ?? "Battle.net 当前仍在登录页；暂停官方启动请求，登录完成后自动继续。")
    {
    }
}

internal sealed partial class MainForm : Form
{
    private const string EnabledFileName = "arena-bot.enabled";

    // Contract with ArenaBoxBotPlugin: this marker asks it to finish the
    // current game/reward flow and stop before opening another run.
    private const string StopAfterGameFileName = "controller-stop-after-game.marker";

    // Contract with ArenaBoxBotPlugin: the plugin writes this marker when a
    // resource/budget condition stops Arena before the next run.  The
    // controller consumes it and finishes its own task state.
    private const string InternalStopFileName = "arena-stop-requested.marker";

    private const string ConstructedControlFileName = "constructed-script.enabled";

    private const string StatsFileName = "arena-stats.json";

    private const string ArenaLogFileName = "arena-box-bot.log";

    private const string ArenaRuntimeStateFileName = "arena-runtime-state.json";

    private const string BoxHeartbeatFileName = "boxai-heartbeat.json";

    private const string BoxRecommendationFileName = "boxai-recommendation.json";

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

    private readonly ComboBox _mode = new();

    private readonly ComboBox _arenaMode = new();

    private readonly ComboBox _deckName = new();

    private readonly NumericUpDown _runGames = new();

    private readonly NumericUpDown _totalGames = new();

    private readonly NumericUpDown _maxRuns = new();

    private readonly CheckBox _heartbeatRequired = new();

    private readonly CheckBox _scheduleEnabled = new();

    private readonly DateTimePicker _startTime = new();

    private readonly DateTimePicker _stopTime = new();

    private readonly CheckBox _closeGame = new();

    private readonly CheckBox _ticketStop = new();

    private readonly CheckBox _goldStop = new();

    private readonly Button _start = new();

    private readonly Button _stop = new();

    private readonly Button _install = new();

    private readonly CheckBox _autoReconnect = new();

    private readonly Label _status = new();

    private readonly Label _runtime = new();

    private readonly Label _stats = new();

    private readonly TextBox _log = new();

    private readonly ToolTip _pathToolTip = new();

    private readonly Dictionary<string, TextBox> _pathBoxes = new(StringComparer.Ordinal);

    private readonly bool _startOnShown;

    private ControllerSettings _settings = new();

    // UI edits are staged: they only reach _settings//config when the user
    // presses 应用设置, and while a match is in progress the apply is queued
    // until that match ends so a wrong click cannot change a live game.
    private ControllerSettings? _pendingSettings;

    private bool _settingsDirty;

    private bool _suppressSettingsDirty;

    private readonly Button _applySettings = new();

    private readonly Label _settingsState = new();

    private string _settingsPath = "";

    private readonly ControllerSettingsStore _settingsStore;

    private readonly SessionModeBindingStore _sessionBindingStore;

    private DurableLogWriter? _userLogWriter;

    private DurableLogWriter? _developerLogWriter;

    private DateTime _lastNativeErrorNotice;

    private int _taskGeneration;

    private bool _running;

    private bool _starting;

    // A mode change while a task is active would change the controller's
    // interpretation of the live client before HsMod has applied the new
    // native mode.  Lock task inputs until the current game/reward boundary
    // is safely stopped, then unlock them for the next task.
    private bool _taskInputsLocked;

    private bool _stopRequested;

    // Queued mode switch: clicking 开始任务 while a task is running must not be
    // dropped. The controller finishes the current match, stops the script,
    // restarts the client so HsMod applies the newly selected mode, then starts
    // again. The latest applied settings win.
    private bool _pendingModeSwitchStart;

    private string _pendingModeSwitchReason = "";

    // Mode the current run was started with; a queued switch to this same mode
    // is a no-op instead of a needless client restart.
    private string _activeRunMode = "";

    // Set when a deferred stop already proved the post-game boundary, so the
    // mode-switch restart can happen immediately instead of waiting again.
    private bool _switchBoundaryConfirmed;

    private bool _stopWaitingForPostGame;

    private DateTime _stopRequestedAt;

    private ArenaStopLogEvidence? _arenaStopLogEvidence;

    private NativeLogReader? _arenaStopLogReader;

    private DateTime _startedAt;

    private ArenaStats _baseline = new();

    private int _baselineGames;

    private int _baselineRuns;

    private bool _arenaRunQuotaReached;

    private bool _arenaRoundLimitStopRequested;

    private int? _baselineLadderGames;

    private ConstructedStopRequest? _constructedStopRequest;

    private readonly NativeLogReader _nativeLogReader = new();

    private readonly NativeLogReader _connectionLogReader = new();

    private readonly ConnectionRecoveryMonitor _connectionRecovery = new();

    private readonly NativeActionDiagnostics _nativeActionDiagnostics = new();

    private bool _reconnecting;

    private bool _recoveryPending;

    private DateTime _lastReconnectAt;

    // A live Hearthstone process is not sufficient evidence that the client
    // is usable. Unity can leave the window open while the game/box bridge is
    // frozen. Track consecutive unhealthy observations and restart only after
    // a sustained failure, never during the initial launch grace period.
    private readonly ClientRecoveryMonitor _clientRecovery = new();

    private readonly ArenaProgressMonitor _arenaProgress = new();

    private DateTime? _nativeDisabledSince;

    private DateTime _lastDeckHealAt = DateTime.MinValue;

    private DateTime _lastBoxOverlayPassAt = DateTime.MinValue;

    private DateTime? _constructedScriptOffSinceUtc;

    private DateTime? _arenaSettledOffSinceUtc;

    private DateTime _lastPendingStopDebugAt = DateTime.MinValue;

    private DateTime _scriptEnabledConfirmedAt = DateTime.MinValue;

    private DateTime _lastStopReenableAt = DateTime.MinValue;
    private DateTime _lastStopCompletedAt = DateTime.MinValue;
    private DateTime _externalEnabledSince = DateTime.MinValue;

    private DateTime _lastStuckRecoveryAt = DateTime.MinValue;

    private int _stuckRecoveryCount;

    private DateTime _gameplayStallSince = DateTime.MinValue;

    private DateTime _loginStallSince = DateTime.MinValue;

    private string _loginStallGameId = "";

    private DateTime _lastBoxWatchdogRestartAt = DateTime.MinValue;

    private int _startupRetryCount;

    private bool _awaitingBattleNetLogin;

    private readonly BattleNetLoginWait _battleNetLoginWait = new();

    private bool _deckChoicesRefreshPending;

    private string _deckChoicesNativeMode = "";

    private DateTime _lastStartupRetryAt = DateTime.MinValue;

    private int _battleNetSessionRestartCount;

    private DateTime _lastBattleNetSessionRestartAt = DateTime.MinValue;

    private DateTime _lastConnectionRecoveryPendingNotice = DateTime.MinValue;

    private bool _networkHistoryPrimed;

    private int? _stopRequestedLadderGames;

    private readonly Dictionary<string, int> _lastReliableLadderGames = new(StringComparer.OrdinalIgnoreCase);

    // Prevent a completed task from being relaunched on every scheduler tick
    // while the same daily window is still open. Manual starts clear it;
    // leaving the window clears it automatically for the next day.
    private bool _scheduleStopLatched;

    private string _lastRuntimeSnapshot = "";

    private HsModStatusSnapshot? _hsModStatus;

    private DateTime _lastHsModSuccessAt;

    private bool _hsModProbeUnavailable;

    private ArenaRuntimeSnapshot? _lastArenaRuntime;

    private string _lastArenaRuntimePath = "";

    private bool _arenaRuntimeReadUnavailable;

    private DateTime _lastHsModProbeAt;

    private int _hsModProbeRunning;

    private volatile HsModStatusSnapshot? _pendingProbeSnapshot;

    private volatile bool _pendingProbeReady;

    private int _monitorTickCount;

    private string _lastBoxLinkDiagnostic = "";

    // A healthy Hearthstone/Box process can still leave Arena matchmaking in
    // CLIENT_STARTED forever after the previous game disconnects. Keep a
    // short rolling timeout history so the controller can recover that native
    // game-server session without touching an active game.
    private readonly Queue<DateTime> _arenaMatchmakingTimeouts = new();

    private bool _arenaNetworkRecovery;

    private DateTime _lastArenaNetworkRecoveryAt = DateTime.MinValue;

    private readonly Queue<DateTime> _constructedMatchmakingTimeouts = new();

    private DateTime _lastConstructedMatchRecoveryAt = DateTime.MinValue;


    private static readonly TimeSpan StuckLaunchGrace = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan StuckRuntimeWindow = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan StuckRecoveryCooldown = TimeSpan.FromMinutes(3);


    private Icon? _bathIcon;


    public MainForm(bool startOnShown = false)
    {
        _startOnShown = startOnShown;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = "hsmodPlus · 🛀";
        // The published executable embeds a multi-resolution 🛀 icon.  Use the
        // embedded resource first so the title bar, Alt+Tab and Explorer all
        // show the same artwork; fall back to the runtime-drawn glyph if the
        // executable was started from an environment that strips resources.
        _bathIcon = LoadApplicationIcon();
        Icon = _bathIcon;
        // Give the control center enough room for real labels and controls.
        // The old 920px window forced several Chinese labels and checkboxes
        // into ellipses even after the grid was made responsive.
        Width = 1280;
        Height = 820;
        MinimumSize = new Size(1120, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Directory.CreateDirectory(ControllerDataDirectory);
        _settingsPath = Path.Combine(ControllerDataDirectory, "controller-settings.json");
        _settingsStore = new ControllerSettingsStore(_settingsPath,
            Path.Combine(AppContext.BaseDirectory, "controller-settings.json"), AppendDeveloper);
        _sessionBindingStore = new SessionModeBindingStore(
            Path.Combine(ControllerDataDirectory, "session-mode.json"), AppendDeveloper);
        var migratedLegacySettings = _settingsStore.MigrateLegacyIfNeeded();
        BuildUi();
        if (migratedLegacySettings) Append("已将旧版中控设置迁移到当前用户配置目录。");
        LoadSettings();
        // Settings edits are staged: nothing reaches the persisted settings or
        // the game until 应用设置 is pressed (and after the current match when
        // one is running).
        WireSettingsTracking();
        UpdateApplyUi();
        // The plugin source is shipped beside the controller. A persisted
        // development path must never override that bundled source.
        _settings.PluginSourcePath = Path.Combine(AppContext.BaseDirectory, "ArenaBoxBot.dll");
        RefreshPathBoxes();
        _timer.Tick += (_, _) => MonitorTick();
        FormClosing += (_, _) => SaveSettings();
        FormClosed += (_, _) => DisposeBathIcon();
        Shown += async (_, _) => await RefreshDeckSuggestionsAsync();
        _stop.Enabled = false;
        // The timer also drives the daily scheduler while no task is active.
        _timer.Start();
        Append($"中控已启动；模式={_settings.Mode}；每日计划={_settings.AutoSchedule}；时间={_settings.StartTime}-{_settings.StopTime}");
        if (_startOnShown)
        {
            Shown += async (_, _) => await StartTaskAsync("监控器命令行启动");
        }
    }


    private static readonly Color UiSurface = Color.FromArgb(17, 28, 45);

    private static readonly Color UiSurfaceRaised = Color.FromArgb(24, 38, 59);

    private static readonly Color UiBorder = Color.FromArgb(47, 69, 92);

    private static readonly Color UiText = Color.FromArgb(222, 235, 245);

    private static readonly Color UiMuted = Color.FromArgb(142, 169, 191);

    private static readonly Color UiAccent = Color.FromArgb(46, 211, 183);

}
