# Combined semantic test inside one live match:
#   1) queue a switch to another mode  -> must queue, match keeps running
#   2) select the running mode again   -> must cancel the queue and keep playing
#   3) click stop                      -> must defer to the match boundary
# Results go to artifacts\mode-matrix.csv.
param(
    [string]$Mode = '标准',
    [string]$OtherMode = '酒馆战棋',
    [int]$MatchWaitSeconds = 900,
    [int]$StopWaitSeconds = 1800
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts') -Force | Out-Null
$csv = Join-Path $root 'artifacts\mode-matrix.csv'
$log = Join-Path $root 'artifacts\interrupt-test.log'
$controllerLog = Join-Path $env:LOCALAPPDATA 'ArenaBoxController\controller.log'
$setmode = Join-Path $PSScriptRoot 'uia-setmode.ps1'
$drive = Join-Path $PSScriptRoot 'uia-drive.ps1'
$indexOf = @{ '标准' = 0; '狂野' = 1; '休闲' = 2; '酒馆战棋' = 3; '竞技场' = 4 }

function Log([string]$message) { Add-Content $log ((Get-Date -Format 'HH:mm:ss') + ' ' + $message) -Encoding UTF8 }
function Record([string]$case, [string]$expected, [string]$actual, [bool]$ok, [string]$detail) {
    $line = '"' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '","' + $case + '","' + $expected + '","' + $actual + '","' + $ok + '","' + ($detail -replace '"', "'") + '"'
    Add-Content $csv $line -Encoding UTF8
    Log ('RESULT ok=' + $ok + ' case=' + $case + ' actual=' + $actual + ' detail=' + $detail)
}
function Get-Status {
    try {
        $html = (Invoke-WebRequest -Uri 'http://127.0.0.1:58744' -TimeoutSec 4 -UseBasicParsing).Content
        $token = [regex]::Match($html, 'name="hsmod-token" content="([^"]+)"').Groups[1].Value
        $status = (Invoke-WebRequest -Uri 'http://127.0.0.1:58744/api/status' -Headers @{ 'X-HsMod-Token' = $token } -TimeoutSec 5 -UseBasicParsing).Content | ConvertFrom-Json
        return [pscustomobject]@{ Reachable = $true; Mode = [string]$status.mode; Script = [string]$status.scriptEnabled; Activity = [string]$status.activity.status }
    } catch { return [pscustomobject]@{ Reachable = $false; Mode = ''; Script = ''; Activity = '' } }
}
function Get-Count { return (Get-Content $controllerLog -Encoding UTF8).Count }
function Get-NewLines([int]$from) {
    $all = Get-Content $controllerLog -Encoding UTF8
    if ($all.Count -le $from) { return @() }
    return $all[$from..($all.Count - 1)]
}
function Invoke-SetMode([string]$mode, [switch]$Start) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $setmode, '-Index', [string]$indexOf[$mode])
    if ($Start) { $arguments += '-Start' }
    $out = Join-Path $root 'artifacts\combined-setmode.log'
    $process = Start-Process cmd.exe -ArgumentList @('/c', "powershell $($arguments -join ' ') > `"$out`" 2>&1") -Verb RunAs -PassThru -WindowStyle Hidden
    $process.WaitForExit(150000) | Out-Null
}
function Invoke-StopClick {
    $out = Join-Path $root 'artifacts\combined-stop.log'
    $process = Start-Process cmd.exe -ArgumentList @('/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$drive`" -Stop > `"$out`" 2>&1") -Verb RunAs -PassThru -WindowStyle Hidden
    $process.WaitForExit(120000) | Out-Null
}

Log ('=== combined semantics for ' + $Mode + ' (other=' + $OtherMode + ') ===')
Invoke-SetMode $Mode | Out-Null
Start-Sleep -Seconds 2
Invoke-SetMode $Mode -Start | Out-Null

$deadline = (Get-Date).AddSeconds($MatchWaitSeconds)
$inMatch = $false
while ((Get-Date) -lt $deadline) {
    $status = Get-Status
    if ($status.Script -eq 'True' -and $status.Activity -match '回合|战斗') { $inMatch = $true; break }
    Start-Sleep -Seconds 5
}
if (-not $inMatch) {
    Record ('interrupt:' + $Mode) 'reach a live match' 'no live match within timeout' $false ''
    Invoke-StopClick | Out-Null
    exit 1
}
Log ('live match reached, act=' + (Get-Status).Activity)

# 1) queue a switch to another mode while the match runs
$from = Get-Count
Invoke-SetMode $OtherMode -Start | Out-Null
$deadline = (Get-Date).AddSeconds(60)
$queued = $null
while ((Get-Date) -lt $deadline) {
    $queued = Get-NewLines $from | Where-Object { $_ -match '已排队模式切换' } | Select-Object -Last 1
    if ($queued) { break }
    Start-Sleep -Seconds 2
}
$status = Get-Status
Record ('queue:' + $Mode + '->' + $OtherMode) 'switch queues and match keeps running' ('queued=' + [bool]$queued + ' script=' + $status.Script + ' act=' + $status.Activity) ([bool]$queued -and $status.Script -eq 'True') ([string]$queued)

# 2) select the running mode again: the queue must be cancelled
$from = Get-Count
Invoke-SetMode $Mode -Start | Out-Null
$deadline = (Get-Date).AddSeconds(60)
$cancelled = $null
while ((Get-Date) -lt $deadline) {
    $cancelled = Get-NewLines $from | Where-Object { $_ -match '已取消排队|已取消停止请求' } | Select-Object -Last 1
    if ($cancelled) { break }
    Start-Sleep -Seconds 2
}
$status = Get-Status
Record ('cancel:' + $Mode) 're-selecting the running mode cancels the queue and keeps playing' ('cancelled=' + [bool]$cancelled + ' script=' + $status.Script + ' act=' + $status.Activity) ([bool]$cancelled -and $status.Script -eq 'True') ([string]$cancelled)

# 3) stop: must wait for the match boundary
$stopAt = Get-Date
Invoke-StopClick | Out-Null
$deadline = (Get-Date).AddSeconds($StopWaitSeconds)
$stoppedAt = $null
while ((Get-Date) -lt $deadline) {
    $status = Get-Status
    if ($status.Reachable -and $status.Script -eq 'False') { $stoppedAt = Get-Date; break }
    Start-Sleep -Seconds 5
}
if ($stoppedAt) {
    $elapsed = [int][math]::Round(($stoppedAt - $stopAt).TotalSeconds)
    Record ('interrupt:' + $Mode) 'stop defers to the match boundary' ('script off after ' + $elapsed + 's') $true ('waited ' + $elapsed + 's')
} else {
    Record ('interrupt:' + $Mode) 'stop defers to the match boundary' 'timeout waiting for script off' $false ''
}
Log '=== combined semantics finished ==='
