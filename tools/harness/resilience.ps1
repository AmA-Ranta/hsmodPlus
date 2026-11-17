# Resilience scenarios for the controller (v3, tracked in git).
# Kills run elevated because Hearthstone and the box are elevated; a non-elevated
# Stop-Process fails silently and invalidates the scenario.
# Scenarios: kill game / kill box / restart controller. Battle.net is untouched.
param(
    [string]$Mode = '标准',
    [int]$StartTimeoutSeconds = 420,
    [int]$RecoveryTimeoutSeconds = 300,
    [switch]$OnlyBox,
    [switch]$OnlyController
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csv = Join-Path $root 'artifacts\resilience.csv'
$log = Join-Path $root 'artifacts\resilience.log'
$setmode = Join-Path $PSScriptRoot 'uia-setmode.ps1'
$drive = Join-Path $PSScriptRoot 'uia-drive.ps1'
$installed = 'D:\炉石中控-1031-整合\hsmodPlus.exe'
$indexOf = @{ '标准' = 0; '狂野' = 1; '休闲' = 2; '酒馆战棋' = 3; '竞技场' = 4 }
$deadline = (Get-Date).AddMinutes(35)

# A stray runner from an earlier session once kept killing the box and restarting
# the controller hours later, which looked exactly like product crashes.
$selfPid = $PID
$parentPid = (Get-WmiObject Win32_Process -Filter ("ProcessId=" + $selfPid)).ParentProcessId
$conflicts = @(Get-WmiObject Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
    $_.ProcessId -ne $selfPid -and $_.ProcessId -ne $parentPid -and $_.CommandLine -and
    ($_.CommandLine.Contains('resilience.ps1') -or $_.CommandLine.Contains('mode-matrix.ps1'))
})
if ($conflicts.Count -gt 0) {
    Write-Host ("ABORT: another harness runner is active: " + ($conflicts.ProcessId -join ','))
    exit 2
}

