[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Package
)

$ErrorActionPreference = 'Stop'
$packagePath = [IO.Path]::GetFullPath($Package)
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "找不到安装包：$packagePath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = $null
try {
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    $manifestEntry = $archive.GetEntry('PACKAGE_SHA256SUMS.txt')
    if ($null -eq $manifestEntry) { throw '安装包缺少 PACKAGE_SHA256SUMS.txt' }

    $manifest = @{}
    $reader = New-Object IO.StreamReader($manifestEntry.Open(), [Text.Encoding]::UTF8)
    try { $lines = $reader.ReadToEnd() -split "`r?`n" }
    finally { $reader.Dispose() }

    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9A-F]{64})  (.+)$') {
            throw "清单格式错误：$line"
        }
        $path = $Matches[2]
        if ($manifest.ContainsKey($path)) { throw "清单存在重复条目：$path" }
        $manifest[$path] = $Matches[1]
    }

    $sha = [Security.Cryptography.SHA256]::Create()
    $entryNames = @{}
    $checked = 0
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -eq 'PACKAGE_SHA256SUMS.txt') { continue }
        $path = $entry.FullName.Replace('\', '/')
        # Check file extensions, not name fragments: Mono.Cecil.Pdb.dll and
        # the Czech runtime resource directory "cs" are required runtime files.
        if ($path -match '(?i)\.(cs|csproj|sln|slnx|pdb|ps1|py|log|bak)$' -or
            $path -match '(?i)(^|/)(\.git|\.vs|bin|obj|tests|artifacts|InstallBackups|logs|cache)(/|$)' -or
            $path -match '(?i)^(README\.md|NATIVE_ARENA\.md)$' -or
            $path -match '(?i)^BepInEx/core/(0Harmony|BepInEx(\.Harmony|\.Preloader)?|MonoMod\.(RuntimeDetour|Utils))\.xml$' -or
            ($path -match '(?i)^BepInEx/config/' -and $path -ne 'BepInEx/config/HsMod.cfg.template')) {
            throw "安装包包含开发文件或本机运行记录：$path"
        }
        $entryNames[$path] = $true
        if (-not $manifest.ContainsKey($path)) { throw "清单缺少文件：$path" }
        $stream = $entry.Open()
        try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
        finally { $stream.Dispose() }
        if (-not [string]::Equals($actual, $manifest[$path], [StringComparison]::OrdinalIgnoreCase)) {
            throw "清单哈希不一致：$path（期望 $($manifest[$path])，实际 $actual）"
        }
        $checked++
    }

    $listedMissing = @($manifest.Keys | Where-Object { -not $entryNames.ContainsKey($_) })
    if ($listedMissing.Count -gt 0) { throw "清单包含包内不存在的文件：$($listedMissing -join '、')" }
    # Compress-Archive on Windows PowerShell may preserve backslashes in
    # nested entry names, while .NET ZipFile on other runtimes emits '/'.
    # Treat both spellings as the same portable package path.
    $templateEntry = $archive.Entries |
        Where-Object { $_.FullName.Replace('\', '/') -eq 'BepInEx/config/HsMod.cfg.template' } |
        Select-Object -First 1
    if ($null -eq $templateEntry) { throw '安装包缺少 HsMod 配置模板' }
    $reader = New-Object IO.StreamReader($templateEntry.Open(), [Text.Encoding]::UTF8)
    try { $template = $reader.ReadToEnd() }
    finally { $reader.Dispose() }
    foreach ($setting in @('Hearthstone Log', 'Match Log', '卡组名称')) {
        $values = [regex]::Matches($template, '(?m)^' + [regex]::Escape($setting) + '[ \t]*=([^\r\n]*)')
        if ($values.Count -ne 1 -or -not [string]::IsNullOrWhiteSpace($values[0].Groups[1].Value)) {
            throw "安装包默认配置包含本机残留或格式不正确：$setting 应保留为空"
        }
    }
    Write-Output "PASS: package manifest hashes ($checked entries)"
    Write-Output 'PASS: release payload excludes development files and local configuration leftovers'
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
}
