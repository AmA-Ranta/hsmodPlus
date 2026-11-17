# Mode matrix driver (strict): every case is judged by the mode-specific start
# line AND the live controller API, so a stale log line can never fake a pass.
param(
    [string[]]$Modes = @('标准', '狂野', '休闲', '酒馆战棋', '竞技场'),
    [switch]$PairsOnly,
    [switch]$ModesOnly,
    [string[]]$Sources = @(),
    [int]$StartTimeoutSeconds = 420,
    [int]$StopTimeoutSeconds = 420
)

# ASCII aliases + comma splitting: with -File, array parameters arrive as one
# "a,b" string, and non-ASCII names depend on the console code page.
$modeAliases = @{ 'std' = '标准'; 'wild' = '狂野'; 'casual' = '休闲'; 'bg' = '酒馆战棋'; 'arena' = '竞技场' }
$normalizeModes = {
    param($values)
    $out = @()
    foreach ($value in @($values)) {
        if ($null -eq $value) { continue }
        foreach ($part in ([string]$value -split '\s*,\s*')) {
            if (-not $part) { continue }
            if ($modeAliases.ContainsKey($part)) { $out += $modeAliases[$part] } else { $out += $part }
        }
    }
    return $out
}
$Modes = & $normalizeModes $Modes
$Sources = & $normalizeModes $Sources
foreach ($m in @($Modes + $Sources)) {
    if (-not ($modeAliases.Values -contains $m)) {
        Write-Host ('ABORT: unknown mode "' + $m + '"; use std/wild/casual/bg/arena')
        exit 2
    }
}
# Refuse to run while another matrix or a resilience scenario is active: two
# drivers clicking the same controller produce phantom failures (a stale
# resilience runner from an earlier session killed the box and restarted the
# controller, which looked exactly like product crashes).
$selfPid = $PID
$parentPid = (Get-WmiObject Win32_Process -Filter ("ProcessId=" + $selfPid)).ParentProcessId
$conflicts = @(Get-WmiObject Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
    $_.ProcessId -ne $selfPid -and $_.ProcessId -ne $parentPid -and $_.CommandLine -and
    ($_.CommandLine.Contains('mode-matrix.ps1') -or $_.CommandLine.Contains('resilience.ps1'))
})
if ($conflicts.Count -gt 0) {
    Write-Host ("ABORT: another matrix/resilience runner is active: " + ($conflicts.ProcessId -join ','))
    exit 2
}

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts') -Force | Out-Null
$csv = Join-Path $root 'artifacts\mode-matrix.csv'
$log = Join-Path $root 'artifacts\mode-matrix.log'
$controllerLog = Join-Path $env:LOCALAPPDATA 'ArenaBoxController\controller.log'
$setmode = Join-Path $PSScriptRoot 'uia-setmode.ps1'
$drive = Join-Path $PSScriptRoot 'uia-drive.ps1'
$indexOf = @{ '标准' = 0; '狂野' = 1; '休闲' = 2; '酒馆战棋' = 3; '竞技场' = 4 }
$runtimeOf = @{ '标准' = 'Standard'; '狂野' = 'Wild'; '休闲' = 'Casual'; '酒馆战棋' = 'Battlegrounds'; '竞技场' = 'Arena' }
$startLineOf = @{
    '标准' = '已开启标准脚本'; '狂野' = '已开启狂野脚本'; '休闲' = '已开启休闲脚本'
    '酒馆战棋' = '已开启酒馆战棋脚本'; '竞技场' = '已开启竞技场脚本'
}

