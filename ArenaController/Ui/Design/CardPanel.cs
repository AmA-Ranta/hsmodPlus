namespace ArenaController.Ui.Design;

/// <summary>
/// White rounded card with a hairline border.  Title and optional subtitle are
/// painted by the card; consumers add content through Controls and use the
/// card's padding.
/// </summary>
internal sealed class CardPanel : Panel
{
    internal CardPanel(string title, string? subtitle = null, int contentTop = 46)
    {
        Title = title;
        Subtitle = subtitle;
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        Padding = new Padding(20, contentTop, 20, 16);
        Margin = new Padding(0);
    }

    internal string Title { get; }

    internal string? Subtitle { get; }

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
        using var titleBrush = new SolidBrush(UiTheme.Text);
        using var subtitleBrush = new SolidBrush(UiTheme.Muted);
        e.Graphics.DrawString(Title, UiTheme.CardTitleFont, titleBrush, 20F, 14F);
        if (!string.IsNullOrEmpty(Subtitle))
            e.Graphics.DrawString(Subtitle, UiTheme.CaptionFont, subtitleBrush, 20F, 32F);
        base.OnPaint(e);
    }
}
