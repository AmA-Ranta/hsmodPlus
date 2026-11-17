using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ArenaController;

namespace HsmodPlus;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly FontFamily UiFont = new("Microsoft YaHei UI");
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");
    private static readonly Brush CanvasBrush = MakeBrush(0xF3, 0xF3, 0xF3);
    private static readonly Brush CardBrush = Brushes.White;
    private static readonly Brush LineBrush = MakeBrush(0xE1, 0xE1, 0xE1);
    private static readonly Brush TextPrimary = MakeBrush(0x1A, 0x1A, 0x1A);
    private static readonly Brush TextSecondary = MakeBrush(0x5D, 0x5D, 0x5D);
    private static readonly Brush AccentBrush = MakeBrush(0x00, 0x5F, 0xB8);
    private static readonly Brush AccentSoft = MakeBrush(0xE8, 0xF1, 0xFB);
    private static readonly Brush SuccessBrush = MakeBrush(0x0F, 0x7B, 0x0F);
    private static readonly Brush WarningText = MakeBrush(0x9D, 0x5D, 0x00);
    private static readonly Brush ArenaTint = MakeBrush(0xF7, 0xF4, 0xFF);

    private static readonly Dictionary<string, (string Title, string Subtitle)> Meta = new()
    {
        ["console"] = ("控制台", "状态、任务与竞技场设置"),
        ["logs"] = ("运行日志", "启动、停止、异常与恢复记录")
    };

    private sealed class NavItem
    {
        public string Key = "";
        public Border Border = null!;
        public TextBlock Icon = null!;
        public TextBlock Label = null!;
    }

    private ControllerHost _host = null!;
    private DispatcherTimer _timer = null!;
    private readonly Dictionary<string, ScrollViewer> _pages = new();
    private readonly List<NavItem> _nav = new();
    private TextBlock _pageTitle = null!;
    private TextBlock _pageSubtitle = null!;
    private TextBlock _chipText = null!;
    private Border _chipDot = null!;
    private TextBlock _statusText = null!;
    private TextBlock _runtimeText = null!;
    private TextBlock _statsText = null!;
    private TextBox _logBox = null!;
    private ComboBox _modeBox = null!;
    private Wpf.Ui.Controls.TextBox _deckBox = null!;
    private TextBox _totalGamesBox = null!;
    private TextBox _runGamesBox = null!;
    private TextBox _arenaTotalBox = null!;
    private TextBox _maxRunsBox = null!;
    private CheckBox _heartbeatCheck = null!;
    private CheckBox _scheduleCheck = null!;
    private TextBox _startBox = null!;
    private TextBox _stopBox = null!;
    private CheckBox _ticketCheck = null!;
    private CheckBox _goldCheck = null!;
    private CheckBox _closeCheck = null!;
    private CheckBox _reconnectCheck = null!;
    private ComboBox _arenaBox = null!;
    private Wpf.Ui.Controls.Button _arenaStopButton = null!;
    private TextBox _hsPathBox = null!;
    private TextBox _boxPathBox = null!;
    private Wpf.Ui.Controls.Button _startButton = null!;
    private Wpf.Ui.Controls.Button _stopButton = null!;
    private TextBlock _settingsState = null!;
    private FrameworkElement _generalQuotaRow = null!;
    private FrameworkElement _arenaQuotaRow = null!;
    private bool _planLoaded;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/bath.ico"));
            Icon = icon;
            AppTitleBar.Icon = new Wpf.Ui.Controls.ImageIcon { Source = icon };
        }
        catch { }
        _host = new ControllerHost();
        _host.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshAll));
        BuildLayout();
        Loaded += (_, _) => { SelectPage("console"); LoadPlanFromHost(); RefreshAll(); };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshAll();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _host.Dispose(); };
    }

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static TextBlock Txt(string text, double size, Brush brush, bool bold = false, bool wrap = false)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = UiFont,
            FontSize = size,
            Foreground = brush,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
    }

    private static Border Card(UIElement child, Thickness? margin = null, Brush? background = null)
    {
        return new Border
        {
            Background = background ?? CardBrush,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = margin ?? new Thickness(0),
            Child = child
        };
    }

    private static TextBlock SectionTitle(string text)
    {
        var block = Txt(text, 13.5, TextPrimary, true);
        block.Margin = new Thickness(0, 0, 0, 8);
        return block;
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var root = new Grid { Background = CanvasBrush };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.Children.Add(BuildSidebar());

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
        main.RowDefinitions.Add(new RowDefinition());
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        main.Children.Add(BuildHeader());
        main.Children.Add(BuildContextHost());
        var actionBar = BuildActionBar();
        Grid.SetRow(actionBar, 2);
        main.Children.Add(actionBar);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        HostRoot.Children.Add(root);
    }

    private Border BuildSidebar()
    {
        var stack = new StackPanel { Margin = new Thickness(10, 10, 10, 8) };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 0, 16) };
        var logo = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = AccentBrush,
            Child = new TextBlock
            {
                Text = "H", FontFamily = new FontFamily("Segoe UI"), FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        brand.Children.Add(logo);
        var name = Txt("hsmodPlus", 13, TextPrimary, true);
        name.Margin = new Thickness(8, 0, 0, 0);
        brand.Children.Add(name);
        stack.Children.Add(brand);

        stack.Children.Add(NavRow("console", "\uE80F", "控制台"));
        stack.Children.Add(NavRow("logs", "\uE9D9", "运行日志"));

        var footer = new StackPanel { Margin = new Thickness(2, 20, 0, 0) };
        footer.Children.Add(new Separator { Background = LineBrush, Margin = new Thickness(0, 0, 0, 8) });
        footer.Children.Add(Txt("v1031 · 插件独立", 10.5, TextSecondary));
        var author = Txt("作者：一天洗三次澡", 10.5, TextSecondary);
        author.Margin = new Thickness(0, 3, 0, 0);
        footer.Children.Add(author);
        stack.Children.Add(footer);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = LineBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack }
        };
    }

    private Border NavRow(string key, string glyph, string label)
    {
        var icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 13, Foreground = TextSecondary, VerticalAlignment = VerticalAlignment.Center, Width = 22 };
        var text = Txt(label, 12, TextSecondary);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(text);
        var border = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 1, 0, 1), Child = row
        };
        border.MouseLeftButtonUp += (_, _) => SelectPage(key);
        _nav.Add(new NavItem { Key = key, Border = border, Icon = icon, Label = text });
        return border;
    }

    private UIElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(18, 6, 18, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _pageTitle = Txt("控制台", 18, TextPrimary, true);
        _pageSubtitle = Txt("状态、任务与竞技场设置", 11.5, TextSecondary);
        _pageSubtitle.Margin = new Thickness(0, 2, 0, 0);
        titles.Children.Add(_pageTitle);
        titles.Children.Add(_pageSubtitle);
        grid.Children.Add(titles);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _chipText = Txt("未运行", 11.5, TextPrimary);
        _chipDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        chipRow.Children.Add(_chipDot);
        chipRow.Children.Add(_chipText);
        var chip = new Border { Height = 30, CornerRadius = new CornerRadius(6), Background = Brushes.White, BorderBrush = LineBrush, BorderThickness = new Thickness(1), Padding = new Thickness(10, 0, 10, 0), Child = chipRow };
        right.Children.Add(chip);

        _startButton = new Wpf.Ui.Controls.Button { Content = "开始任务", Height = 34, MinWidth = 96, Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
        _startButton.Click += async (_, _) => await _host.StartAsync();
        _stopButton = new Wpf.Ui.Controls.Button { Content = "停止并等待本局", Height = 34, MinWidth = 124, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary };
        _stopButton.Click += (_, _) => _host.Stop();
        right.Children.Add(_startButton);
        right.Children.Add(_stopButton);

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private Grid BuildContextHost()
    {
        var host = new Grid { Margin = new Thickness(18, 0, 18, 4) };
        Grid.SetRow(host, 1);
        foreach (var key in Meta.Keys)
        {
            var page = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
            page.Content = BuildPage(key);
            _pages[key] = page;
            host.Children.Add(page);
        }
        return host;
    }

    private UIElement BuildPage(string key) => key switch
    {
        "console" => BuildConsolePage(),
        _ => BuildLogsPage()
    };

    // ---------------------------------------------------------------- console

    private UIElement BuildConsolePage()
    {
        var stack = new StackPanel();

        // 运行状态：常驻一行块，尽量少占高度。
        var status = new StackPanel();
        _statusText = Txt("未运行", 13, TextPrimary, true, true);
        status.Children.Add(_statusText);
        _runtimeText = Txt("", 11.5, TextPrimary, false, true);
        _runtimeText.Margin = new Thickness(0, 3, 0, 2);
        status.Children.Add(_runtimeText);
        _statsText = Txt("", 11.5, TextSecondary, false, true);
        status.Children.Add(_statsText);
        stack.Children.Add(Card(status, new Thickness(0, 0, 0, 6)));

        // 通用任务：天梯 / 狂野 / 休闲 / 酒馆战棋，只需对局数。
        var general = new StackPanel();
        general.Children.Add(SectionTitle("通用任务（天梯 / 狂野 / 休闲 / 酒馆战棋）"));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

        _modeBox = new ComboBox { Height = 34, Margin = new Thickness(0, 4, 8, 4) };
        _modeBox.Items.Add("标准"); _modeBox.Items.Add("狂野"); _modeBox.Items.Add("休闲"); _modeBox.Items.Add("酒馆战棋"); _modeBox.Items.Add("竞技场");
        // Keep the engine in sync immediately: the start/switch button and the
        // queued-mode-switch message must see the mode the user just picked,
        // not just the last applied one.
        _modeBox.SelectionChanged += (_, _) =>
        {
            UpdateModeUi();
            if (_modeBox.SelectedItem is string pickedMode) _host.SetMode(pickedMode);
        };
        AddField(grid, 0, "模式", _modeBox, 0);
        _deckBox = new Wpf.Ui.Controls.TextBox
        {
            Height = 34,
            Margin = new Thickness(0, 4, 0, 4),
            PlaceholderText = "填写卡组名称（如：快攻 / 海盗战），默认沿用上次填写"
        };
        AddField(grid, 0, "卡组名称", _deckBox, 3);

        var quotaRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        quotaRow.Children.Add(Txt("对局数", 11.5, TextSecondary));
        _totalGamesBox = new TextBox { Width = 88, Height = 34, Margin = new Thickness(8, 0, 6, 0) };
        quotaRow.Children.Add(_totalGamesBox);
        quotaRow.Children.Add(Txt("局（0 = 不限）", 10.5, TextSecondary));
        _generalQuotaRow = quotaRow;
        Grid.SetRow(quotaRow, 1);
        Grid.SetColumn(quotaRow, 0);
        Grid.SetColumnSpan(quotaRow, 2);
        grid.Children.Add(quotaRow);

        _heartbeatCheck = new CheckBox { Content = "启动前等待盒子心跳", Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(_heartbeatCheck, 1);
        Grid.SetColumn(_heartbeatCheck, 3);
        Grid.SetColumnSpan(_heartbeatCheck, 2);
        grid.Children.Add(_heartbeatCheck);

        var scheduleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _scheduleCheck = new CheckBox { Content = "每日计划", Margin = new Thickness(0, 0, 10, 0) };
        scheduleRow.Children.Add(_scheduleCheck);
        scheduleRow.Children.Add(Txt("开始", 11, TextSecondary));
        _startBox = new TextBox { Width = 60, Height = 34, Margin = new Thickness(6, 0, 12, 0) };
        scheduleRow.Children.Add(_startBox);
        scheduleRow.Children.Add(Txt("停止", 11, TextSecondary));
        _stopBox = new TextBox { Width = 60, Height = 34, Margin = new Thickness(6, 0, 0, 0) };
        scheduleRow.Children.Add(_stopBox);
        Grid.SetRow(scheduleRow, 2);
        Grid.SetColumn(scheduleRow, 0);
        Grid.SetColumnSpan(scheduleRow, 5);
        grid.Children.Add(scheduleRow);

        general.Children.Add(grid);
        stack.Children.Add(Card(general, new Thickness(0, 0, 0, 6)));

        // 竞技场：独立统计与策略，额度也只在这里出现。
        var arena = new StackPanel();
        arena.Children.Add(SectionTitle("竞技场（独立统计 / 仅竞技场模式生效）"));
        var arenaGrid = new Grid();
        arenaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        arenaGrid.ColumnDefinitions.Add(new ColumnDefinition());
        arenaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        arenaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        arenaGrid.ColumnDefinitions.Add(new ColumnDefinition());
        arenaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        arenaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        arenaGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });

        var arenaTypeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _arenaBox = new ComboBox { Width = 150, Height = 34, Margin = new Thickness(0, 0, 12, 0) };
        _arenaBox.Items.Add("普通竞技场");
        _arenaBox.Items.Add("地下竞技场");
        arenaTypeRow.Children.Add(Txt("类型", 11.5, TextSecondary));
        arenaTypeRow.Children.Add(_arenaBox);
        _arenaStopButton = new Wpf.Ui.Controls.Button { Content = "本轮结束后停止", Height = 34, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary };
        _arenaStopButton.Click += (_, _) => _host.RequestArenaRunStop();
        arenaTypeRow.Children.Add(_arenaStopButton);
        Grid.SetRow(arenaTypeRow, 0);
        Grid.SetColumn(arenaTypeRow, 0);
        Grid.SetColumnSpan(arenaTypeRow, 5);
        arenaGrid.Children.Add(arenaTypeRow);

        var arenaQuota = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        arenaQuota.Children.Add(Txt("每轮对局", 11.5, TextSecondary));
        _runGamesBox = new TextBox { Width = 60, Height = 34, Margin = new Thickness(6, 0, 16, 0) };
        arenaQuota.Children.Add(_runGamesBox);
        arenaQuota.Children.Add(Txt("总局", 11.5, TextSecondary));
        _arenaTotalBox = new TextBox { Width = 70, Height = 34, Margin = new Thickness(6, 0, 16, 0) };
        arenaQuota.Children.Add(_arenaTotalBox);
        arenaQuota.Children.Add(Txt("最多轮数", 11.5, TextSecondary));
        _maxRunsBox = new TextBox { Width = 60, Height = 34, Margin = new Thickness(6, 0, 0, 0) };
        arenaQuota.Children.Add(_maxRunsBox);
        _arenaQuotaRow = arenaQuota;
        Grid.SetRow(arenaQuota, 1);
        Grid.SetColumn(arenaQuota, 0);
        Grid.SetColumnSpan(arenaQuota, 5);
        arenaGrid.Children.Add(arenaQuota);

        var arenaStops = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _ticketCheck = new CheckBox { Content = "门票不足时停止", Margin = new Thickness(0, 0, 20, 0) };
        _goldCheck = new CheckBox { Content = "金币不足时停止", Margin = new Thickness(0, 0, 20, 0) };
        arenaStops.Children.Add(_ticketCheck);
        arenaStops.Children.Add(_goldCheck);
        arenaStops.Children.Add(Txt("0 = 不限；本轮停止只在本轮对局与领奖完成后生效。", 10.5, TextSecondary));
        Grid.SetRow(arenaStops, 2);
        Grid.SetColumn(arenaStops, 0);
        Grid.SetColumnSpan(arenaStops, 5);
        arenaGrid.Children.Add(arenaStops);
        arena.Children.Add(arenaGrid);
        var arenaCard = Card(arena, new Thickness(0, 0, 0, 6), ArenaTint);
        stack.Children.Add(arenaCard);

        // 其他：停止策略（非竞技场资源）与安装维护并排。
        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star) });

        var stops = new StackPanel();
        stops.Children.Add(SectionTitle("停止策略"));
        _closeCheck = new CheckBox { Content = "停止后关闭炉石", Margin = new Thickness(0, 2, 0, 4) };
        _reconnectCheck = new CheckBox { Content = "掉线 / 卡住自动恢复", Margin = new Thickness(0, 0, 0, 4) };
        stops.Children.Add(_closeCheck);
        stops.Children.Add(_reconnectCheck);
        var stopsCard = Card(stops, new Thickness(0, 0, 8, 0));
        Grid.SetColumn(stopsCard, 0);
        bottom.Children.Add(stopsCard);

        var install = new StackPanel();
        install.Children.Add(SectionTitle("安装维护"));
        var pathGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition());
        pathGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        pathGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        pathGrid.Children.Add(Txt("炉石位置", 11, TextSecondary));
        _hsPathBox = new TextBox { Height = 34, Margin = new Thickness(0, 2, 0, 2) };
        Grid.SetColumn(_hsPathBox, 1);
        pathGrid.Children.Add(_hsPathBox);
        var boxCaption = Txt("盒子位置", 11, TextSecondary);
        Grid.SetRow(boxCaption, 1);
        pathGrid.Children.Add(boxCaption);
        _boxPathBox = new TextBox { Height = 34, Margin = new Thickness(0, 2, 0, 2) };
        Grid.SetRow(_boxPathBox, 1);
        Grid.SetColumn(_boxPathBox, 1);
        pathGrid.Children.Add(_boxPathBox);
        install.Children.Add(pathGrid);

        var installButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var installButton = new Wpf.Ui.Controls.Button { Content = "安装 / 修复插件", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Height = 34, Margin = new Thickness(0, 2, 8, 0) };
        installButton.Click += (_, _) => _host.BeginInstall();
        var restoreButton = new Wpf.Ui.Controls.Button { Content = "还原原版", Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Height = 34, Margin = new Thickness(0, 2, 8, 0) };
        restoreButton.Click += (_, _) => _host.BeginRestore();
        var detectButton = new Wpf.Ui.Controls.Button { Content = "探测路径", Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Height = 34, Margin = new Thickness(0, 2, 0, 0) };
        detectButton.Click += (_, _) => _host.DetectPaths();
        installButtons.Children.Add(installButton);
        installButtons.Children.Add(restoreButton);
        installButtons.Children.Add(detectButton);
        install.Children.Add(installButtons);
        var note = Txt("保留你现有的 HsMod / version.dll 版本，只安装或修复插件。", 10.5, TextSecondary, false, true);
        note.Margin = new Thickness(0, 4, 0, 0);
        install.Children.Add(note);
        var installCard = Card(install);
        Grid.SetColumn(installCard, 1);
        bottom.Children.Add(installCard);
        stack.Children.Add(bottom);
        return stack;
    }

    private void AddField(Grid grid, int row, string caption, UIElement control, int column)
    {
        var label = Txt(caption, 11.5, TextSecondary);
        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column + 1);
        grid.Children.Add(control);
    }

    private UIElement BuildLogsPage()
    {
        _logBox = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"),
            FontSize = 11, Height = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap, Background = MakeBrush(0xFA, 0xFA, 0xFA)
        };
        var stack = new StackPanel();
        stack.Children.Add(Card(_logBox));
        return stack;
    }

    private UIElement BuildActionBar()
    {
        _settingsState = Txt("设置已应用", 11, TextSecondary);
        var apply = new Wpf.Ui.Controls.Button { Content = "应用设置", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Height = 30, MinWidth = 100 };
        apply.Click += (_, _) => ApplyPlan();
        var bar = new Grid { Margin = new Thickness(18, 4, 18, 8) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_settingsState);
        Grid.SetColumn(apply, 1);
        bar.Children.Add(apply);
        return bar;
    }

    // ------------------------------------------------------------ interaction

    private void SelectPage(string key)
    {
        foreach (var (pageKey, page) in _pages)
            page.Visibility = pageKey == key ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in _nav)
        {
            var selected = item.Key == key;
            item.Border.Background = selected ? AccentSoft : Brushes.Transparent;
            item.Label.Foreground = selected ? TextPrimary : TextSecondary;
            item.Label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            item.Icon.Foreground = selected ? AccentBrush : TextSecondary;
        }
        var meta = Meta.TryGetValue(key, out var m) ? m : ("控制台", "");
        _pageTitle.Text = meta.Item1;
        _pageSubtitle.Text = meta.Item2;
        if (key == "console" && !_planLoaded) LoadPlanFromHost();
        else if (key == "console") RefreshAll();
    }

    /// <summary>天梯/战棋只保留对局数；竞技场额度独立显示，避免概念混淆。</summary>
    private void UpdateModeUi()
    {
        var arena = _modeBox?.SelectedItem is string mode && string.Equals(mode, "竞技场", StringComparison.Ordinal);
        if (_generalQuotaRow != null) _generalQuotaRow.Opacity = arena ? 0.45 : 1.0;
        if (_totalGamesBox != null) _totalGamesBox.IsEnabled = !arena;
        if (_arenaQuotaRow != null) _arenaQuotaRow.Opacity = arena ? 1.0 : 0.75;
    }

    private void LoadPlanFromHost()
    {
        var s = _host.Settings;
        _modeBox.SelectedItem = _modeBox.Items.Contains(s.Mode) ? s.Mode : "标准";
        _deckBox.Text = s.DeckName;
        _totalGamesBox.Text = s.MaxTotalGames.ToString();
        _runGamesBox.Text = s.MaxGamesPerRun.ToString();
        _arenaTotalBox.Text = s.MaxTotalGames.ToString();
        _maxRunsBox.Text = s.MaxRuns.ToString();
        _heartbeatCheck.IsChecked = s.HeartbeatRequired;
        _scheduleCheck.IsChecked = s.AutoSchedule;
        _startBox.Text = s.StartTime.ToString("HH:mm");
        _stopBox.Text = s.StopTime.ToString("HH:mm");
        _ticketCheck.IsChecked = s.StopOnTicketShortage;
        _goldCheck.IsChecked = s.StopOnGoldShortage;
        _closeCheck.IsChecked = s.CloseGameAfterStop;
        _reconnectCheck.IsChecked = s.AutoReconnect;
        _arenaBox.SelectedItem = _arenaBox.Items.Contains(s.ArenaMode) ? s.ArenaMode : "普通竞技场";
        _hsPathBox.Text = s.HearthstoneDirectory;
        _boxPathBox.Text = s.BoxExecutablePath;
        _host.RefreshDeckOptions();
        // 卡组名称是手填文本：不依赖游戏内卡组列表，未开游戏也能填写。
        _planLoaded = true;
        UpdateModeUi();
    }

    private void ApplyPlan()
    {
        if (_modeBox.SelectedItem != null) _host.SetMode(_modeBox.SelectedItem.ToString()!);
        _host.SetDeckName(_deckBox.Text);
        _host.SetArenaMode(_arenaBox.SelectedItem?.ToString() ?? "普通竞技场");
        var arena = _modeBox.SelectedItem is string mode && string.Equals(mode, "竞技场", StringComparison.Ordinal);
        var runGames = arena ? ParseInt(_runGamesBox.Text, 0, 1000) : 0;
        var totalGames = arena ? ParseInt(_arenaTotalBox.Text, 0, 100000) : ParseInt(_totalGamesBox.Text, 0, 100000);
        var maxRuns = arena ? ParseInt(_maxRunsBox.Text, 0, 10000) : 0;
        _host.SetLimits(runGames, totalGames, maxRuns);
        _host.SetSchedule(_scheduleCheck.IsChecked == true, ParseTime(_startBox.Text, 10, 0), ParseTime(_stopBox.Text, 17, 0));
        _host.SetHeartbeatRequired(_heartbeatCheck.IsChecked == true);
        _host.SetStops(_ticketCheck.IsChecked == true, _goldCheck.IsChecked == true, _closeCheck.IsChecked == true, _reconnectCheck.IsChecked == true);
        _host.SetPaths(_hsPathBox.Text, _boxPathBox.Text);
        _host.ApplySettings();
        RefreshAll();
    }

    private static int ParseInt(string text, int min, int max) =>
        int.TryParse(text.Trim(), out var value) ? Math.Clamp(value, min, max) : min;

    private static DateTime ParseTime(string text, int hour, int minute) =>
        DateTime.TryParseExact(text.Trim(), "HH:mm", null, System.Globalization.DateTimeStyles.None, out var parsed)
            ? DateTime.Today.AddHours(parsed.Hour).AddMinutes(parsed.Minute)
            : DateTime.Today.AddHours(hour).AddMinutes(minute);

    private void RefreshAll()
    {
        if (_host == null) return;
        try
        {
            _chipText.Text = _host.StatusText.Length > 0 ? _host.StatusText : (_host.Running ? "运行中" : "未运行");
            _chipDot.Background = _host.Running ? SuccessBrush : TextSecondary;
            _statusText.Text = _host.StatusText.Length > 0 ? _host.StatusText : "未运行";
            _statusText.Foreground = _host.Running ? SuccessBrush : TextPrimary;
            _runtimeText.Text = _host.RuntimeText;
            _statsText.Text = _host.StatsText;
            var log = _host.LogText;
            if (_logBox != null && !_logBox.IsFocused && !string.Equals(_logBox.Text, log, StringComparison.Ordinal))
            {
                _logBox.Text = log;
                _logBox.ScrollToEnd();
            }
            _settingsState.Text = _host.PendingApply ? "设置已暂存，点击「应用设置」生效" : "设置已应用";
            _settingsState.Foreground = _host.PendingApply ? WarningText : TextSecondary;
            // 运行中「开始」不是重复启动，而是“本局结束后切换到已应用的模式”；
            // 禁用会让用户的切换请求被静默丢弃。
            _startButton.IsEnabled = !_host.Starting;
            _startButton.Content = _host.Running ? "开始 / 切换" : "开始任务";
            _stopButton.IsEnabled = _host.Running;
            if (_arenaStopButton != null) _arenaStopButton.IsEnabled = _host.ArenaStopAvailable && _host.Running;
        }
        catch { }
    }
}
