Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE=[System.Windows.Automation.AutomationElement]
$w=$AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,(New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty,'hsmodPlus 中控')))
$btns=$w.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::Button)))
foreach($b in $btns){ try { $n=[string]$b.Current.Name; if($n -match '开始|停止'){ $r=$b.Current.BoundingRectangle; $n + ' | enabled=' + $b.Current.IsEnabled + ' | offscreen=' + $b.Current.IsOffscreen + ' | rect=' + [int]$r.X + ',' + [int]$r.Y + ' ' + [int]$r.Width + 'x' + [int]$r.Height } } catch { } }
$texts=$w.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))
$i=0
foreach($t in $texts){ try { $n=[string]$t.Current.Name; if($n -match '运行中|已排队|启动|已停止|脚本'){ $n.Substring(0,[Math]::Min(80,$n.Length)); $i++ } } catch { }; if($i -ge 6){ break } }
