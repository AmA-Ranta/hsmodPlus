using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ArenaController;

/// <summary>
/// Applies <see cref="BoxOverlayPolicy"/> to the running Box process so its
/// embedded Qt overlay never eats clicks meant for the game client.
/// </summary>
internal static class BoxOverlayClickThrough
{
    private const int GwlExStyle = -20;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;

    public static int Apply(IEnumerable<int> boxProcessIds, Action<string>? log = null)
    {
        var applied = 0;
        foreach (var pid in boxProcessIds)
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (GetWindowThreadProcessId(hwnd, out var owner) != pid || !IsWindowVisible(hwnd)) return true;
                    var className = ClassName(hwnd);
                    var title = WindowText(hwnd);
                    var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
                    if (!BoxOverlayPolicy.ShouldMakeClickThrough(className, title, exStyle)) return true;
                    SetWindowLongPtr(hwnd, GwlExStyle, BoxOverlayPolicy.ClickThroughStyle(exStyle));
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SwpNoActivate | SwpNoSize | SwpNoMove | SwpNoZOrder);
                    applied++;
                    log?.Invoke("box-overlay.click-through;title=" + title + ";hwnd=" + hwnd);
                }
                catch (Exception ex) { log?.Invoke("box-overlay.click-through.failed=" + ex.Message); }
                return true;
            }, IntPtr.Zero);
        }
        return applied;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(128);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string WindowText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern long SetWindowLongPtr(IntPtr hwnd, int index, long value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    public static IEnumerable<int> RunningBoxProcessIds()
    {
        foreach (var process in Process.GetProcessesByName("HSAng"))
        {
            int id;
            try { id = process.Id; }
            finally { process.Dispose(); }
            yield return id;
        }
    }
}
