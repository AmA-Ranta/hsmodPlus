# Summarise artifacts\mode-matrix.csv into a markdown table for the delivery docs.
param([string]$Csv = '', [switch]$FailsOnly)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Csv)) { $Csv = Join-Path $root 'artifacts\mode-matrix.csv' }
if (-not (Test-Path $Csv)) { throw "找不到矩阵结果：$Csv" }

$rows = Import-Csv $Csv
$total = $rows.Count
$passed = ($rows | Where-Object { $_.ok -eq 'True' }).Count
$failed = $total - $passed

Write-Host ('# 模式矩阵结果（' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + '）')
Write-Host ''
Write-Host ('总计 ' + $total + ' 用例：通过 ' + $passed + '，失败 ' + $failed)
Write-Host ''
Write-Host '| 时间 | 用例 | 预期 | 实际 | 判定 |'
Write-Host '|---|---|---|---|---|'
$selected = if ($FailsOnly) { $rows | Where-Object { $_.ok -ne 'True' } } else { $rows }
foreach ($row in $selected) {
    $verdict = if ($row.ok -eq 'True') { 'PASS' } else { 'FAIL' }
    Write-Host ('| ' + $row.time + ' | ' + $row.case + ' | ' + $row.expected + ' | ' + $row.actual + ' | ' + $verdict + ' |')
}
