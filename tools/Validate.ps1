<#
.SYNOPSIS
  Repository validation: build the solution and run the non-arena test suites.

.DESCRIPTION
  Arena behaviour is intentionally out of scope for this run (task instruction
  "不要测试竞技场").  Arena projects and game/IL-only verifiers stay in the
  repository but are not part of the default validation.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$env:TEMP = Join-Path $Root 'artifacts\test-temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null

Write-Host '[1/3] 编译前端与插件'
dotnet build (Join-Path $Root 'ArenaController.Wpf\ArenaController.Wpf.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '前端构建失败' }
dotnet build (Join-Path $Root 'ArenaBoxBot\ArenaBoxBot.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '插件构建失败' }

Write-Host '[2/3] 运行非竞技场回归'
$projects = @(
    'tests\ControllerConfiguration\ControllerConfiguration.csproj',
    'tests\ProcessDiscovery\ProcessDiscovery.csproj',
    'tests\BattleNetLoginWait\BattleNetLoginWait.csproj',
    'tests\PluginModeCatalog\PluginModeCatalog.csproj',
    'tests\MemoryCleanup\MemoryCleanup.csproj',
    'tests\StopConditions\StopConditions.csproj',
    'tests\PopupPolicy\PopupPolicy.csproj'
)
foreach ($project in $projects) {
    Write-Host ("  -> " + $project)
    dotnet run --project (Join-Path $Root $project) -c Release
    if ($LASTEXITCODE -ne 0) { throw ("测试失败: " + $project) }
}

Write-Host '[3/3] PASS 非竞技场回归全部通过'
