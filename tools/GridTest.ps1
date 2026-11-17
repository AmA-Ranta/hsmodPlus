<#
.SYNOPSIS
  Grid test harness for the WPF controller.

.DESCRIPTION
  For every requested cell it writes controller-settings.json, restarts the
  controller, attaches through the UI, verifies the native mode/deck/limit,
  stops safely and checks the final stopped state.  Results are written to
  artifacts/grid-results.csv.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\GridTest.ps1 -Cells standard,wild -MaxCells 4
#>
[CmdletBinding()]
param(
    [string]$Cells = 'all',
    [int]$MaxCells = 99,
    [int]$OnlyLimit = -1,
    [switch]$SkipLive,
    [switch]$RestartGame
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Root 'artifacts'
New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null
$settingsPath = Join-Path $env:LOCALAPPDATA 'ArenaBoxController\controller-settings.json'
$controllerExe = 'D:\炉石中控-1031-整合\hsmodPlus.exe'
$uia = Join-Path $Artifacts 'uia-btn.ps1'
$log = Join-Path $env:LOCALAPPDATA 'ArenaBoxController\controller.log'
$resultCsv = Join-Path $Artifacts 'grid-results.csv'

if (-not (Test-Path $uia)) {
    $code = @'
param([int]$TargetPid, [string]$Action)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { 'NO_WINDOW'; exit 2 }
$buttons = @()
foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { $buttons += $el } }
$target = $buttons | Where-Object { $_.Current.Name -eq $Action } | Select-Object -First 1
if (-not $target) { 'NO_TARGET ' + $Action; exit 3 }
$target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
'INVOKED'
'@
    Set-Content $uia $code -Encoding UTF8
}

