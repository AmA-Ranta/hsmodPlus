[CmdletBinding()]
param(
    [ValidateSet('list','click','press','msg-click','combo','combo-real','tab','status','tab-selected','at')]
    [string]$Action = 'list',
    [string]$Name = '',
    [int]$Index = 0,
    [int]$X = 0,
    [int]$Y = 0,
    [int]$ComboItem = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class LiveUiNative {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<IntPtr> Children(IntPtr parent) {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, (h,l) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static string Text(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
    public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
    public static void Click(IntPtr h, int x, int y) {
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        SendMessage(h, 0x0201, (IntPtr)1, lp);
        System.Threading.Thread.Sleep(60);
        SendMessage(h, 0x0202, IntPtr.Zero, lp);
    }
}
'@
[void][LiveUiNative]::SetProcessDPIAware()
$proc = Get-Process -Name hsmodPlus -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'hsmodPlus 未运行' }
$form = $proc.MainWindowHandle
if ($form -eq [IntPtr]::Zero) { throw '找不到主窗口' }

function Focus-Form {
    # Release the foreground lock so an injected/real click actually lands on
    # this (already elevated) controller window instead of whichever window
    # currently owns the foreground.
    [LiveUiNative]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40
    [LiveUiNative]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
    [void][LiveUiNative]::ShowWindow($form, 9)
    [void][LiveUiNative]::SetForegroundWindow($form)
    Start-Sleep -Milliseconds 400
}

function Get-Controls {
    $rows = @()
    foreach ($h in [LiveUiNative]::Children($form)) {
        $r = New-Object LiveUiNative+RECT
        [void][LiveUiNative]::GetWindowRect($h, [ref]$r)
        $rows += [pscustomobject]@{
            Handle = $h.ToInt64()
            Class = [LiveUiNative]::Cls($h)
            Text = [LiveUiNative]::Text($h)
            Visible = [LiveUiNative]::IsWindowVisible($h)
            Enabled = [LiveUiNative]::IsWindowEnabled($h)
            X = $r.Left; Y = $r.Top; W = ($r.Right - $r.Left); H = ($r.Bottom - $r.Top)
        }
    }
    $rows
}

switch ($Action) {
    'list' {
        Get-Controls | Where-Object { $_.W -gt 0 -and $_.H -gt 0 } |
            ForEach-Object { '{0}|{1}|{2}|vis={3}|en={4}|{5},{6} {7}x{8}' -f $_.Handle, ($_.Class -replace 'WindowsForms10\.',''), $_.Text, $_.Visible, $_.Enabled, $_.X, $_.Y, $_.W, $_.H }
    }
    'click' {
        $target = Get-Controls | Where-Object { $_.Text -eq $Name -and $_.Visible } | Select-Object -First 1
        if (-not $target) { $target = Get-Controls | Where-Object { $_.Text -like ('*' + $Name + '*') -and $_.Visible -and $_.Enabled } | Select-Object -First 1 }
        if (-not $target) { throw "找不到控件：$Name" }
        [LiveUiNative]::Click([IntPtr]$target.Handle, [int]($target.W / 2), [int]($target.H / 2))
        "clicked '$($target.Text)' handle=$($target.Handle) $($target.W)x$($target.H)"
    }
    'press' {
        $target = Get-Controls | Where-Object { $_.Text -eq $Name -and $_.Visible } | Select-Object -First 1
        if (-not $target) { $target = Get-Controls | Where-Object { $_.Text -like ('*' + $Name + '*') -and $_.Visible -and $_.Enabled } | Select-Object -First 1 }
        if (-not $target) { throw "找不到控件：$Name" }
        Focus-Form
        Start-Sleep -Milliseconds 350
        $x = [int]($target.X + $target.W / 2)
        $y = [int]($target.Y + $target.H / 2)
        [void][LiveUiNative]::SetCursorPos($x, $y)
        Start-Sleep -Milliseconds 120
        [LiveUiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) # LEFTDOWN
        Start-Sleep -Milliseconds 90
        [LiveUiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) # LEFTUP
        "pressed '$($target.Text)' at screen $x,$y"
    }
    'at' {
        Focus-Form
        Start-Sleep -Milliseconds 350
        [void][LiveUiNative]::SetCursorPos($X, $Y)
        Start-Sleep -Milliseconds 120
        [LiveUiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 90
        [LiveUiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        "clicked screen $X,$Y"
    }
    'msg-click' {
        $target = Get-Controls | Where-Object { $_.Text -eq $Name -and $_.Visible } | Select-Object -First 1
        if (-not $target) { $target = Get-Controls | Where-Object { $_.Text -like ('*' + $Name + '*') -and $_.Visible -and $_.Enabled } | Select-Object -First 1 }
        if (-not $target) { throw "找不到控件：$Name" }
        [void][LiveUiNative]::SendMessage([IntPtr]$target.Handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
        $lp = [IntPtr](([int]($target.H / 2) -shl 16) -bor ([int]($target.W / 2) -band 0xFFFF))
        [void][LiveUiNative]::SendMessage([IntPtr]$target.Handle, 0x0201, [IntPtr]1, $lp)
        Start-Sleep -Milliseconds 80
        [void][LiveUiNative]::SendMessage([IntPtr]$target.Handle, 0x0202, [IntPtr]::Zero, $lp)
        "msg-clicked '$($target.Text)' handle=$($target.Handle)"
    }
    'combo' {
        $combos = Get-Controls | Where-Object { $_.Class -match 'COMBOBOX|ComboBox' -and $_.Visible }
        if ($combos.Count -eq 0) { throw '找不到 ComboBox' }
        $target = $combos[$Index] 2>$null
        if (-not $target) { throw "找不到第 $Index 个 ComboBox" }
        # CB_SETCURSEL then a CBN_SELCHANGE notification to the parent form.
        [void][LiveUiNative]::SendMessage([IntPtr]$target.Handle, 0x014E, [IntPtr]$ComboItem, [IntPtr]::Zero)
        $ctrlId = [LiveUiNative]::GetDlgCtrlID([IntPtr]$target.Handle)
        $wparam = [IntPtr](($ctrlId -band 0xFFFF) -bor (1 -shl 16))   # CBN_SELCHANGE = 1
        [void][LiveUiNative]::SendMessage($form, 0x0111, $wparam, [IntPtr]$target.Handle)
        "combo index $Index handle=$($target.Handle) set item=$ComboItem notify=$wparam"
    }
    'combo-real' {
        $combos = Get-Controls | Where-Object { $_.Class -match 'COMBOBOX|ComboBox' -and $_.Visible }
        if ($combos.Count -eq 0) { throw '找不到 ComboBox' }
        $target = $combos[$Index]
        Focus-Form
        Start-Sleep -Milliseconds 350
        $cx = [int]($target.X + $target.W / 2); $cy = [int]($target.Y + $target.H / 2)
        [void][LiveUiNative]::SetCursorPos($cx, $cy)
        Start-Sleep -Milliseconds 120
        [LiveUiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [LiveUiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 400
        [LiveUiNative]::keybd_event(0x24, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40
        [LiveUiNative]::keybd_event(0x24, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
        for ($i = 0; $i -lt $ComboItem; $i++) {
            [LiveUiNative]::keybd_event(0x28, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40
            [LiveUiNative]::keybd_event(0x28, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80
        }
        [LiveUiNative]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
        [LiveUiNative]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
        "combo-real index $Index -> item $ComboItem via keyboard"
    }
    'tab' {
        $tab = Get-Controls | Where-Object { $_.Class -match 'SysTabControl32' } | Select-Object -First 1
        if (-not $tab) { throw '找不到 TabControl' }
        # Never send TCM_GETITEMRECT across processes: its lParam is a pointer
        # that the target process would dereference, which crashes it with an
        # AccessViolationException.  The controller uses fixed-size tabs
        # (144x42, padding 12,6), so compute the header centre arithmetically
        # and inject a real mouse click instead.
        $cx = [int]($tab.X + 12 + $Index * 144 + 72)
        $cy = [int]($tab.Y + 6 + 21)
        Focus-Form
        Start-Sleep -Milliseconds 300
        [void][LiveUiNative]::SetCursorPos($cx, $cy)
        Start-Sleep -Milliseconds 120
        [LiveUiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 90
        [LiveUiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        "tab $Index clicked at $cx,$cy"
    }
    'tab-selected' {
        $tab = Get-Controls | Where-Object { $_.Class -match 'SysTabControl32' } | Select-Object -First 1
        $sel = [LiveUiNative]::SendMessage([IntPtr]$tab.Handle, 0x130B, [IntPtr]::Zero, [IntPtr]::Zero)
        "selected tab index: $([int]$sel)"
    }
    'status' {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
        $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if (-not $win) { throw 'UIA 找不到窗口' }
        $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $all) {
            $n = $el.Current.Name
            if ([string]::IsNullOrWhiteSpace($n)) { continue }
            if ($n -match '炉石：|脚本：|插件：|：\\d+ 胜|最近时间|已探测|中控已启动|监控已开始|启动|停止|安装|失败|每日计划') { $n }
        }
    }
}
