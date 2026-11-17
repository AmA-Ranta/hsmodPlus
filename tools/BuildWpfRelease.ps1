<#
.SYNOPSIS
  Build the single, unified delivery zip for the WPF controller.

.DESCRIPTION
  The controller engine lives in ArenaController (hsmodPlus.Core) and the
  desktop front-end in ArenaController.Wpf.  This script builds the plugin,
  publishes the front-end as a self-contained single-file hsmodPlus.exe,
  stages a clean payload from this repository (pkg/ + pkg/Native/) and writes
  one zip under 交付/.  No dev artifacts, backups or previews are shipped.

  Repository inputs:
    pkg/Hearthstone/      BepInEx runtime, winhttp.dll, doorstop_config.ini
    pkg/Box/              RankBypass.ini.template
    pkg/UniverseLib.Mono.dll
    pkg/Native/              HsMod.dll + version.dll
    ArenaBoxBot/          plugin source (net48)
    ArenaController.Wpf/  WPF front-end
    ArenaController/      engine (hsmodPlus.Core)
#>
[CmdletBinding()]
param(
    [string]$Version = (Get-Date -Format 'yyyyMMdd'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$delivery = Join-Path $Root '交付'
$staging = Join-Path $Root ('artifacts\staging-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $Root 'artifacts\publish-wpf'
$pluginOut = Join-Path $Root 'ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll'
$zipName = "hsmodPlus-$Version.zip"
$zip = Join-Path $delivery $zipName

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'Validate.ps1')
    if ($LASTEXITCODE -ne 0) { throw '仓库校验失败' }
}

Write-Host '[1/4] 构建插件 ArenaBoxBot.dll'
dotnet build (Join-Path $Root 'ArenaBoxBot\ArenaBoxBot.csproj') -c Release
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $pluginOut)) { throw '插件构建失败' }

Write-Host '[2/4] 发布 WPF 前端单文件 exe'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $Root 'ArenaController.Wpf\ArenaController.Wpf.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o $publish
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $publish 'hsmodPlus.exe'))) { throw 'WPF 发布失败' }

Write-Host '[3/4] 组装干净载荷（仅运行必需文件）'
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$hs = Join-Path $Root 'pkg\Hearthstone'
Copy-Item (Join-Path $publish 'hsmodPlus.exe') $staging
Copy-Item $pluginOut $staging
Copy-Item (Join-Path $Root 'pkg\UniverseLib.Mono.dll') $staging
Copy-Item (Join-Path $Root 'pkg/Native\HsMod.dll') $staging
Copy-Item (Join-Path $Root 'pkg/Native\version.dll') $staging
Copy-Item (Join-Path $hs 'winhttp.dll') $staging
Copy-Item (Join-Path $hs 'doorstop_config.ini') $staging
Copy-Item (Join-Path $Root 'pkg\Box\RankBypass.ini.template') $staging
New-Item -ItemType Directory -Path (Join-Path $staging 'BepInEx\core') -Force | Out-Null
Copy-Item (Join-Path $hs 'BepInEx\core\*') (Join-Path $staging 'BepInEx\core')
New-Item -ItemType Directory -Path (Join-Path $staging 'BepInEx\config') -Force | Out-Null
Copy-Item (Join-Path $hs 'BepInEx\config\*') (Join-Path $staging 'BepInEx\config') -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $staging 'BepInEx\patchers') -Force | Out-Null
Copy-Item (Join-Path $hs 'BepInEx\patchers\*') (Join-Path $staging 'BepInEx\patchers') -ErrorAction SilentlyContinue
# Doorstop explicitly resolves Mono assemblies from this directory. Omitting it
# can be masked by an existing installation but breaks a clean deployment.
Copy-Item (Join-Path $hs 'BepInEx\UnstrippedCorlib') (Join-Path $staging 'BepInEx\UnstrippedCorlib') -Recurse

$readme = @"
炉石脚本中控 · 整合包（$Version）
=====================================

运行：双击 hsmodPlus.exe（首次会请求管理员权限）。
安装插件：在「控制台 → 安装维护」确认炉石/盒子路径，点「安装 / 修复插件」。
安装前请完全退出炉石（Hearthstone.exe）与盒子（HSAng.exe）。

说明：
- 界面为 WPF-Fluent 桌面版（控制台 / 运行日志两页）；标题栏含 🛀 图标与窗口按钮。
- 中控与 version.dll / HsMod 版本解耦：安装时保留你现有的这两个文件，只安装/修复插件。
- 天梯 / 狂野 / 休闲 / 酒馆战棋只设对局数；竞技场额度与门票金币保护在竞技场独立块。
- 设置修改后需点「应用设置」；对局中会排队到本局结束生效。
"@
Set-Content (Join-Path $staging '安装说明.txt') $readme -Encoding UTF8

# Deployment manifest: the installer refuses unverified plugin sources, so the
# payload list is generated from the staged files themselves.
$sums = Get-ChildItem $staging -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($staging.Length + 1).Replace('\', '/')
    (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant() + '  ' + $relative
}
Set-Content (Join-Path $staging 'SHA256SUMS.txt') ($sums -join "`r`n") -Encoding UTF8

Write-Host '[4/4] 生成统一压缩包'
New-Item -ItemType Directory -Path $delivery -Force | Out-Null
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
# Fail the release if any runtime dependency or manifest entry was omitted.
& (Join-Path $PSScriptRoot 'VerifyDelivery.ps1') -Zip $zip
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host "PASS: $zip"
Write-Host "SHA256: $hash"
Write-Host ("Files: " + ((Get-Item $zip).Length / 1MB).ToString('0.0') + ' MB')
