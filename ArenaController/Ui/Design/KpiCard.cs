namespace ArenaController.Ui.Design;

/// <summary>
/// Compact dashboard KPI card: small grey caption, one large value and a
/// coloured shortcut line.  Data is assigned through the properties and the
/// card repaints itself; clicking invokes <see cref="LinkAction"/>.
/// </summary>
internal sealed class KpiCard : Control
{
    private string _valueText = "";
    private string _linkText = "";
    private Color _linkColor = UiTheme.Accent;
    private Color _valueColor = UiTheme.Text;

    internal KpiCard(string title)
    {
        Title = title;
        Height = 118;
        Dock = DockStyle.Fill;
        Margin = new Padding(0);
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        Cursor = Cursors.Hand;
    }

    internal string Title { get; }

    internal string ValueText
    {
        get => _valueText;
        set { if (_valueText == value) return; _valueText = value; Invalidate(); }
    }

    internal string LinkText
    {
        get => _linkText;
        set { if (_linkText == value) return; _linkText = value; Invalidate(); }
    }

    internal Color LinkColor
    {
        get => _linkColor;
        set { _linkColor = value; Invalidate(); }
    }

    internal Color ValueColor
    {
        get => _valueColor;
        set { _valueColor = value; Invalidate(); }
    }

    internal Action? LinkAction { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var parentColor = Parent?.BackColor ?? UiTheme.Background;
        e.Graphics.Clear(parentColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;
        bounds.Width -= 1;
        bounds.Height -= 1;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = UiTheme.RoundedRect(bounds, UiTheme.RadiusLarge);
        using var fill = new SolidBrush(UiTheme.Surface);
        using var border = new Pen(UiTheme.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var caption = new SolidBrush(UiTheme.Muted);
        using var value = new SolidBrush(ValueColor);
        using var link = new SolidBrush(LinkColor);
        e.Graphics.DrawString(Title, UiTheme.BodyFont, caption, 20F, 16F);
        e.Graphics.DrawString(ValueText, UiTheme.BigFont, value, 20F, 46F);
        if (!string.IsNullOrEmpty(LinkText))
            e.Graphics.DrawString(LinkText, UiTheme.BodyFont, link, 20F, 84F);
        base.OnPaint(e);
    }

    protected override void OnClick(EventArgs e)
    {
        LinkAction?.Invoke();
        base.OnClick(e);
    }
}
