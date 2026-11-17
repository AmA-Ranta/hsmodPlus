namespace ArenaController.Ui.Design;

/// <summary>
/// Amber attention banner shown above the dashboard KPI cards: round icon,
/// message text and a right-aligned shortcut link.
/// </summary>
internal sealed class BannerPanel : Panel
{
    internal BannerPanel()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Background;
        Margin = new Padding(0);
        Padding = new Padding(56, 0, 20, 0);
    }

    internal string Message { get; set; } = "";

    internal string LinkText { get; set; } = "去处理 →";

    internal Action? LinkAction { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var parentColor = Parent?.BackColor ?? UiTheme.Background;
        e.Graphics.Clear(parentColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var bounds = ClientRectangle;
        bounds.Width -= 1;
        bounds.Height -= 1;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = UiTheme.RoundedRect(bounds, UiTheme.RadiusLarge);
        using var fill = new SolidBrush(UiTheme.WarningSoft);
        using var border = new Pen(UiTheme.WarningBorder);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        var icon = new Rectangle(18, (Height - 26) / 2, 26, 26);
        using var iconFill = new SolidBrush(UiTheme.Warning);
        e.Graphics.FillEllipse(iconFill, icon);
        using var bang = new SolidBrush(Color.White);
        using var bangFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        e.Graphics.DrawString("!", bangFont, bang, icon.X + 10, icon.Y + 4);

        using var text = new SolidBrush(UiTheme.Text);
        var messageSize = e.Graphics.MeasureString(Message, UiTheme.BodyFont);
        e.Graphics.DrawString(Message, UiTheme.BodyFont, text, 58F, (Height - messageSize.Height) / 2);

        using var link = new SolidBrush(UiTheme.Accent);
        var linkSize = e.Graphics.MeasureString(LinkText, UiTheme.BodyFont);
        e.Graphics.DrawString(LinkText, UiTheme.BodyFont, link, bounds.Width - linkSize.Width - 18,
            (Height - linkSize.Height) / 2);
    }

    protected override void OnClick(EventArgs e)
    {
        LinkAction?.Invoke();
        base.OnClick(e);
    }
}
