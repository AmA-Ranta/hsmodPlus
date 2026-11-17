param([int]$Index=3,[switch]$Apply,[switch]$Start)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Fore9 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@
$AE=[System.Windows.Automation.AutomationElement]
$names=@('标准','狂野','休闲','酒馆战棋','竞技场')
$expected = if($Index -ge 0 -and $Index -lt $names.Count){ $names[$Index] } else { $null }
$w=$AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,(New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty,'hsmodPlus 中控')))
if(-not $w){ 'no controller window'; exit 1 }
$h=[IntPtr]$w.Current.NativeWindowHandle
[Fore9]::ShowWindow($h,9)|Out-Null
Start-Sleep -Milliseconds 300
$combos=$w.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::ComboBox)))
if($combos.Count -eq 0){ 'no combo'; exit 2 }
$c=$combos[0]
# Selecting by keystrokes is fragile after a client crash or when the window loses
# focus: the combo silently keeps its old value. Verify the selection and retry.
$selected=$null
for($attempt=1;$attempt -le 4;$attempt++){
  [Fore9]::SetForegroundWindow($h)|Out-Null
  Start-Sleep -Milliseconds 250
  try { $c.SetFocus() } catch { }
  Start-Sleep -Milliseconds 350
  [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
  Start-Sleep -Milliseconds 250
  for($i=0;$i -lt $Index;$i++){ [System.Windows.Forms.SendKeys]::SendWait('{DOWN}'); Start-Sleep -Milliseconds 180 }
  Start-Sleep -Milliseconds 400
  try {
    $sp=$c.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    $sel=$sp.Current.GetSelection()
    if($sel.Count -gt 0){ $selected=$sel[0].Current.Name }
  } catch { $selected=$null }
  if($selected -and (-not $expected -or $selected -eq $expected)){ break }
  Start-Sleep -Milliseconds 500
}
'mode now: ' + $selected
if($expected -and $selected -ne $expected){ 'mode selection failed (expected ' + $expected + ')'; exit 3 }
function ByName($name){ $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty,$name))) }
if($Apply){ $b=ByName '应用设置'; if($b){ $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); 'applied' } else { 'apply button missing' } }
Start-Sleep -Milliseconds 600
if($Start){ $b=ByName '开始任务'; if(-not $b){ $b=ByName '开始 / 切换' }; if(-not $b){ $b=ByName '开始/切换' }; if($b){ $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); 'started(' + $b.Current.Name + ')' } else { 'start button missing' } }
