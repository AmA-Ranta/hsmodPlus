using System.Drawing.Drawing2D;

namespace ArenaController.Ui.Design;

/// <summary>
/// Design tokens for the light "desktop control center" template: soft grey
/// canvas, white cards with hairline borders, one calm blue accent plus
/// green/amber status colours.
/// </summary>
internal static class UiTheme
{
    internal static readonly Color Background = Color.FromArgb(246, 247, 249);
    internal static readonly Color Surface = Color.White;
    internal static readonly Color SurfaceAlt = Color.White;
    internal static readonly Color SurfaceRaised = Color.FromArgb(238, 243, 255);
    internal static readonly Color HoverSurface = Color.FromArgb(245, 247, 250);
    internal static readonly Color Border = Color.FromArgb(237, 239, 243);
    internal static readonly Color BorderStrong = Color.FromArgb(225, 229, 235);

    internal static readonly Color Text = Color.FromArgb(17, 24, 39);
    internal static readonly Color Muted = Color.FromArgb(107, 114, 128);
    internal static readonly Color Faint = Color.FromArgb(156, 163, 175);

    internal static readonly Color Accent = Color.FromArgb(37, 99, 235);
    internal static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    internal static readonly Color AccentSoft = Color.FromArgb(238, 243, 255);
    internal static readonly Color Success = Color.FromArgb(22, 163, 74);
    internal static readonly Color SuccessSoft = Color.FromArgb(236, 253, 245);
    internal static readonly Color Warning = Color.FromArgb(217, 119, 6);
    internal static readonly Color WarningSoft = Color.FromArgb(255, 251, 240);
    internal static readonly Color WarningBorder = Color.FromArgb(253, 230, 138);
    internal static readonly Color Danger = Color.FromArgb(220, 38, 38);
    internal static readonly Color DangerSoft = Color.FromArgb(254, 242, 242);
    internal static readonly Color Info = Color.FromArgb(37, 99, 235);

    internal const int RadiusLarge = 12;
    internal const int RadiusSmall = 8;
    internal const int SidebarWidth = 236;
    internal const int NavItemHeight = 48;
    internal const int HeaderHeight = 100;
    internal const int ActionBarHeight = 56;

    internal static Font PageTitleFont { get; } = new("Microsoft YaHei UI", 15F, FontStyle.Bold, GraphicsUnit.Point);
    internal static Font BigFont { get; } = new("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
    internal static Font BrandFont { get; } = new("Microsoft YaHei UI", 12.5F, FontStyle.Bold, GraphicsUnit.Point);
    internal static Font CardTitleFont { get; } = new("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point);
    internal static Font SectionFont { get; } = new("Microsoft YaHei UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
    internal static Font BodyFont { get; } = new("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
    internal static Font CaptionFont { get; } = new("Microsoft YaHei UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point);
    internal static Font IconFont { get; } = new("Segoe MDL2 Assets", 11F, FontStyle.Regular, GraphicsUnit.Point);

    internal static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(2, radius * 2);
        if (bounds.Width <= diameter || bounds.Height <= diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
