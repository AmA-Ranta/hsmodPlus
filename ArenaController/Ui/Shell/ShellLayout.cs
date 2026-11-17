using ArenaController.Ui.Design;

namespace ArenaController;

/// <summary>
/// Shell composition: white sidebar with grouped navigation, page header with
/// account status and primary actions, card based workspace and a quiet apply
/// bar.  Functional controls stay owned by MainForm; this file only arranges
/// them and refreshes their dashboard projections.
/// </summary>
internal sealed partial class MainForm
{
    private readonly Dictionary<string, Control> _shellPages = new(StringComparer.Ordinal);
    private readonly List<NavButton> _navButtons = new();
    private Panel? _shellPageHost;
    private Label? _pageTitleLabel;
    private Label? _pageSubtitleLabel;
    private BannerPanel? _banner;
    private KpiCard? _kpiInstances;
    private KpiCard? _kpiSchedule;
    private KpiCard? _kpiArena;
    private KpiCard? _kpiNotify;
    private WeekPlanControl? _weekPlan;
    private Label? _nextPill;
    private Label? _nextTime;
    private Label? _nextSub;
    private Label? _nextLead;
    private Label? _legendPlan;
    private Label? _legendSwitch;
    private readonly Label?[] _activityTime = new Label?[5];
    private readonly Label?[] _activityText = new Label?[5];
    private readonly List<(DateTime At, string Text)> _recentEvents = new();

    private static readonly string[] NavKeys = { "dashboard", "instances", "plan", "arena", "notify", "support" };

    private static readonly Dictionary<string, (string Title, string Subtitle)> PageMeta = new()
    {
        ["dashboard"] = ("总览", "当前账号的运行状态与待办"),
        ["instances"] = ("账号与实例", "战网、炉石、盒子与插件的实例状态"),
        ["plan"] = ("运行计划", "五模式、卡组、额度与每日计划"),
        ["arena"] = ("竞技场", "竞技场类型、门票金币保护与轮次边界"),
        ["notify"] = ("通知推送", "运行日志与最近活动"),
        ["support"] = ("维护与支持", "插件安装、路径配置与纯净还原")
    };

