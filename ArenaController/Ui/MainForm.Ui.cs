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

internal sealed partial class MainForm : Form
{

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);


    private static Icon LoadApplicationIcon()
    {
        try
        {
            using var embedded = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (embedded != null) return (Icon)embedded.Clone();
        }
        catch { /* fall back to the drawn emoji below */ }
        return CreateBathIcon();
    }


    private static Icon CreateBathIcon()
    {
        using var bitmap = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI Emoji", 42F, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            const string glyph = "🛀";
            var measured = graphics.MeasureString(glyph, font);
            using var brush = new SolidBrush(Color.White);
            graphics.DrawString(glyph, font, brush,
                (bitmap.Width - measured.Width) / 2F,
                (bitmap.Height - measured.Height) / 2F - 2F);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }


    private void DisposeBathIcon()
    {
        var icon = _bathIcon;
        _bathIcon = null;
        if (icon == null) return;
        icon.Dispose();
    }


    private void BuildUi()
    {
        BuildShell();
    }


    private static void ApplyDarkTheme(Control root, bool header = false)
    {
        var isHeader = header || root.BackColor == Color.FromArgb(19, 30, 51);
        foreach (Control control in root.Controls)
        {
            if (!isHeader)
            {
                switch (control)
                {
                    case GroupBox group:
                        group.BackColor = UiSurface;
                        group.ForeColor = UiText;
                        break;
                    case TabPage page:
                        page.BackColor = Color.FromArgb(13, 22, 37);
                        page.ForeColor = UiText;
                        break;
                    case Label label:
                        label.ForeColor = UiText;
                        break;
                    case TextBox textBox when !textBox.ReadOnly:
                        textBox.BackColor = Color.FromArgb(9, 19, 33);
                        textBox.ForeColor = UiText;
                        textBox.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case TextBox readOnly:
                        readOnly.BackColor = Color.FromArgb(19, 31, 49);
                        readOnly.ForeColor = UiMuted;
                        readOnly.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case ComboBox combo:
                        combo.BackColor = Color.FromArgb(9, 19, 33);
                        combo.ForeColor = UiText;
                        combo.FlatStyle = FlatStyle.Flat;
                        break;
                    case NumericUpDown number:
                        number.BackColor = Color.FromArgb(9, 19, 33);
                        number.ForeColor = UiText;
                        number.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case CheckBox check:
                        check.ForeColor = UiText;
                        break;
                    case Button button:
                        button.BackColor = UiSurfaceRaised;
                        button.ForeColor = UiText;
                        button.FlatStyle = FlatStyle.Flat;
                        button.FlatAppearance.BorderColor = UiBorder;
                        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(35, 58, 78);
                        break;
                }
            }
            ApplyDarkTheme(control, isHeader);
        }
    }


    private void StyleActionButtons()
    {
        _start.BackColor = UiTheme.Accent;
        _start.ForeColor = Color.White;
        _start.FlatStyle = FlatStyle.Flat;
        _start.FlatAppearance.BorderSize = 0;
        _start.FlatAppearance.MouseOverBackColor = UiTheme.AccentHover;
        _stop.BackColor = Color.White;
        _stop.ForeColor = UiTheme.Danger;
        _stop.FlatStyle = FlatStyle.Flat;
        _stop.FlatAppearance.BorderColor = Color.FromArgb(255, 196, 196);
        _stop.FlatAppearance.MouseOverBackColor = UiTheme.DangerSoft;
        _install.BackColor = UiTheme.Accent;
        _install.ForeColor = Color.White;
        _install.FlatStyle = FlatStyle.Flat;
        _install.FlatAppearance.BorderSize = 0;
        _install.FlatAppearance.MouseOverBackColor = UiTheme.AccentHover;
    }


    private void AddPathRow(TableLayoutPanel grid, int row, string title, Func<string> read, Action<string> write)
    {
        grid.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = UiTheme.Muted, Padding = new Padding(3, 0, 3, 0) }, 0, row);
        var box = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 8), BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, BorderStyle = BorderStyle.FixedSingle };
        box.Text = read(); _pathToolTip.SetToolTip(box, box.Text); box.TextChanged += (_, _) => { write(box.Text); _pathToolTip.SetToolTip(box, box.Text); };
        _pathBoxes[title] = box;
        grid.Controls.Add(box, 1, row);
        var browse = new Button { Text = "…", Dock = DockStyle.Fill, Margin = new Padding(5, 8, 3, 8), TabStop = false, FlatStyle = FlatStyle.Flat };
        browse.Click += (_, _) =>
        {
            if (title.Contains("盒子", StringComparison.Ordinal))
            {
                using var dialog = new OpenFileDialog { Title = "选择炉石传说盒子", Filter = "炉石传说盒子 (HSAng.exe)|HSAng.exe|可执行文件 (*.exe)|*.exe" };
                if (File.Exists(box.Text)) dialog.FileName = box.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
            }
            else
            {
                using var dialog = new FolderBrowserDialog { Description = "选择炉石目录、Hearthstone.exe、官方启动器或 Hearthstone_Data 目录" };
                var current = NativeInstallService.ResolveGameRootPath(box.Text);
                if (!string.IsNullOrWhiteSpace(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
            }
        };
        grid.Controls.Add(browse, 2, row);
    }


    private void AddReadOnlyPathRow(TableLayoutPanel grid, int row, string title, Func<string> read)
    {
        grid.Controls.Add(new Label { Text = title, Anchor = AnchorStyles.Left, AutoSize = true, ForeColor = UiTheme.Faint }, 0, row);
        var box = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = UiTheme.SurfaceAlt, ForeColor = UiTheme.Muted, BorderStyle = BorderStyle.FixedSingle, TabStop = false };
        box.Text = read(); _pathBoxes[title] = box;
        grid.Controls.Add(box, 1, row);
        grid.SetColumnSpan(box, 2);
    }


    private void UpdateModeUi()
    {
        var arena = string.Equals(_mode.SelectedItem?.ToString(), "竞技场", StringComparison.Ordinal);
        var battlegrounds = string.Equals(_mode.SelectedItem?.ToString(), "酒馆战棋", StringComparison.Ordinal);
        _mode.Enabled = !_taskInputsLocked;
        _deckName.Enabled = !_taskInputsLocked && !arena && !battlegrounds;
        _arenaMode.Enabled = !_taskInputsLocked && arena;
        UpdateArenaRunStopUi();
    }


    private void LockTaskInputs(bool locked)
    {
        _taskInputsLocked = locked;
        UpdateModeUi();
        _runGames.Enabled = !locked;
        _totalGames.Enabled = !locked;
        _maxRuns.Enabled = !locked;
    }


    private static void AddControlRow(TableLayoutPanel grid, int row, string title, Control control, int column, int span)
    {
        grid.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = UiTheme.Muted }, column - 1, row);
        control.Margin = new Padding(3, 6, 3, 6);
        grid.Controls.Add(control, column, row); grid.SetColumnSpan(control, span);
    }


    private static void ConfigureNumber(NumericUpDown number, int min, int max)
    {
        number.Minimum = min; number.Maximum = max;
        // Let the table cell determine the width.  A fixed 82px control is
        // scaled by WinForms at high DPI and can become wider than the cell,
        // clipping the value or its spinner buttons.
        number.Dock = DockStyle.Fill;
    }


    private void SetPathBox(string title, string value)
    { if (_pathBoxes.TryGetValue(title, out var box)) box.Text = value; }

}
