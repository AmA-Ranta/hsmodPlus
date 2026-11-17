param([string]$Mode='',[string]$ModeKey='',[string]$Deck='',[int]$Limit=-1,[string]$Heartbeat='',[int]$ArenaRound=-1,[int]$ArenaTotal=-1,[int]$ArenaMaxRuns=-1,[switch]$Apply,[switch]$Start,[switch]$Stop)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$root = $AE::RootElement
$c0 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, 'hsmodPlus 中控')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $c0)
if (-not $win) { 'window not found'; exit 1 }
function ByType($ct) { return $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct))) }
function ByName($name) { return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name))) }
if ($ModeKey -ne '') {
  $combos = ByType ([System.Windows.Automation.ControlType]::ComboBox)
  $combo = $combos[0]
  $combo.SetFocus(); Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait($ModeKey); Start-Sleep -Milliseconds 500
  try { $sp = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern); $cur = $sp.Current.GetSelection()[0].Current.Name; 'mode now: ' + $cur } catch { 'mode read failed' }
}
if ($Mode -ne '') {
  $combos = ByType ([System.Windows.Automation.ControlType]::ComboBox)
  $combo = $combos[0]
  $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
  $ec.Expand(); Start-Sleep -Milliseconds 600
  $liCond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
    (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Mode)))
  $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $liCond)
  if (-not $item) { 'listitem not found: ' + $Mode; $ec.Collapse(); exit 2 }
  $sel = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
  $sel.Select(); Start-Sleep -Milliseconds 400
  try { $ec.Collapse() } catch { }
  'mode selected: ' + $Mode
}
$edits = ByType ([System.Windows.Automation.ControlType]::Edit)
if ($Deck -ne '') { $vp = $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue($Deck); 'deck set: ' + $Deck }
if ($Limit -ge 0) { $vp = $edits[1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue([string]$Limit); 'limit set: ' + $Limit }
if ($ArenaRound -ge 0) { $vp = $edits[4].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue([string]$ArenaRound); 'arenaRound set' }
if ($ArenaTotal -ge 0) { $vp = $edits[5].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue([string]$ArenaTotal); 'arenaTotal set' }
if ($ArenaMaxRuns -ge 0) { $vp = $edits[6].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $vp.SetValue([string]$ArenaMaxRuns); 'arenaMaxRuns set' }
if ($Heartbeat -ne '') {
  $cb = ByName '启动前等待盒子心跳'
  if ($cb) { $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $cur = $tp.Current.ToggleState; $want = if ($Heartbeat -eq 'on') { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }; if ($cur -ne $want) { $tp.Toggle(); Start-Sleep -Milliseconds 300 }; 'heartbeat=' + $Heartbeat + ' was=' + $cur }
}
if ($Apply) { $b = ByName '应用设置'; if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); 'applied' } }
if ($Start) { $b = ByName '开始任务'; if (-not $b) { $b = ByName '开始 / 切换' }; if (-not $b) { $b = ByName '开始/切换' }; if ($b) { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); 'started(' + $b.Current.Name + ')' } else { 'start button not found' } }
if ($Stop) { $b = ByName '停止并等待本局'; if (-not $b) { $b = ByName '本轮结束后停止' }; if ($b) { try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); 'stopped-clicked' } catch { 'stop-button-not-enabled' } } else { 'stop-button-not-found' } }