if (-not (Test-Path $csv)) { Set-Content $csv 'time,case,expected,actual,ok,detail' -Encoding UTF8 }
function Log([string]$message) { Add-Content $log ((Get-Date -Format 'HH:mm:ss') + ' ' + $message) -Encoding UTF8 }
function Check-Deadline {
    if ((Get-Date) -gt $deadline) { Log 'deadline exceeded; exiting'; exit 4 }
}
function Record([string]$case, [string]$expected, [string]$actual, [bool]$ok, [string]$detail) {
    $line = '"' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '","' + $case + '","' + $expected + '","' + $actual + '","' + $ok + '","' + ($detail -replace '"', "'") + '"'
    Add-Content $csv $line -Encoding UTF8
    Log ('RESULT ok=' + $ok + ' case=' + $case + ' actual=' + $actual)
}
function Get-Status {
    try {
        $html = (Invoke-WebRequest -Uri 'http://127.0.0.1:58744' -TimeoutSec 4 -UseBasicParsing).Content
        $token = [regex]::Match($html, 'name="hsmod-token" content="([^"]+)"').Groups[1].Value
        $status = (Invoke-WebRequest -Uri 'http://127.0.0.1:58744/api/status' -Headers @{ 'X-HsMod-Token' = $token } -TimeoutSec 5 -UseBasicParsing).Content | ConvertFrom-Json
        return [pscustomobject]@{ Reachable = $true; Mode = [string]$status.mode; Script = [string]$status.scriptEnabled; Activity = [string]$status.activity.status }
    } catch {
        return [pscustomobject]@{ Reachable = $false; Mode = ''; Script = ''; Activity = '' }
    }
}
function Kill-Elevated([string]$namePattern) {
    $payload = "Get-Process | Where-Object { `$_.ProcessName -like '$namePattern' } | Stop-Process -Force -ErrorAction SilentlyContinue`r`nStart-Sleep -Seconds 2`r`n"
    $tmp = Join-Path $root ('artifacts\kill-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    Set-Content $tmp $payload -Encoding UTF8
    $process = Start-Process cmd.exe -ArgumentList @('/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$tmp`"") -Verb RunAs -PassThru -WindowStyle Hidden
    $process.WaitForExit(60000) | Out-Null
    for ($i = 0; $i -lt 8; $i++) {
        if (@(Get-Process | Where-Object { $_.ProcessName -like $namePattern }).Count -eq 0) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}
function Invoke-SetMode([string]$mode, [switch]$Start) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $setmode, '-Index', [string]$indexOf[$mode])
    if ($Start) { $arguments += '-Start' }
    $out = Join-Path $root 'artifacts\resilience-setmode.log'
    $process = Start-Process cmd.exe -ArgumentList @('/c', "powershell $($arguments -join ' ') > `"$out`" 2>&1") -Verb RunAs -PassThru -WindowStyle Hidden
    $process.WaitForExit(150000) | Out-Null
}
function Invoke-StopClick {
    $out = Join-Path $root 'artifacts\resilience-stop.log'
    $process = Start-Process cmd.exe -ArgumentList @('/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$drive`" -Stop > `"$out`" 2>&1") -Verb RunAs -PassThru -WindowStyle Hidden
    $process.WaitForExit(120000) | Out-Null
}
function Wait-Started([int]$timeoutSeconds) {
    $limit = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $limit) {
        Check-Deadline
        $status = Get-Status
        if ($status.Reachable -and $status.Script -eq 'True') { return $true }
        Start-Sleep -Seconds 5
    }
    return $false
}
function Ensure-Stopped {
    $status = Get-Status
    if ($status.Reachable -and $status.Script -eq 'False') { return $true }
    Invoke-StopClick | Out-Null
    $limit = (Get-Date).AddSeconds(420)
    while ((Get-Date) -lt $limit) {
        Check-Deadline
        $status = Get-Status
        if ($status.Reachable -and $status.Script -eq 'False') { return $true }
        Start-Sleep -Seconds 5
    }
    return $false
}
function Start-Task([string]$mode) {
    Ensure-Stopped | Out-Null
    Invoke-SetMode $mode | Out-Null
    Start-Sleep -Seconds 2
    Invoke-SetMode $mode -Start | Out-Null
    return (Wait-Started $StartTimeoutSeconds)
}

Log '=== resilience v3 run started ==='

if (-not $OnlyBox -and -not $OnlyController) {
    # R1: kill the game while a task runs; the controller must relaunch it.
    if (-not (Start-Task $Mode)) {
        Record ('kill-game@' + $Mode) 'task running before the kill' 'start failed' $false ''
    } else {
        $hsBefore = @(Get-Process Hearthstone -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
        $killed = Kill-Elevated 'Hearthstone'
        Log ('killed Hearthstone=' + $killed + ' before=' + ($hsBefore -join ','))
        $limit = (Get-Date).AddSeconds($RecoveryTimeoutSeconds)
        $recovered = $false
        while ((Get-Date) -lt $limit) {
            Check-Deadline
            $alive = @(Get-Process Hearthstone -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
            $status = Get-Status
            if ($killed -and $alive.Count -gt 0 -and $status.Reachable -and $status.Script -eq 'True') { $recovered = $true; break }
            Start-Sleep -Seconds 6
        }
        Record ('kill-game@' + $Mode) 'client relaunched and script resumes' ('killed=' + $killed + ' recovered=' + $recovered) ($killed -and $recovered) ''
    }
}

if (-not $OnlyController) {
    # R2: kill every box process; the task must keep running and the box must return.
    if (-not (Start-Task $Mode)) {
        Record ('kill-box@' + $Mode) 'task running before the kill' 'start failed' $false ''
    } else {
        $killed = Kill-Elevated 'HSA*'
        Log ('killed box=' + $killed)
        $limit = (Get-Date).AddSeconds($RecoveryTimeoutSeconds)
        $recovered = $false
        while ((Get-Date) -lt $limit) {
            Check-Deadline
            $boxAlive = @(Get-Process | Where-Object { $_.ProcessName -like 'HSA*' }).Count -gt 0
            $status = Get-Status
            if ($boxAlive) { $recovered = $true; break }
            Start-Sleep -Seconds 6
        }
        Record ('kill-box@' + $Mode) 'box processes come back and the task keeps running' ('killed=' + $killed + ' recovered=' + $recovered) ($killed -and $recovered) ''
    }
}

# R3: restart the controller itself; it must come back as one instance and start.
Ensure-Stopped | Out-Null
Kill-Elevated 'hsmodPlus' | Out-Null
Start-Sleep -Seconds 4
Start-Process $installed
Start-Sleep -Seconds 15
$status = Get-Status
if ($status.Reachable) {
    $started = Start-Task $Mode
    $instances = @(Get-Process hsmodPlus -ErrorAction SilentlyContinue).Count
    Record ('restart-controller@' + $Mode) 'one instance again and can start a task' ('reachable=True started=' + $started + ' instances=' + $instances) ($started -and $instances -eq 1) ''
} else {
    Record ('restart-controller@' + $Mode) 'controller restarts' 'api unreachable after restart' $false ''
}

Ensure-Stopped | Out-Null
Log '=== resilience v3 run finished ==='