    private void BuildShell()
    {
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.Text;
        Font = UiTheme.BodyFont;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiTheme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiTheme.SidebarWidth));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildSidebar(), 0, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.HeaderHeight));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.ActionBarHeight));
        main.Controls.Add(BuildHeader(), 0, 0);

        _shellPageHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background, Padding = new Padding(24, 4, 24, 8) };
        BuildShellPages();
        main.Controls.Add(_shellPageHost, 0, 1);
        main.Controls.Add(BuildActionBar(), 0, 2);
        root.Controls.Add(main, 1, 0);

        Controls.Add(root);
        SelectShellPage("dashboard");
        ApplyShellTheme();
    }

    // ---------------------------------------------------------------- sidebar

    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Border);
            e.Graphics.DrawLine(pen, sidebar.ClientSize.Width - 1, 0, sidebar.ClientSize.Width - 1, sidebar.ClientSize.Height);
        };

        var rail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 12,
            BackColor = UiTheme.Surface,
            Padding = new Padding(12, 12, 12, 10),
            Margin = new Padding(0)
        };
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.NavItemHeight));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.NavItemHeight));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.NavItemHeight));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.NavItemHeight));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.NavItemHeight));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, UiTheme.NavItemHeight));
        rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rail.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        rail.Controls.Add(BuildBrandPanel(), 0, 0);

        var sections = new (string Caption, (string Key, string Glyph, string Label)[] Items)[]
        {
            ("工作区", new[]
            {
                ("dashboard", "\uE80F", "总览"),
                ("instances", "\uE77B", "账号与实例")
            }),
            ("账号功能", new[]
            {
                ("plan", "\uE787", "运行计划"),
                ("arena", "\uE7C1", "竞技场"),
                ("notify", "\uEA8F", "通知推送")
            }),
            ("应用", new[]
            {
                ("support", "\uE713", "维护与支持")
            })
        };
        var row = 1;
        foreach (var section in sections)
        {
            rail.Controls.Add(new Label
            {
                Text = section.Caption,
                Dock = DockStyle.Fill,
                ForeColor = UiTheme.Faint,
                Font = UiTheme.CaptionFont,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                Margin = new Padding(0)
            }, 0, row++);
            foreach (var item in section.Items)
            {
                var button = new NavButton(item.Glyph, item.Label) { Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 1) };
                button.Click += (_, _) => SelectShellPage(item.Key);
                _navButtons.Add(button);
                rail.Controls.Add(button, 0, row++);
            }
        }

        rail.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = new Padding(0) }, 0, row++);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Surface, Margin = new Padding(0) };
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        footer.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Border, Height = 1, Margin = new Padding(0) }, 0, 0);
        footer.Controls.Add(new Label { Text = "v1031 · 插件独立", ForeColor = UiTheme.Muted, Font = UiTheme.CaptionFont, AutoSize = true, Margin = new Padding(10, 6, 0, 0) }, 0, 1);
        footer.Controls.Add(new Label { Text = "作者：一天洗三次澡", ForeColor = UiTheme.Faint, Font = UiTheme.CaptionFont, AutoSize = true, Margin = new Padding(10, 0, 0, 0) }, 0, 2);
        rail.Controls.Add(footer, 0, row);
        sidebar.Controls.Add(rail);
        return sidebar;
    }

    private Control BuildBrandPanel()
    {
        var brand = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = new Padding(0) };
        brand.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var icon = new Rectangle(12, 12, 48, 48);
            using (var path = UiTheme.RoundedRect(icon, 12))
            using (var fill = new SolidBrush(UiTheme.Accent))
                e.Graphics.FillPath(fill, path);
            using (var letter = new SolidBrush(Color.White))
            using (var letterFont = new Font("Segoe UI", 18F, FontStyle.Bold, GraphicsUnit.Point))
                e.Graphics.DrawString("H", letterFont, letter, icon.X + 14, icon.Y + 8);
            using var title = new SolidBrush(UiTheme.Text);
            using var sub = new SolidBrush(UiTheme.Muted);
            e.Graphics.DrawString("HsMod 中控", UiTheme.BrandFont, title, 72F, 16F);
            e.Graphics.DrawString("桌面版 · 新方案", UiTheme.CaptionFont, sub, 73F, 40F);
        };
        return brand;
    }

    // ----------------------------------------------------------------- header

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background, Margin = new Padding(0) };

        _pageTitleLabel = new Label
        {
            Text = "总览",
            Font = UiTheme.PageTitleFont,
            ForeColor = UiTheme.Text,
            AutoSize = true,
            Location = new Point(28, 16),
            BackColor = UiTheme.Background
        };
        _pageSubtitleLabel = new Label
        {
            Text = "当前账号的运行状态与待办",
            Font = UiTheme.BodyFont,
            ForeColor = UiTheme.Muted,
            AutoSize = true,
            Location = new Point(28, 48),
            BackColor = UiTheme.Background
        };
        header.Controls.Add(_pageTitleLabel);
        header.Controls.Add(_pageSubtitleLabel);

        var cluster = new TableLayoutPanel
        {
            Dock = DockStyle.None,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = UiTheme.Background,
            Height = 44,
            Width = 596,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        cluster.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        cluster.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        cluster.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        cluster.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cluster.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        cluster.Controls.Add(new Label
        {
            Text = "当前账号",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.CaptionFont,
            Margin = new Padding(0)
        }, 0, 0);

        var chip = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = new Padding(0, 0, 10, 0) };
        chip.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = UiTheme.RoundedRect(new Rectangle(0, 0, chip.ClientSize.Width - 1, chip.ClientSize.Height - 1), UiTheme.RadiusSmall);
            using var fill = new SolidBrush(UiTheme.Surface);
            using var border = new Pen(UiTheme.BorderStrong);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            var dot = new Rectangle(14, Math.Max(0, (chip.ClientSize.Height - 10) / 2), 10, 10);
            using var dotFill = new SolidBrush(UiTheme.Success);
            e.Graphics.FillEllipse(dotFill, dot);
        };
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.Padding = new Padding(34, 0, 28, 0);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = UiTheme.BodyFont;
        _status.ForeColor = UiTheme.Text;
        _status.BackColor = UiTheme.Surface;
        _status.Text = "本机 · 已连接";
        chip.Controls.Add(_status);
        cluster.Controls.Add(chip, 1, 0);

        _start.Text = "开始任务";
        _start.Dock = DockStyle.Fill;
        _start.Margin = new Padding(0, 0, 8, 0);
        _start.Click += async (_, _) => await StartTaskAsync("手动开始");
        cluster.Controls.Add(_start, 2, 0);

        _stop.Text = "停止并等待本局";
        _stop.Dock = DockStyle.Fill;
        _stop.Margin = new Padding(0);
        _stop.Click += (_, _) => RequestStop("手动停止");
        cluster.Controls.Add(_stop, 3, 0);

        header.Controls.Add(cluster);
        void Position() => cluster.Location = new Point(Math.Max(24, header.ClientSize.Width - cluster.Width - 24), Math.Max(8, (header.ClientSize.Height - cluster.Height) / 2));
        header.Resize += (_, _) => Position();
        header.Layout += (_, _) => Position();
        return header;
    }

    // ------------------------------------------------------------------- pages

    private void BuildShellPages()
    {
        if (_shellPageHost == null) return;
        _shellPages["dashboard"] = BuildDashboardPage();
        _shellPages["instances"] = BuildInstancesPage();
        _shellPages["plan"] = BuildPlanPage();
        _shellPages["arena"] = BuildArenaPage();
        _shellPages["notify"] = BuildNotifyPage();
        _shellPages["support"] = BuildSupportPage();
        foreach (var page in _shellPages.Values)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _shellPageHost.Controls.Add(page);
        }
    }

    private void SelectShellPage(string key)
    {
        foreach (var pair in _shellPages) pair.Value.Visible = pair.Key == key;
        for (var i = 0; i < NavKeys.Length && i < _navButtons.Count; i++)
            _navButtons[i].Selected = NavKeys[i] == key;
        var meta = PageMeta.TryGetValue(key, out var m) ? m : ("总览", "");
        if (_pageTitleLabel != null) _pageTitleLabel.Text = meta.Item1;
        if (_pageSubtitleLabel != null) _pageSubtitleLabel.Text = meta.Item2;
    }

    private Control BuildDashboardPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = UiTheme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 192));

        _banner = new BannerPanel { Dock = DockStyle.Fill, Message = "正在检查运行环境…", Margin = new Padding(0, 0, 0, 14) };
        _banner.LinkAction = () => SelectShellPage("support");
        page.Controls.Add(_banner, 0, 0);

        var kpis = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = UiTheme.Background, Margin = new Padding(0, 0, 0, 14) };
        for (var i = 0; i < 4; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        _kpiInstances = new KpiCard("游戏实例") { Margin = new Padding(0, 0, 12, 0), LinkAction = () => SelectShellPage("instances") };
        _kpiSchedule = new KpiCard("时间表") { Margin = new Padding(0, 0, 12, 0), LinkAction = () => SelectShellPage("plan") };
        _kpiArena = new KpiCard("竞技场") { Margin = new Padding(0, 0, 12, 0), LinkAction = () => SelectShellPage("arena") };
        _kpiNotify = new KpiCard("通知推送") { LinkAction = () => SelectShellPage("notify") };
        kpis.Controls.Add(_kpiInstances, 0, 0);
        kpis.Controls.Add(_kpiSchedule, 1, 0);
        kpis.Controls.Add(_kpiArena, 2, 0);
        kpis.Controls.Add(_kpiNotify, 3, 0);
        page.Controls.Add(kpis, 0, 1);

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Background, Margin = new Padding(0, 0, 0, 14) };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        middle.Controls.Add(BuildWeekPlanCard(), 0, 0);
        middle.Controls.Add(BuildNextCard(), 1, 0);
        page.Controls.Add(middle, 0, 2);

        page.Controls.Add(BuildActivityCard(), 0, 3);
        return page;
    }

    private Control BuildWeekPlanCard()
    {
        var card = new CardPanel("本周运行计划", null) { Margin = new Padding(0, 0, 14, 0) };
        var link = new Label
        {
            Text = "编辑时间表 →",
            ForeColor = UiTheme.Accent,
            Font = UiTheme.BodyFont,
            AutoSize = true,
            Cursor = Cursors.Hand,
            BackColor = UiTheme.Surface
        };
        link.Click += (_, _) => SelectShellPage("plan");
        card.Controls.Add(link);
        link.Location = new Point(Math.Max(20, card.Width - link.Width - 20), 14);
        card.Resize += (_, _) => link.Location = new Point(Math.Max(20, card.Width - link.Width - 20), 14);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiTheme.Surface, Margin = new Padding(0), Padding = new Padding(0) };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _weekPlan = new WeekPlanControl { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        body.Controls.Add(_weekPlan, 0, 0);
        var legend = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Surface, Margin = new Padding(0), Padding = new Padding(0) };
        _legendPlan = new Label { AutoSize = true, ForeColor = UiTheme.Accent, Font = UiTheme.CaptionFont, Text = "● 计划 20:00–23:00", Margin = new Padding(0, 0, 24, 0) };
        _legendSwitch = new Label { AutoSize = true, ForeColor = UiTheme.Muted, Font = UiTheme.CaptionFont, Text = "● 自动启停 未启用", Margin = new Padding(0) };
        legend.Controls.Add(_legendPlan);
        legend.Controls.Add(_legendSwitch);
        body.Controls.Add(legend, 0, 1);
        card.Controls.Add(body);
        return card;
    }

    private Control BuildNextCard()
    {
        var card = new CardPanel("接下来", null) { Margin = new Padding(0) };
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = UiTheme.Surface, Margin = new Padding(0), Padding = new Padding(0) };
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var pillHost = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface, Margin = new Padding(0, 0, 0, 4) };
        pillHost.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = UiTheme.RoundedRect(new Rectangle(0, 0, Math.Min(210, pillHost.ClientSize.Width), pillHost.ClientSize.Height), UiTheme.RadiusSmall);
            using var fill = new SolidBrush(UiTheme.SuccessSoft);
            e.Graphics.FillPath(fill, path);
        };
        _nextPill = new Label { Dock = DockStyle.Fill, Text = "● 自动启停已开启", ForeColor = UiTheme.Success, Font = UiTheme.CaptionFont, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), BackColor = Color.Transparent };
        pillHost.Controls.Add(_nextPill);
        stack.Controls.Add(pillHost, 0, 0);

        _nextTime = new Label { Dock = DockStyle.Fill, Text = "今天 20:00", Font = UiTheme.BigFont, ForeColor = UiTheme.Text, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
        stack.Controls.Add(_nextTime, 0, 1);
        _nextSub = new Label { Dock = DockStyle.Fill, Text = "标准 · 下一运行时段", Font = UiTheme.BodyFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
        stack.Controls.Add(_nextSub, 0, 2);
        stack.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Border, Height = 1, Margin = new Padding(0, 8, 0, 8) }, 0, 3);
        _nextLead = new Label { Dock = DockStyle.Fill, Text = "到点自动开始", Font = UiTheme.CaptionFont, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
        stack.Controls.Add(_nextLead, 0, 4);

        var openPlan = new Button { Text = "查看运行计划", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = UiTheme.Surface, ForeColor = UiTheme.Accent, Font = UiTheme.BodyFont, Margin = new Padding(0, 6, 0, 0) };
        openPlan.FlatAppearance.BorderColor = UiTheme.Accent;
        openPlan.FlatAppearance.MouseOverBackColor = UiTheme.AccentSoft;
        openPlan.Click += (_, _) => SelectShellPage("plan");
        stack.Controls.Add(openPlan, 0, 5);
        card.Controls.Add(stack);
        return card;
    }

    private Control BuildActivityCard()
    {
        var card = new CardPanel("最近活动", null) { Margin = new Padding(0) };
        var all = new Label { Text = "全部活动 →", ForeColor = UiTheme.Accent, Font = UiTheme.BodyFont, AutoSize = true, Cursor = Cursors.Hand, BackColor = UiTheme.Surface };
        all.Click += (_, _) => SelectShellPage("notify");
        card.Controls.Add(all);
        all.Location = new Point(Math.Max(20, card.Width - all.Width - 20), 14);
        card.Resize += (_, _) => all.Location = new Point(Math.Max(20, card.Width - all.Width - 20), 14);

        var list = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = UiTheme.Surface, Margin = new Padding(0), Padding = new Padding(0) };
        for (var i = 0; i < 5; i++)
        {
            list.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = UiTheme.Surface, Margin = new Padding(0), Padding = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var dot = new Label { Text = "●", ForeColor = UiTheme.Success, Font = UiTheme.CaptionFont, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
            _activityTime[i] = new Label { Text = "", ForeColor = UiTheme.Faint, Font = UiTheme.CaptionFont, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
            _activityText[i] = new Label { Text = "", ForeColor = UiTheme.Text, Font = UiTheme.BodyFont, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new Padding(0) };
            row.Controls.Add(dot, 0, 0);
            row.Controls.Add(_activityTime[i], 1, 0);
            row.Controls.Add(_activityText[i], 2, 0);
            list.Controls.Add(row, 0, i);
        }
        card.Controls.Add(list);
        return card;
    }

    private Control BuildInstancesPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Background, Margin = new Padding(0), Padding = new Padding(0) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));

        var runtimeCard = new CardPanel("实例状态", "战网 / 炉石 / 盒子 / 插件 / 心跳") { Margin = new Padding(0, 0, 14, 0) };
        _runtime.AutoSize = false;
        _runtime.Dock = DockStyle.Fill;
        _runtime.Font = UiTheme.BodyFont;
        _runtime.ForeColor = UiTheme.Text;
        _runtime.BackColor = UiTheme.Surface;
        _runtime.TextAlign = ContentAlignment.TopLeft;
        _runtime.Margin = new Padding(0);
        runtimeCard.Controls.Add(_runtime);
        page.Controls.Add(runtimeCard, 0, 0);

        var statsCard = new CardPanel("对局统计", "当前模式与竞技场累计") { Margin = new Padding(0) };
        _stats.AutoSize = false;
        _stats.Dock = DockStyle.Fill;
        _stats.Font = UiTheme.BodyFont;
        _stats.ForeColor = UiTheme.Text;
        _stats.BackColor = UiTheme.Surface;
        _stats.TextAlign = ContentAlignment.TopLeft;
        _stats.Margin = new Padding(0);
        statsCard.Controls.Add(_stats);
        page.Controls.Add(statsCard, 1, 0);
        return page;
    }

    private Control BuildPlanPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Background, Margin = new Padding(0), Padding = new Padding(0) };
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 26));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 26));

        var taskCard = new CardPanel("任务与额度", "模式、卡组与运行额度；修改后点击「应用设置」") { Margin = new Padding(0, 0, 0, 12) };
        var taskGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4, Margin = new Padding(0), Padding = new Padding(0), BackColor = UiTheme.Surface };
        taskGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        taskGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        taskGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        taskGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        for (var i = 0; i < 4; i++) taskGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        _mode.Items.Clear();
        _mode.Items.AddRange(new object[] { "标准", "狂野", "休闲", "酒馆战棋", "竞技场" });
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Dock = DockStyle.Fill;
        _mode.SelectedIndexChanged += (_, _) => UpdateModeUi();
        AddControlRow(taskGrid, 0, "模式", _mode, 1, 3);
        _deckName.DropDownStyle = ComboBoxStyle.DropDown;
        _deckName.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _deckName.AutoCompleteSource = AutoCompleteSource.ListItems;
        _deckName.Dock = DockStyle.Fill;
        _deckName.Margin = new Padding(3, 6, 3, 6);
        AddControlRow(taskGrid, 1, "卡组名称", _deckName, 1, 3);
        _deckName.Enter += async (_, _) => await RefreshDeckSuggestionsAsync();
        _deckName.DropDownClosed += (_, _) => BeginInvoke(new Action(() => _ = RefreshDeckSuggestionsAsync()));
        ConfigureNumber(_runGames, 0, 1000);
        ConfigureNumber(_totalGames, 0, 100000);
        ConfigureNumber(_maxRuns, 0, 10000);
        AddControlRow(taskGrid, 2, "每轮对局", _runGames, 1, 1);
        AddControlRow(taskGrid, 2, "任务总局", _totalGames, 3, 1);
        AddControlRow(taskGrid, 3, "最多轮数", _maxRuns, 1, 1);
        _heartbeatRequired.Text = "启动前等待盒子心跳";
        _heartbeatRequired.AutoSize = true;
        taskGrid.Controls.Add(_heartbeatRequired, 3, 3);
        taskCard.Controls.Add(taskGrid);
        page.Controls.Add(taskCard, 0, 0);

        var scheduleCard = new CardPanel("每日计划", "按时间段自动开始与停止") { Margin = new Padding(0, 0, 0, 12) };
        var scheduleGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0), BackColor = UiTheme.Surface };
        scheduleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16));
        scheduleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        scheduleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        scheduleGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        scheduleGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        scheduleGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _scheduleEnabled.Text = "按计划自动运行";
        _scheduleEnabled.Dock = DockStyle.Fill;
        _scheduleEnabled.TextAlign = ContentAlignment.MiddleLeft;
        _startTime.Format = DateTimePickerFormat.Custom;
        _startTime.CustomFormat = "HH:mm";
        _startTime.ShowUpDown = true;
        _startTime.Dock = DockStyle.Fill;
        _startTime.Margin = new Padding(3, 2, 3, 2);
        _stopTime.Format = DateTimePickerFormat.Custom;
        _stopTime.CustomFormat = "HH:mm";
        _stopTime.ShowUpDown = true;
        _stopTime.Dock = DockStyle.Fill;
        _stopTime.Margin = new Padding(3, 2, 3, 2);
        scheduleGrid.Controls.Add(_scheduleEnabled, 0, 0);
        scheduleGrid.SetColumnSpan(_scheduleEnabled, 4);
        scheduleGrid.Controls.Add(new Label { Text = "开始时间", Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        scheduleGrid.Controls.Add(_startTime, 1, 1);
        scheduleGrid.Controls.Add(new Label { Text = "停止时间", Dock = DockStyle.Fill, ForeColor = UiTheme.Muted, TextAlign = ContentAlignment.MiddleLeft }, 2, 1);
        scheduleGrid.Controls.Add(_stopTime, 3, 1);
        scheduleCard.Controls.Add(scheduleGrid);
        page.Controls.Add(scheduleCard, 0, 1);

        var stopCard = new CardPanel("停止策略", "资源保护、自动恢复与收尾行为") { Margin = new Padding(0) };
        var stopGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0), BackColor = UiTheme.Surface };
        stopGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        stopGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        stopGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        stopGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _ticketStop.Text = "门票不足时停止";
        _ticketStop.Dock = DockStyle.Fill;
        _ticketStop.TextAlign = ContentAlignment.MiddleLeft;
        _goldStop.Text = "金币不足时停止";
        _goldStop.Dock = DockStyle.Fill;
        _goldStop.TextAlign = ContentAlignment.MiddleLeft;
        _closeGame.Text = "停止后关闭炉石";
        _closeGame.Dock = DockStyle.Fill;
        _closeGame.TextAlign = ContentAlignment.MiddleLeft;
        _autoReconnect.Text = "掉线 / 卡住自动恢复（战网在线时）";
        _autoReconnect.Dock = DockStyle.Fill;
        _autoReconnect.TextAlign = ContentAlignment.MiddleLeft;
        stopGrid.Controls.Add(_ticketStop, 0, 0);
        stopGrid.Controls.Add(_goldStop, 1, 0);
        stopGrid.Controls.Add(_closeGame, 0, 1);
        stopGrid.Controls.Add(_autoReconnect, 1, 1);
        stopCard.Controls.Add(stopGrid);
        page.Controls.Add(stopCard, 0, 2);
        return page;
    }

    private Control BuildArenaPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = UiTheme.Background, Margin = new Padding(0), Padding = new Padding(0) };
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var card = new CardPanel("竞技场设置", "普通 / 地下、门票金币保护与轮次边界") { Margin = new Padding(0, 0, 0, 12) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0), Padding = new Padding(0), BackColor = UiTheme.Surface };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(new Label { Text = "竞技场类型", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.Muted, Font = UiTheme.BodyFont }, 0, 0);
        _arenaMode.Items.Clear();
        _arenaMode.Items.AddRange(new object[] { "普通竞技场", "地下竞技场" });
        _arenaMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _arenaMode.Dock = DockStyle.Fill;
        _arenaMode.Margin = new Padding(3, 6, 3, 6);
        grid.Controls.Add(_arenaMode, 1, 0);
        var note = new Label { Text = "门票 / 金币不足时停止；本轮结束后可单独停止，不会开启下一轮。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.Muted, Font = UiTheme.CaptionFont };
        grid.Controls.Add(note, 0, 1);
        grid.SetColumnSpan(note, 2);

        var arenaActionGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0), BackColor = UiTheme.Surface };
        arenaActionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        arenaActionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        ConfigureArenaRunStopButton(arenaActionGrid);
        arenaActionGrid.RowStyles.Clear();
        arenaActionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        arenaActionGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        grid.Controls.Add(arenaActionGrid, 0, 2);
        grid.SetColumnSpan(arenaActionGrid, 2);
        card.Controls.Add(grid);
        page.Controls.Add(card, 0, 0);
        page.Controls.Add(new Label
        {
            Text = "提示：竞技场的「本轮结束后停止」只在本轮全部对局与领奖完成后生效。",
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Faint,
            Font = UiTheme.CaptionFont,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        return page;
    }

    private Control BuildNotifyPage()
    {
        var card = new CardPanel("运行日志", "启动、停止、异常与恢复都会记录在这里") { Margin = new Padding(0) };
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = UiTheme.SurfaceAlt;
        _log.ForeColor = UiTheme.Text;
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _log.Margin = new Padding(0);
        card.Controls.Add(_log);
        return card;
    }

    private Control BuildSupportPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = UiTheme.Background, Margin = new Padding(0), Padding = new Padding(0) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var pathsCard = new CardPanel("安装与维护", "炉石、盒子与内置插件源的位置") { Margin = new Padding(0, 0, 14, 0) };
        var pathsGrid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, RowCount = 3, Height = 168, Padding = new Padding(0), Margin = new Padding(0), BackColor = UiTheme.Surface };
        pathsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        pathsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        for (var row = 0; row < 3; row++) pathsGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        AddPathRow(pathsGrid, 0, "炉石位置", () => _settings.HearthstoneDirectory, v => _settings.HearthstoneDirectory = v);
        AddPathRow(pathsGrid, 1, "盒子位置", () => _settings.BoxExecutablePath, v => _settings.BoxExecutablePath = v);
        AddReadOnlyPathRow(pathsGrid, 2, "内置插件源", () => Path.Combine(AppContext.BaseDirectory, "ArenaBoxBot.dll"));
        pathsCard.Controls.Add(pathsGrid);

        _install.Text = "安装 / 修复插件";
        _install.AutoSize = false;
        _install.Height = 36;
        _install.Width = 140;
        _install.Margin = new Padding(0, 0, 8, 0);
        _install.Click += (_, _) => InstallPlugin();
        var restore = new Button { Text = "还原纯净原版", FlatStyle = FlatStyle.Flat, Width = 130, Height = 36, Margin = new Padding(0, 0, 8, 0) };
        restore.Click += (_, _) => RestoreOriginal();
        var detect = new Button { Text = "重新探测路径", FlatStyle = FlatStyle.Flat, Width = 130, Height = 36, Margin = new Padding(0) };
        detect.Click += (_, _) => { DetectInstallPaths(); RefreshPathBoxes(); Append("已重新探测安装路径。"); };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = UiTheme.Surface, Height = 44, Padding = new Padding(0, 8, 0, 0) };
        actions.Controls.Add(_install);
        actions.Controls.Add(restore);
        actions.Controls.Add(detect);
        pathsCard.Controls.Add(actions);

        var aboutCard = new CardPanel("关于", null) { Margin = new Padding(0) };
        var about = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted,
            Font = UiTheme.BodyFont,
            TextAlign = ContentAlignment.TopLeft,
            Text = "hsmodPlus · 1031 更新\r\n\r\n中控只负责安装、启动与监控；游戏内自动化由独立插件 ArenaBoxBot 完成。\r\n\r\n插件源：ArenaBoxBot.dll\r\n安装清单：BepInEx\\HsMod\\native-arena-install.json\r\n\r\n安装前请退出炉石与盒子；还原会按清单删除加载器文件并恢复备份。"
        };
        aboutCard.Controls.Add(about);
        page.Controls.Add(pathsCard, 0, 0);
        page.Controls.Add(aboutCard, 1, 0);
        return page;
    }

    private Control BuildActionBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Background, Padding = new Padding(24, 8, 24, 10), Margin = new Padding(0) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = UiTheme.Background, Margin = new Padding(0), Padding = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
        _settingsState.AutoSize = false;
        _settingsState.Dock = DockStyle.Fill;
        _settingsState.TextAlign = ContentAlignment.MiddleLeft;
        _settingsState.ForeColor = UiTheme.Muted;
        _settingsState.Font = UiTheme.CaptionFont;
        _settingsState.Text = "设置已应用";
        _settingsState.Margin = new Padding(0);
        _applySettings.Text = "应用设置";
        _applySettings.AutoSize = false;
        _applySettings.Dock = DockStyle.Fill;
        _applySettings.Margin = new Padding(0, 2, 0, 2);
        _applySettings.Click += (_, _) => ApplyStagedSettings();
        grid.Controls.Add(_settingsState, 0, 0);
        grid.Controls.Add(_applySettings, 1, 0);
        bar.Controls.Add(grid);
        return bar;
    }

    // --------------------------------------------------------------- dashboard

    private void RefreshDashboard()
    {
        if (_kpiInstances == null) return;
        var game = IsHearthstoneRunning();
        var box = IsBoxRunning();
        var api = _hsModStatus != null && _hsModStatus.Ready && !_hsModProbeUnavailable;
        var online = (game ? 1 : 0) + (box ? 1 : 0) + (api ? 1 : 0);
        _kpiInstances.ValueText = online > 0 ? online + " 个在线" : "全部离线";
        _kpiInstances.ValueColor = online > 0 ? UiTheme.Text : UiTheme.Muted;
        _kpiInstances.LinkText = "查看实例 →";

        var scheduleOn = _settings.AutoSchedule;
        _kpiSchedule.ValueText = scheduleOn ? "已启用" : "未启用";
        _kpiSchedule.ValueColor = scheduleOn ? UiTheme.Success : UiTheme.Muted;
        var startText = string.IsNullOrWhiteSpace(_settings.StartTime) ? "20:00" : _settings.StartTime;
        _kpiSchedule.LinkText = "下一时段 " + startText + " →";

        var arenaOn = string.Equals(_settings.Mode, "竞技场", StringComparison.Ordinal);
        _kpiArena.ValueText = arenaOn ? (_running ? "进行中" : "空闲") : "未启用";
        _kpiArena.ValueColor = arenaOn ? UiTheme.Text : UiTheme.Muted;
        _kpiArena.LinkText = "运行与诊断 →";

        _kpiNotify.ValueText = "已配置";
        _kpiNotify.LinkText = "查看日志 →";

        if (_banner != null)
        {
            var pending = new List<string>();
            if (string.IsNullOrWhiteSpace(_settings.HearthstoneDirectory) || !Directory.Exists(_settings.HearthstoneDirectory)) pending.Add("炉石路径未配置");
            if (string.IsNullOrWhiteSpace(_settings.BoxExecutablePath) || !File.Exists(_settings.BoxExecutablePath)) pending.Add("盒子路径未配置");
            if (!box) pending.Add("盒子未运行");
            if (!api && game) pending.Add("插件状态不可读");
            _banner.Message = pending.Count == 0 ? "所有检查项正常" : pending.Count + " 项待处理：" + string.Join(" · ", pending);
        }

        if (_nextPill != null && _nextTime != null && _nextSub != null && _nextLead != null)
        {
            _nextPill.Text = scheduleOn ? "● 自动启停已开启" : "○ 自动启停未开启";
            _nextPill.ForeColor = scheduleOn ? UiTheme.Success : UiTheme.Muted;
            var modeText = string.IsNullOrWhiteSpace(_settings.Mode) ? "标准" : _settings.Mode;
            var startTime = ParseTime(_settings.StartTime, 20, 0);
            var now = DateTime.Now;
            var next = now.Date.AddHours(startTime.Hour).AddMinutes(startTime.Minute);
            if (next <= now) next = next.AddDays(1);
            var dayText = (next.Date - now.Date).Days == 0 ? "今天" : (next.Date - now.Date).Days == 1 ? "明天" : next.ToString("MM-dd");
            _nextTime.Text = dayText + " " + next.ToString("HH:mm");
            _nextSub.Text = modeText + " · 下一运行时段";
            _nextLead.Text = scheduleOn ? "到点自动开始" : "启用每日计划后自动开始";
        }

        if (_weekPlan != null)
        {
            var start = ParseTime(_settings.StartTime, 20, 0);
            var stop = ParseTime(_settings.StopTime, 23, 0);
            _weekPlan.UpdatePlan(scheduleOn,
                TimeSpan.FromHours(start.Hour) + TimeSpan.FromMinutes(start.Minute),
                TimeSpan.FromHours(stop.Hour) + TimeSpan.FromMinutes(stop.Minute));
            if (_legendPlan != null) _legendPlan.Text = "● 计划 " + start.ToString("HH:mm") + "–" + stop.ToString("HH:mm");
            if (_legendSwitch != null)
            {
                _legendSwitch.Text = "● 自动启停 " + (scheduleOn ? "已启用" : "未启用");
                _legendSwitch.ForeColor = scheduleOn ? UiTheme.Success : UiTheme.Muted;
            }
        }

        for (var i = 0; i < _activityTime.Length; i++)
        {
            var label = _activityText[i];
            var time = _activityTime[i];
            if (label == null || time == null) continue;
            var index = _recentEvents.Count - 1 - i;
            if (index >= 0)
            {
                var evt = _recentEvents[index];
                time.Text = evt.At.ToString("HH:mm");
                label.Text = evt.Text;
            }
            else
            {
                time.Text = "";
                label.Text = _recentEvents.Count == 0 && i == 0 ? "暂无活动记录，任务开始后会显示在这里" : "";
            }
        }
    }

    private void AddRecentEvent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var trimmed = text.Trim();
        if (trimmed.Length > 80) trimmed = trimmed[..80] + "…";
        if (_recentEvents.Count > 0 && _recentEvents[^1].Text == trimmed) return;
        _recentEvents.Add((DateTime.Now, trimmed));
        if (_recentEvents.Count > 30) _recentEvents.RemoveAt(0);
    }

    // ------------------------------------------------------------------- theme

    private void ApplyShellTheme()
    {
        StyleActionButtons();
        _start.Font = UiTheme.SectionFont;
        _stop.Font = UiTheme.BodyFont;
        foreach (var combo in new[] { _mode, _deckName, _arenaMode })
        {
            combo.BackColor = UiTheme.SurfaceAlt;
            combo.ForeColor = UiTheme.Text;
            combo.FlatStyle = FlatStyle.Flat;
            combo.Font = UiTheme.BodyFont;
        }
        foreach (var number in new[] { _runGames, _totalGames, _maxRuns })
        {
            number.BackColor = UiTheme.SurfaceAlt;
            number.ForeColor = UiTheme.Text;
            number.BorderStyle = BorderStyle.FixedSingle;
        }
        foreach (var check in new[] { _heartbeatRequired, _scheduleEnabled, _ticketStop, _goldStop, _closeGame, _autoReconnect })
        {
            check.ForeColor = UiTheme.Text;
            check.BackColor = UiTheme.Surface;
            check.Font = UiTheme.BodyFont;
        }
        foreach (var picker in new[] { _startTime, _stopTime })
        {
            picker.BackColor = UiTheme.SurfaceAlt;
            picker.ForeColor = UiTheme.Text;
            picker.Font = UiTheme.BodyFont;
        }
        _applySettings.BackColor = UiTheme.Accent;
        _applySettings.ForeColor = Color.White;
        _applySettings.FlatStyle = FlatStyle.Flat;
        _applySettings.FlatAppearance.BorderSize = 0;
        _applySettings.FlatAppearance.MouseOverBackColor = UiTheme.AccentHover;
        _applySettings.Font = UiTheme.SectionFont;
        ThemeAll(this);
    }

    /// <summary>
    /// Themes the plain controls created inline by page builders without
    /// touching the deliberately coloured primary actions.
    /// </summary>
    private void ThemeAll(Control root)
    {
        foreach (Control child in root.Controls)
        {
            switch (child)
            {
                case Label label when label.ForeColor == Control.DefaultForeColor:
                    label.ForeColor = UiTheme.Text;
                    break;
                case TextBox text when !ReferenceEquals(text, _log):
                    text.BackColor = UiTheme.SurfaceAlt;
                    text.ForeColor = UiTheme.Text;
                    text.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case Button button when button != _start && button != _stop && button != _install &&
                    button != _applySettings && button != _stopAfterRun:
                    button.BackColor = UiTheme.Surface;
                    button.ForeColor = UiTheme.Text;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = UiTheme.BorderStrong;
                    button.FlatAppearance.MouseOverBackColor = UiTheme.HoverSurface;
                    break;
            }
            ThemeAll(child);
        }
    }
}
