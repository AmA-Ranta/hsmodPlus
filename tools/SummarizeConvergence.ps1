# Aggregate convergence evidence (mode matrix + resilience) into one markdown report.
param(
    [string]$Matrix = '',
    [string]$Resilience = '',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Matrix)) { $Matrix = Join-Path $root 'artifacts\mode-matrix.csv' }
if ([string]::IsNullOrWhiteSpace($Resilience)) { $Resilience = Join-Path $root 'artifacts\resilience.csv' }
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = Join-Path $root 'artifacts\convergence-report.md' }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 收敛测试汇总（' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + '）')
$lines.Add('')
$lines.Add('由 tools\SummarizeConvergence.ps1 从 CSV 证据生成，不手工编辑。')
$lines.Add('')

function Add-Section([string]$title, [string]$path) {
    $lines.Add('## ' + $title)
    $lines.Add('')
    if (-not (Test-Path $path)) {
        $lines.Add('（缺少证据文件：' + $path + '）')
        $lines.Add('')
        return
    }
    $rows = Import-Csv $path
    $skips = @($rows | Where-Object { $_.ok -eq 'True' -and $_.detail -like 'SKIP*' })
    $fails = @($rows | Where-Object { $_.ok -ne 'True' })
    $passes = @($rows | Where-Object { $_.ok -eq 'True' -and $_.detail -notlike 'SKIP*' })
    $lines.Add(('总计 {0}：通过 {1}，跳过 {2}，失败 {3}' -f $rows.Count, $passes.Count, $skips.Count, $fails.Count))
    $lines.Add('')
    if ($fails.Count -gt 0) {
        $lines.Add('| 时间 | 用例 | 实际 |')
        $lines.Add('|---|---|---|')
        foreach ($row in $fails) {
            $lines.Add('| ' + $row.time + ' | ' + $row.case + ' | ' + $row.actual + ' |')
        }
        $lines.Add('')
    }
    if ($skips.Count -gt 0) {
        $lines.Add('跳过用例（源模式已在对局中，切换会被正确延后，不计失败）：')
        $lines.Add('')
        foreach ($row in $skips) {
            $lines.Add('- ' + $row.case + '：' + $row.actual)
        }
        $lines.Add('')
    }
    if ($passes.Count -gt 0) {
        $lines.Add('通过用例：')
        $lines.Add('')
        foreach ($row in $passes) {
            $lines.Add('- ' + $row.time + ' ' + $row.case + ' → ' + $row.actual)
        }
        $lines.Add('')
    }
}

Add-Section '模式矩阵（启停 + 两两切换）' $Matrix
Add-Section '韧性场景（杀游戏 / 杀盒子 / 重启中控）' $Resilience

$lines | Set-Content $Out -Encoding UTF8
Write-Host ('report written: ' + $Out)
Write-Host ''
$lines | ForEach-Object { Write-Host $_ }
