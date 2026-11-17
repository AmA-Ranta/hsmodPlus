namespace ArenaController.Ui.Design;

/// <summary>
/// Weekly plan strip: weekday header plus one rounded bar per day showing the
/// configured run window.  Hours map linearly across each day column.
/// </summary>
internal sealed class WeekPlanControl : Control
{
    private bool _enabled;
    private TimeSpan _start = TimeSpan.FromHours(20);
    private TimeSpan _stop = TimeSpan.FromHours(23);

    internal WeekPlanControl()
    {
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        Dock = DockStyle.Fill;
        Margin = new Padding(0);
    }

    internal void UpdatePlan(bool enabled, TimeSpan start, TimeSpan stop)
    {
        _enabled = enabled;
        _start = start;
        _stop = stop;
        Invalidate();
    }

    private static readonly string[] Days = { "一", "二", "三", "四", "五", "六", "今天" };

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var width = Width;
        if (width <= 0) return;

        // Weekday header strip.
        var header = new Rectangle(0, 0, width, 34);
        using (var strip = UiTheme.RoundedRect(header, UiTheme.RadiusSmall))
        using (var fill = new SolidBrush(UiTheme.HoverSurface))
            e.Graphics.FillPath(fill, strip);

        var cell = width / Days.Length;
        for (var i = 0; i < Days.Length; i++)
        {
            var today = i == Days.Length - 1;
            var rect = new Rectangle(i * cell, 0, cell, 34);
            if (today)
            {
                var pill = new Rectangle(rect.X + 6, rect.Y + 3, rect.Width - 12, rect.Height - 6);
                using var path = UiTheme.RoundedRect(pill, UiTheme.RadiusSmall);
                using var fill = new SolidBrush(UiTheme.SurfaceRaised);
                e.Graphics.FillPath(fill, path);
            }
            using var brush = new SolidBrush(today ? UiTheme.Accent : UiTheme.Muted);
            var text = Days[i];
            var size = e.Graphics.MeasureString(text, UiTheme.BodyFont);
            e.Graphics.DrawString(text, UiTheme.BodyFont, brush,
                rect.X + (rect.Width - size.Width) / 2, rect.Y + (34 - size.Height) / 2);
        }

        // Timeline area.
        var top = 44;
        var laneTop = top + 16;
        var laneHeight = Math.Max(18, Math.Min(30, (Height - laneTop - 8) / 2));
        var trackTop = laneTop;
        using (var guide = new Pen(UiTheme.Border))
        {
            for (var i = 1; i < Days.Length; i++)
                e.Graphics.DrawLine(guide, i * cell, top, i * cell, Math.Min(Height, laneTop + laneHeight + 6));
        }

        if (!_enabled)
        {
            using var hint = new SolidBrush(UiTheme.Faint);
            e.Graphics.DrawString("未启用每日计划", UiTheme.BodyFont, hint, 6, laneTop + 4);
            return;
        }

        var day = 24.0 * 60.0;
        var start = _start.TotalMinutes;
        var stop = _stop.TotalMinutes;
        if (stop <= start) stop += day;
        for (var i = 0; i < Days.Length; i++)
        {
            var x0 = i * cell + 4;
            var inner = cell - 8;
            var (a, b) = MapBar(start, stop, inner);
            var rect = new Rectangle(x0 + a, trackTop, Math.Max(4, b - a), laneHeight);
            using var path = UiTheme.RoundedRect(rect, 6);
            using var fill = new SolidBrush(i == Days.Length - 1 ? UiTheme.Accent : UiTheme.SurfaceRaised);
            e.Graphics.FillPath(fill, path);
            if (stop > day)
            {
                var second = new Rectangle(x0, trackTop, Math.Max(4, (int)(inner * (stop - day) / day)), laneHeight);
                using var path2 = UiTheme.RoundedRect(second, 6);
                using var fill2 = new SolidBrush(i == Days.Length - 1 ? UiTheme.Accent : UiTheme.SurfaceRaised);
                e.Graphics.FillPath(fill2, path2);
            }
        }
    }

    private static (int A, int B) MapBar(double start, double stop, int inner)
    {
        var day = 24.0 * 60.0;
        var from = Math.Max(0, Math.Min(day, start));
        var to = Math.Max(from, Math.Min(day, stop));
        var a = (int)(inner * from / day);
        var b = (int)(inner * to / day);
        return (a, Math.Max(a + 4, b));
    }
}
