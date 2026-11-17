namespace ArenaController.Ui.Design;

/// <summary>
/// Sidebar navigation row: MDL2 glyph + label, selected state is a soft blue
/// pill with a solid accent bar on its left edge.
/// </summary>
internal sealed class NavButton : Control
{
    private bool _hover;
    private bool _selected;

    internal NavButton(string glyph, string text)
    {
        Glyph = glyph;
        Label = text;
        Height = UiTheme.NavItemHeight;
        Dock = DockStyle.Top;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        TabStop = false;
        Font = UiTheme.BodyFont;
    }

    internal string Glyph { get; }

    internal string Label { get; }

    internal bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var parentColor = Parent?.BackColor ?? UiTheme.Surface;
        e.Graphics.Clear(parentColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var pill = new Rectangle(6, 4, Math.Max(0, Width - 12), Height - 8);
        if (pill.Width <= 0 || pill.Height <= 0) return;
        if (_selected || _hover)
        {
            using var path = UiTheme.RoundedRect(pill, UiTheme.RadiusSmall);
            using var fill = new SolidBrush(_selected ? UiTheme.SurfaceRaised : UiTheme.HoverSurface);
            e.Graphics.FillPath(fill, path);
        }
        if (_selected)
        {
            using var accent = new SolidBrush(UiTheme.Accent);
            e.Graphics.FillRectangle(accent, pill.Left, pill.Top + 8, 3, pill.Height - 16);
        }
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var glyphBrush = new SolidBrush(_selected ? UiTheme.Accent : UiTheme.Faint);
        using var textBrush = new SolidBrush(_selected ? UiTheme.Text : UiTheme.Muted);
        var textHeight = TextRenderer.MeasureText(Label, UiTheme.BodyFont).Height;
        var top = Math.Max(0, (Height - textHeight) / 2);
        e.Graphics.DrawString(Glyph, UiTheme.IconFont, glyphBrush, pill.Left + 16, top + 1);
        e.Graphics.DrawString(Label, _selected ? UiTheme.SectionFont : UiTheme.BodyFont, textBrush, pill.Left + 48, top);
        base.OnPaint(e);
    }
}