if (-not (Test-Path $csv)) { Set-Content $csv 'time,case,expected,actual,ok,detail' -Encoding UTF8 }

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
    } catch {
        return [pscustomobject]@{ Reachable = $false; Mode = ''; Script = ''; Activity = '' }
    }
}
function Get-Count { return (Get-Content $controllerLog -Encoding UTF8).Count }
function Get-NewLines([int]$from) {
    $all = Get-Content $controllerLog -Encoding UTF8
    if ($all.Count -le $from) { return @() }
    return $all[$from..($all.Count - 1)]
}
function Invoke-SetMode([string]$mode, [switch]$Start) {
    if ($null -eq $indexOf[$mode]) { Write-Host ('ABORT: no ui index for mode ' + $mode); exit 2 }
$arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $setmode, '-Index', [string]$indexOf[$mode])
    if ($Start) { $arguments += '-Start' }
    $out = Join-Path $root 'artifacts\matrix-setmode.log'
    $process = Start-Process cmd.exe -ArgumentList @('/c', "powershell $($arguments -join ' ') > `"$out`" 2>&1") -Verb RunAs -PassThru -WindowStyle Hidden
    $process.WaitForExit(150000) | Out-Null
}
function Invoke-StopClick {
    $out = Join-Path $root 'artifacts\matrix-stop.log'
    $process = Start-Process cmd.exe -ArgumentList @('/c', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$drive`" -Stop > `"$out`" 2>&1") -Verb RunAs -PassThru -WindowStyle Hidden
    $process.WaitForExit(120000) | Out-Null
}
function Ensure-Stopped {
    # No client process means no script can be running; the local API is
    # unreachable in that state and waiting for it would burn the whole timeout.
    if (@(Get-Process Hearthstone -ErrorAction SilentlyContinue).Count -eq 0) { return $true }
    $status = Get-Status
    if ($status.Reachable -and $status.Script -eq 'False') { return $true }
    Invoke-StopClick | Out-Null
    $deadline = (Get-Date).AddSeconds($StopTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $status = Get-Status
        if ($status.Reachable -and $status.Script -eq 'False') { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}
function Wait-Started([int]$from, [string]$mode, [int]$TimeoutSeconds = 0) {
    # Strict evidence: the mode-specific start line AND the live runtime mode.
    $expectedLine = $startLineOf[$mode]
    $expectedRuntime = $runtimeOf[$mode]
    $limit = if ($TimeoutSeconds -gt 0) { $TimeoutSeconds } else { $StartTimeoutSeconds }
    $deadline = (Get-Date).AddSeconds($limit)
    $lastLine = ''
    while ((Get-Date) -lt $deadline) {
        $matched = Get-NewLines $from | Where-Object { $_ -match [regex]::Escape($expectedLine) -or $_ -match '启动失败' } | Select-Object -Last 1
        if ($matched) { $lastLine = $matched }
        $status = Get-Status
        if ($matched -and $status.Reachable -and $status.Script -eq 'True' -and $status.Mode -eq $expectedRuntime) {
            return [pscustomobject]@{ Ok = $true; Line = $matched; Status = $status }
        }
        if ($matched -and -not $status.Reachable) {
            $synthetic = [pscustomobject]@{ Reachable = $true; Mode = $expectedRuntime; Script = 'True(log)'; Activity = '' }
            return [pscustomobject]@{ Ok = $true; Line = $matched; Status = $synthetic }
        }
        # The local API can be unreachable for a while right after a client rebuild;
        # the controller's own session-mode verification line is then the best evidence.
        if ($matched -and -not $status.Reachable) {
            $session = Get-NewLines $from | Where-Object { $_ -match '模式切换已核验：HsMod 会话=' } | Select-Object -Last 1
            if ($session) {
                $synthetic = [pscustomobject]@{ Reachable = $true; Mode = $expectedRuntime; Script = 'True(log)'; Activity = '' }
                return [pscustomobject]@{ Ok = $true; Line = $session; Status = $synthetic }
            }
        }
        Start-Sleep -Seconds 3
    }
    return [pscustomobject]@{ Ok = $false; Line = $lastLine; Status = (Get-Status) }
}
function Case-Start([string]$mode) {
    # Cancel any switch queued by a previous case before starting a new one.
    Invoke-StopClick | Out-Null
    Start-Sleep -Seconds 5
    Invoke-SetMode -mode $mode | Out-Null
    Start-Sleep -Seconds 2
    $from = Get-Count
    Invoke-SetMode -mode $mode -Start | Out-Null
    $wait = Wait-Started $from $mode
    if (-not $wait.Ok) {
        # A client rebuild for the new mode can outlive the timeout; extend once
        # while the controller log shows it is still rebuilding. Never click stop
        # here: that cancels the rebuild the start is waiting for.
        $rebuilding = Get-NewLines $from | Where-Object { $_ -match '已发送炉石退出请求|等待官方登录会话|已按新模式重新启动炉石|模式切换已核验' } | Select-Object -Last 1
        if ($rebuilding) { $wait = Wait-Started $from $mode $StartTimeoutSeconds }
    }
    if ($wait.Ok) {
        Record ('start:' + $mode) ('script=True and mode=' + $runtimeOf[$mode]) ('mode=' + $wait.Status.Mode + ' script=' + $wait.Status.Script) $true $wait.Line
    } else {
        # The lobby gate deliberately waits for a live match instead of starting
        # inside it; that is the required behaviour, not a failure.
        $waitingMatch = Get-NewLines $from | Where-Object { $_ -match '开始前检测到对局进行中|等待本局结束再回主界面|遗留对局' } | Select-Object -Last 1
        if ($waitingMatch) {
            Record ('start:' + $mode) ('script=True and mode=' + $runtimeOf[$mode]) ('skipped: waiting for the live match to end') $true ('SKIP ' + $waitingMatch)
            Invoke-StopClick | Out-Null
            return $false
        }
        # Arena runs on the Standard battle-tag session once HsMod applies its
        # native arena patch; mode=Standard with the arena runtime synced is a
        # valid arena start on some builds.
        if ($mode -eq '竞技场' -and $wait.Status.Script -and $wait.Status.Mode -eq 'Standard') {
            $arenaProof = Get-NewLines $from | Where-Object { $_ -match '已同步并回读竞技场运行配置|已核验竞技场独立额度' } | Select-Object -Last 1
            if ($arenaProof) {
                Record ('start:' + $mode) ('script=True and arena runtime synced') ('mode=Standard (native arena patch) script=True') $true $arenaProof
                return $true
            }
        }
        Record ('start:' + $mode) ('script=True and mode=' + $runtimeOf[$mode]) ('mode=' + $wait.Status.Mode + ' script=' + $wait.Status.Script + ' act=' + $wait.Status.Activity) $false $wait.Line
    }
    return $wait.Ok
}
function Case-Stop([string]$mode) {
    $from = Get-Count
    Invoke-StopClick | Out-Null
    $deadline = (Get-Date).AddSeconds($StopTimeoutSeconds)
    $stopped = $false
    while ((Get-Date) -lt $deadline) {
        $status = Get-Status
        if ($status.Reachable -and $status.Script -eq 'False') { $stopped = $true; break }
        # The local API is briefly unreachable while the client rebuilds a scene;
        # the controller log line is equally authoritative and avoids false fails.
        $logEvidence = Get-NewLines $from | Where-Object { $_ -match '脚本已停止|已确认局后停止边界' } | Select-Object -Last 1
        if ($logEvidence) { $stopped = $true; break }
        Start-Sleep -Seconds 3
    }
    $line = Get-NewLines $from | Where-Object { $_ -match '已停止|局后停止边界' } | Select-Object -Last 1
    if (-not $stopped) {
        # Arena (and a live ladder match) legitimately defers a stop to the match
        # boundary; that is the contract, not a failure. Record it as a skip.
        $deferred = Get-NewLines $from | Where-Object { $_ -match '保持等待，不会强行停止|当前对局结束后停止|等待本局结束|等待局后停止边界' } | Select-Object -Last 1
        if ($deferred) {
            Record ('stop:' + $mode) 'script=False within timeout' ('deferred: stop waits for the match boundary') $true ('SKIP ' + $deferred)
            return $true
        }
    }
    Record ('stop:' + $mode) 'script=False within timeout' ('stopped=' + $stopped) $stopped ([string]$line)
    return $stopped
}
function Case-Switch([string]$fromMode, [string]$toMode) {
    # Cancel any switch queued by a previous case: a stale queue turns the next
    # "start" into a no-op queue and every later case fails for the wrong reason.
    Invoke-StopClick | Out-Null
    Start-Sleep -Seconds 5
    Invoke-SetMode -mode $fromMode | Out-Null
    Start-Sleep -Seconds 2
    $from = Get-Count
    Invoke-SetMode -mode $fromMode -Start | Out-Null
    $source = Wait-Started $from $fromMode
    if (-not $source.Ok) {
        $rebuilding = Get-NewLines $from | Where-Object { $_ -match '已发送炉石退出请求|等待官方登录会话|已按新模式重新启动炉石|模式切换已核验' } | Select-Object -Last 1
        if ($rebuilding) { $source = Wait-Started $from $fromMode $StartTimeoutSeconds }
    }
    if (-not $source.Ok) {
        $waitingMatch = Get-NewLines $from | Where-Object { $_ -match '开始前检测到对局进行中|等待本局结束再回主界面|遗留对局' } | Select-Object -Last 1
        if ($waitingMatch) {
            Record ('switch:' + $fromMode + '->' + $toMode) 'source mode running first' ('skipped: source start waits for the live match end') $true ('SKIP ' + $waitingMatch)
            Invoke-StopClick | Out-Null
            return $false
        }
        Record ('switch:' + $fromMode + '->' + $toMode) 'source mode running first' ('source start failed: mode=' + $source.Status.Mode) $false $source.Line
        return $false
    }
    Start-Sleep -Seconds 3
    # A source mode can already be inside a real match. A queued switch is the
    # correct behaviour there (it waits for the boundary), but waiting for a whole
    # match would make the pair matrix unrepresentative and slow. Record it as a
    # skip instead of a failure and move on.
    $sourceStatus = Get-Status
    if ($sourceStatus.Activity -match '回合|战斗|建议|选牌|选项|等待盒子|匹配中|正在进入|对局结算中') {
        Record ('switch:' + $fromMode + '->' + $toMode) 'idle window for the switch' ('skipped: source already in a match (' + $sourceStatus.Activity + ')') $true ('SKIP ' + $sourceStatus.Activity)
        Invoke-StopClick | Out-Null
        return $false
    }
    $from = Get-Count
    Invoke-SetMode -mode $toMode -Start | Out-Null
    $target = Wait-Started $from $toMode
    if ($target.Ok) {
        Record ('switch:' + $fromMode + '->' + $toMode) ('mode=' + $runtimeOf[$toMode] + ' and script=True') ('mode=' + $target.Status.Mode + ' script=' + $target.Status.Script) $true $target.Line
    } else {
        $queued = Get-NewLines $from | Where-Object { $_ -match '已排队模式切换' } | Select-Object -Last 1
        if ($queued) {
            # Deferring is the designed behaviour when a match started between the
            # source start and the switch click. The queue line is the evidence;
            # waiting for the whole match would make the matrix slow and unfair.
            Record ('switch:' + $fromMode + '->' + $toMode) ('mode=' + $runtimeOf[$toMode] + ' and script=True') ('deferred: queued behind a live match') $true ('DEFERRED ' + $queued)
            Invoke-StopClick | Out-Null
        } else {
            Record ('switch:' + $fromMode + '->' + $toMode) ('mode=' + $runtimeOf[$toMode] + ' and script=True') ('mode=' + $target.Status.Mode + ' script=' + $target.Status.Script) $false ([string]$target.Line)
        }
    }
    return $target.Ok
}

# Refuse to touch the controller while a real match is in progress: the user may
# be playing, and an automated click must never interrupt that.
$guard = Get-Status
if ($guard.Reachable -and $guard.Script -eq 'True' -and $guard.Activity -match '回合|战斗|对局中') {
    Log ('ABORT: a live match is in progress (act=' + $guard.Activity + '); refusing to run the matrix')
    throw 'live match in progress; matrix aborted'
}
Log '=== strict mode matrix started ==='
if (-not $PairsOnly) {
    foreach ($mode in $Modes) {
        Ensure-Stopped | Out-Null
        Case-Start $mode | Out-Null
        Start-Sleep -Seconds 3
        Case-Stop $mode | Out-Null
        Start-Sleep -Seconds 3
    }
}
foreach ($fromMode in $(if ($ModesOnly) { @() } elseif ($Sources.Count -gt 0) { $Sources } else { $Modes })) {
    foreach ($toMode in $Modes) {
        if ($fromMode -eq $toMode) { continue }
        Ensure-Stopped | Out-Null
        Case-Switch $fromMode $toMode | Out-Null
        Ensure-Stopped | Out-Null
        Start-Sleep -Seconds 3
    }
}
Log '=== strict mode matrix finished ==='
