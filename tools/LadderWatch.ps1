<#
.SYNOPSIS
  Read the native ladder ledger and report progress toward Legend.

.EXAMPLE
  pwsh -File .\tools\LadderWatch.ps1
#>
[CmdletBinding()]
param([string]$Csv = 'D:\Hearthstone\BepInEx\HsMod\ladder_session.csv')

if (-not (Test-Path $Csv)) { Write-Host "NO_LEDGER $Csv"; exit 2 }
$rows = Get-Content $Csv -Encoding UTF8 | Select-Object -Skip 1 | ForEach-Object {
    $p = $_ -split ';'
    [pscustomobject]@{ Time = $p[0]; Mode = $p[1]; Win = $p[2]; Rank = $p[3]; Stars = $p[4]
        Legend = $p[5]; RankAfter = $p[6]; StarsAfter = $p[7]; LegendAfter = $p[8] }
}
$std = $rows | Where-Object { $_.Mode -eq 'standard' }
$wins = @($std | Where-Object { $_.Win -eq '1' }).Count
$losses = @($std | Where-Object { $_.Win -eq '0' }).Count
$last = $std | Select-Object -Last 1
Write-Host ("标准天梯: {0} 胜 / {1} 负 / {2} 局" -f $wins, $losses, $std.Count)
if ($last) {
    $legendNow = ($last.LegendAfter -ne '0' -and $last.LegendAfter -ne '')
    $rankText = "{0}.{1}" -f $last.RankAfter, $last.StarsAfter
    Write-Host ("最近结果: {0}; 段位 {1}; 时间 {2}" -f ($(if ($last.Win -eq '1') { '胜' } else { '负' })), $rankText, $last.Time)
    if ($legendNow) { Write-Host ("传说名次: {0}" -f $last.LegendAfter); Write-Host 'LEGEND' }
    else { Write-Host '尚未传说（段位 51 即传说）' }
}
# A short recent form line helps spot a losing streak.
$recent = $std | Select-Object -Last 8
Write-Host ("近 8 局: " + (($recent | ForEach-Object { if ($_.Win -eq '1') { 'W' } else { 'L' } }) -join ' '))