function Kill-Controller {
    $killScript = Join-Path $Artifacts 'grid-kill-controller.ps1'
    $killBody = @(
        'Get-Process hsmodPlus -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }',
        'Start-Sleep -Seconds 2'
    )
    Set-Content $killScript ($killBody -join "`r`n") -Encoding UTF8
    $q = Start-Process cmd.exe -ArgumentList @('/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$killScript`"") -Verb RunAs -PassThru -WindowStyle Hidden
    $q.WaitForExit(60000) | Out-Null
    Start-Sleep -Seconds 2
}

function Restart-Game {
    $killGameScript = Join-Path $Artifacts 'grid-kill-game.ps1'
    if (-not (Test-Path $killGameScript)) {
        $body = @(
            'Get-Process Hearthstone, HSAng -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }',
            'Start-Sleep -Seconds 4'
        )
        Set-Content $killGameScript ($body -join "`r`n") -Encoding UTF8
    }
    $q = Start-Process cmd.exe -ArgumentList @('/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$killGameScript`"") -Verb RunAs -PassThru -WindowStyle Hidden
    $q.WaitForExit(60000) | Out-Null
    Start-Sleep -Seconds 2
}

function Start-Controller {
    Kill-Controller
    $waitDeadline = (Get-Date).AddSeconds(25)
    while ((@(Get-Process hsmodPlus -ErrorAction SilentlyContinue).Count) -gt 0 -and (Get-Date) -lt $waitDeadline) { Start-Sleep -Seconds 1 }
    if (@(Get-Process hsmodPlus -ErrorAction SilentlyContinue).Count -gt 0) { throw 'ENV_ERROR: previous controller is still running' }
    $p = Start-Process -FilePath $controllerExe -WorkingDirectory (Split-Path $controllerExe) -Verb RunAs -PassThru
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ((Test-Path $log) -and ((Get-Content $log -Tail 3 -Encoding UTF8) -match '监控已开始')) { break }
    }
    return $p
}

function Click-Ui([int]$ControllerId, [string]$Action) {
    $out = Join-Path $Artifacts ('grid-uia-' + $Action + '.log')
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        Remove-Item $out -Force -ErrorAction SilentlyContinue
        $q = Start-Process cmd.exe -ArgumentList @('/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$uia`" -TargetPid $ControllerId -Action $Action *> `"$out`"") -Verb RunAs -PassThru -WindowStyle Hidden
        $q.WaitForExit(90000) | Out-Null
        Start-Sleep -Seconds 3
        $text = if (Test-Path $out) { (Get-Content $out -Encoding UTF8 | Select-Object -Last 1) } else { 'NO_OUTPUT' }
        if ($text -match 'INVOKED') { return $text }
    }
    return $text
}

function Get-LogLineCount { return @(Get-Content $log -Encoding UTF8 -ErrorAction SilentlyContinue).Count }

function Wait-LogMatch([string]$Pattern, [int]$TimeoutSeconds, [int]$SinceIndex = 0) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $lines = @(Get-Content $log -Encoding UTF8 -ErrorAction SilentlyContinue)
        if ($lines.Count -gt $SinceIndex) {
            $newText = ($lines[$SinceIndex..($lines.Count - 1)] -join "`n")
            if ($newText -match $Pattern) { return $true }
        }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Get-HsModStatus {
    for ($port = 58744; $port -le 58748; $port++) {
        try {
            $root = "http://127.0.0.1:$port"
            $html = (Invoke-WebRequest -Uri $root -TimeoutSec 3 -UseBasicParsing).Content
            $token = [regex]::Match($html, 'name="hsmod-token" content="([^"]+)"').Groups[1].Value
            if (-not $token) { continue }
            return (Invoke-WebRequest -Uri ($root + '/api/status') -Headers @{'X-HsMod-Token' = $token} -TimeoutSec 4 -UseBasicParsing).Content | ConvertFrom-Json
        } catch { }
    }
    return $null
}

function Get-DeckConfigItem {
    try {
        $html = (Invoke-WebRequest -Uri 'http://127.0.0.1:58744' -TimeoutSec 3 -UseBasicParsing).Content
        $token = [regex]::Match($html, 'name="hsmod-token" content="([^"]+)"').Groups[1].Value
        $cfg = (Invoke-WebRequest -Uri 'http://127.0.0.1:58744/api/config?lang=zhCN' -Headers @{'X-HsMod-Token' = $token} -TimeoutSec 6 -UseBasicParsing).Content | ConvertFrom-Json
        foreach ($item in $cfg.groups.'自动脚本') { if ($item.key -eq 'wildLadderDeckName') { return $item } }
    } catch { }
    return $null
}

function Test-DeckOption([string]$Mode, [string]$Wanted) {
    # Strictly requires the requested deck in this mode's native deckOptions
    # list (the UI `options` field is a different, empty placeholder array).
    $item = Get-DeckConfigItem
    if (-not $item) { return $null }
    $tag = switch ($Mode) { '标准' { '标准' } '狂野' { '狂野' } default { '' } }
    foreach ($opt in @($item.deckOptions)) {
        if ($opt.label -notlike "*$Wanted*") { continue }
        if ($tag -and ($opt.label -notlike "*$tag*")) { continue }
        return $opt.label
    }
    return $null
}

function Test-DeckPending {
    $item = Get-DeckConfigItem
    if (-not $item) { return $false }
    return [bool]$item.deckPending
}

function Get-NativeLimit {
    try {
        $html = (Invoke-WebRequest -Uri 'http://127.0.0.1:58744' -TimeoutSec 3 -UseBasicParsing).Content
        $token = [regex]::Match($html, 'name="hsmod-token" content="([^"]+)"').Groups[1].Value
        $cfg = (Invoke-WebRequest -Uri 'http://127.0.0.1:58744/api/config?lang=zhCN' -Headers @{'X-HsMod-Token' = $token} -TimeoutSec 6 -UseBasicParsing).Content | ConvertFrom-Json
        foreach ($item in $cfg.groups.'自动脚本') { if ($item.key -eq 'wildLadderStopAfterGames') { return $item.value } }
    } catch { }
    return $null
}

$escalated = Join-Path $Artifacts 'grid-settings-write.ps1'
function Set-CellSettings([string]$Mode, [int]$Limit, [bool]$Heartbeat, [string]$Deck,
    [bool]$Schedule = $false, [bool]$CloseGame = $false) {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $json.Mode = $Mode
    $json.DeckName = if ($Deck) { $Deck } else { $json.DeckName }
    $json.MaxTotalGames = $Limit
    $json.MaxGamesPerRun = 0
    $json.MaxRuns = 0
    $json.RequireBoxHeartbeat = $Heartbeat
    $json.CloseGameAfterStop = $CloseGame
    $json.AutoSchedule = $Schedule
    if ($Schedule) { $json.StartTime = '00:00'; $json.StopTime = '00:01' }
    $json.AutoSchedule = $false
    $body = $json | ConvertTo-Json -Depth 6
    Set-Content $escalated @"
param()
`$path = '$settingsPath'
`$body = @'
$body
'@
[IO.File]::WriteAllText(`$path, `$body, (New-Object Text.UTF8Encoding(`$false)))
"@ -Encoding UTF8
    $q = Start-Process cmd.exe -ArgumentList @('/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$escalated`"") -Verb RunAs -PassThru -WindowStyle Hidden
    $q.WaitForExit(30000) | Out-Null
}

$modeMap = @{ '标准' = 'Standard'; '狂野' = 'Wild'; '休闲' = 'Casual'; '酒馆战棋' = 'Battlegrounds' }
# 每个模式使用该模式真实存在的卡组：狂野没有“快攻”，用“海盗战”。
$deckForMode = @{ '标准' = '快攻'; '狂野' = '海盗战'; '休闲' = '快攻' }
$paperModes = @('标准', '狂野', '休闲', '酒馆战棋')
$selected = if ($Cells -eq 'all') { $paperModes + @('schedule') } else { $Cells -split ',' | ForEach-Object { $_.Trim() } }

$rows = @()
$cellNo = 0
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
foreach ($kind in $selected) {
    $mode = if ($kind -eq 'schedule' -or $kind -eq 'close') { '标准' } elseif ($kind -eq 'bg') { '酒馆战棋' } elseif ($kind -eq 'standard') { '标准' } elseif ($kind -eq 'wild') { '狂野' } elseif ($kind -eq 'casual') { '休闲' } else { $kind }
    $isSchedule = $kind -eq 'schedule'
    $isClose = $kind -eq 'close'
    foreach ($limit in 0, 1) {
        foreach ($heartbeat in $false, $true) {
            if ($cellNo -ge $MaxCells) { break }
            if (($isSchedule -or $isClose) -and ($limit -ne 0 -or $heartbeat)) { continue }
            if ($OnlyLimit -ge 0 -and $limit -ne $OnlyLimit -and -not ($isSchedule -or $isClose)) { continue }
            $cellNo++
            $deck = $deckForMode[$mode]
            $row = [ordered]@{ run = $runId; cell = $cellNo; mode = $mode; maxTotalGames = $limit; heartbeat = $heartbeat; deck = $deck
                startResult = ''; startSeconds = 0; nativeMode = ''; nativeDeck = ''; nativeLimit = ''
                stopResult = ''; stopSeconds = 0; finalScriptOff = ''; autoresumeOff = ''; verdict = 'FAIL'; evidence = '' }
            if ($SkipLive) { $row.startResult = 'SKIPPED'; $row.verdict = 'SKIPPED'; $rows += [pscustomobject]$row; continue }
            if ($mode -ne '酒馆战棋' -and -not $deck) { $row.startResult = 'SKIPPED_NO_DECK'; $row.verdict = 'SKIPPED'; $rows += [pscustomobject]$row; continue }
            try {
                if ($RestartGame -and $mode -ne '标准') { Restart-Game }
                Set-CellSettings $mode $limit $heartbeat $deck $isSchedule $isClose
                $started = Get-Date
                $p = Start-Controller
                $baseLines = Get-LogLineCount
                $clicked = Click-Ui $p.Id '开始任务'
                $attached = $false
                $blockedReason = ''
                $attachDeadline = (Get-Date).AddSeconds(240)
                while ((Get-Date) -lt $attachDeadline) {
                    $lines = @(Get-Content $log -Encoding UTF8 -ErrorAction SilentlyContinue)
                    if ($lines.Count -gt $baseLines) {
                        $newText = ($lines[$baseLines..($lines.Count - 1)] -join "`n")
                        if ($newText -match 'WPF 界面开始|已通过 HsMod 本地接口开启脚本') { $attached = $true; break }
                        if ($newText -match '模式为.*与中控选择.*不同|不会自动重启客户端|等待战网登录') { $blockedReason = ($newText -split "`n" | Where-Object { $_ -match '模式为|等待战网登录' } | Select-Object -Last 1); break }
                    }
                    Start-Sleep -Seconds 2
                }
                $row.startSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds)
                $allLines = @(Get-Content $log -Encoding UTF8 -ErrorAction SilentlyContinue)
                $newAll = if ($allLines.Count -gt $baseLines) { ($allLines[$baseLines..($allLines.Count - 1)] -join "`n") } else { '' }
                if ($isSchedule) {
                    # Outside the daily window the controller must refuse to start.
                    $windowBlocked = (-not $attached) -and ($newAll -match '计划|窗口|不在')
                    $row.startResult = 'SCHEDULE_OUTSIDE'
                    $row.stopResult = 'N/A'
                    $row.verdict = if ($windowBlocked) { 'PASS' } else { 'FAIL' }
                    $row.evidence = 'attached=' + $attached + '; blocked=' + $windowBlocked + ' | ' + $newAll.Substring(0, [Math]::Min(220, $newAll.Length))
                    $rows += [pscustomobject]$row
                    continue
                }
                if (-not $attached) {
                    if ($blockedReason) { $row.startResult = 'BLOCKED'; $row.verdict = 'BLOCKED' }
                    else { $row.startResult = 'FAIL_NO_ATTACH'; $row.verdict = 'FAIL' }
                    $row.evidence = ($clicked + ' | ' + $blockedReason + ' | ' + (Get-Content $log -Tail 3 -Encoding UTF8 | Out-String))
                } else {
                    $row.startResult = 'ATTACHED'
                    $st = Get-HsModStatus
                    if ($st) { $row.nativeMode = $st.mode; $row.nativeDeck = $st.deck.name; $row.nativeLimit = (Get-NativeLimit) }
                    $deckFound = ($mode -eq '酒馆战棋')
                    if (-not $deckFound) {
                        # 卡组列表要等客户端收藏加载完成，最多等 30 秒再判定。
                        $deckWait = (Get-Date).AddSeconds(30)
                        while ((Get-Date) -lt $deckWait) {
                            if (Test-DeckOption $mode $deck) { $deckFound = $true; break }
                            Start-Sleep -Seconds 3
                        }
                    }
                    $deckPending = Test-DeckPending
                    $expected = $modeMap[$mode]
                    $limitOk = (('' + $row.nativeLimit) -eq ('' + $limit))
                    $modeOk = ($row.nativeMode -eq $expected)
                    $deckOk = ($mode -eq '酒馆战棋') -or ($row.nativeDeck -eq $deck)
                    $stopAt = Get-Date
                    $stopBase = Get-LogLineCount
                    $click2 = Click-Ui $p.Id '停止并等待本局'
                    $stopped = Wait-LogMatch '已确认局后停止边界|已停止|停止接管' 420 $stopBase
                    $row.stopSeconds = [math]::Round(((Get-Date) - $stopAt).TotalSeconds)
                    $row.stopResult = if ($stopped) { 'STOPPED' } else { 'FAIL_STOP_TIMEOUT' }
                    $st2 = Get-HsModStatus
                    $row.finalScriptOff = if ($st2) { (-not $st2.scriptEnabled) } else { $false }
                    try { $ar = Get-Content 'D:\Hearthstone\BepInEx\HsMod\autoresume-auto-1.json' -Raw -Encoding UTF8 | ConvertFrom-Json; $row.autoresumeOff = (-not $ar.restartEnabled) } catch { $row.autoresumeOff = $false }
                    if ($stopped -and $modeOk -and $limitOk -and $deckOk -and $row.finalScriptOff -and $row.autoresumeOff) { $row.verdict = 'PASS' } else { $row.verdict = 'FAIL' }
                    $row.evidence += '; deckPending=' + $deckPending
                    $row.evidence = ($clicked + ' | ' + $click2 + ' | mode=' + $row.nativeMode + ' deck=' + $row.nativeDeck + ' limit=' + $row.nativeLimit + ' deckFound=' + $deckFound)
                    if ($isClose) {
                        # 停止后关闭炉石：脚本停止后客户端应在限定时间内正常退出。
                        $goneDeadline = (Get-Date).AddSeconds(120)
                        while ((@(Get-Process Hearthstone -ErrorAction SilentlyContinue).Count) -gt 0 -and (Get-Date) -lt $goneDeadline) { Start-Sleep -Seconds 3 }
                        $gameGone = (@(Get-Process Hearthstone -ErrorAction SilentlyContinue).Count -eq 0)
                        $row.finalScriptOff = $gameGone
                        $row.verdict = if ($stopped -and $gameGone) { 'PASS' } else { 'FAIL' }
                        $row.evidence += '; gameGone=' + $gameGone
                    }
                }
            } catch {
                $row.startResult = 'ERROR'
                $row.verdict = 'FAIL'
                $row.evidence = $_.Exception.Message
            }
            $rows += [pscustomobject]$row
        }
    }
}

if (Test-Path $resultCsv) { $rows | Export-Csv $resultCsv -NoTypeInformation -Encoding UTF8 -Append } else { $rows | Export-Csv $resultCsv -NoTypeInformation -Encoding UTF8 }
$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
Write-Host ("RESULTS=" + $resultCsv)
