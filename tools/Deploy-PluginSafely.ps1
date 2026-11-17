# Waits for a safe boundary (no match in progress) before touching the game.
# Rule: never kill/restart Hearthstone while a match is being played.
param(
  [int]$TimeoutMinutes = 180,
  [switch]$Deploy,
  [string]$PluginSource = 'D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
  [string]$PluginTarget = 'D:\Hearthstone\BepInEx\plugins\ArenaBoxBot.dll'
)
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
  $gameplay = $true
  try { $gameplay = [bool](Get-Content 'D:\Hearthstone\BepInEx\HsMod\controller-scene.json' -Raw -Encoding UTF8 | ConvertFrom-Json).gameplay } catch { $gameplay = $true }
  if (-not $gameplay) {
    "safe at $((Get-Date).ToString('HH:mm:ss')) gameplay=false" | Write-Output
    if ($Deploy) {
      Get-Process hsmodPlus -ErrorAction SilentlyContinue | ForEach-Object { try { Stop-Process -Id $_.Id -Force } catch {} }
      Get-Process Hearthstone -ErrorAction SilentlyContinue | ForEach-Object { try { Stop-Process -Id $_.Id -Force } catch {} }
      Start-Sleep -Seconds 5
      Copy-Item $PluginSource $PluginTarget -Force
      "deployed $((Get-FileHash $PluginTarget -Algorithm SHA256).Hash.Substring(0,16))" | Write-Output
    }
    exit 0
  }
  "match in progress at $((Get-Date).ToString('HH:mm:ss')); waiting" | Write-Output
  Start-Sleep -Seconds 60
}
"timed out waiting for a safe boundary" | Write-Output
exit 1
