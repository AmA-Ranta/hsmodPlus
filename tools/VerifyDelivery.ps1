# Verify the delivery zip: required files, internal SHA256SUMS, and that the
# payload matches what this repository just built.
param(
    [string]$Zip = '',
    [switch]$SkipPayloadMatch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Zip)) {
    $Zip = Get-ChildItem (Join-Path $root '交付') -Filter 'hsmodPlus-*.zip' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not (Test-Path $Zip)) { throw "交付包不存在：$Zip" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
try {
    # Zip entries on Windows may use backslashes; normalise so the manifest and
    # the payload compare on the same separator.
    $entryMap = @{}
    foreach ($item in $archive.Entries) { $entryMap[$item.FullName.Replace('\', '/')] = $item }
    $names = @($entryMap.Keys)
    $required = @(
        'hsmodPlus.exe',
        'ArenaBoxBot.dll',
        'HsMod.dll',
        'version.dll',
        'UniverseLib.Mono.dll',
        'winhttp.dll',
        'doorstop_config.ini',
        'RankBypass.ini.template',
        'SHA256SUMS.txt',
        '安装说明.txt'
    )
    # Verify the complete loader payload, not just the controller/plugin DLLs.
    # In particular doorstop_config.ini points at BepInEx/UnstrippedCorlib.
    $runtimeRoot = Join-Path $root 'pkg\Hearthstone\BepInEx'
    $runtimeRequired = @('BepInEx/core/BepInEx.Preloader.dll', 'BepInEx/UnstrippedCorlib/mscorlib.dll',
        'BepInEx/UnstrippedCorlib/Newtonsoft.Json.dll', 'BepInEx/UnstrippedCorlib/UniTask.dll')
    foreach ($runtimeFile in (Get-ChildItem $runtimeRoot -Recurse -File)) {
        $runtimeRequired += 'BepInEx/' + $runtimeFile.FullName.Substring($runtimeRoot.Length + 1).Replace('\', '/')
    }
    $required = @($required + $runtimeRequired | Sort-Object -Unique)
    $missing = $required | Where-Object { $names -notcontains $_ }
    if ($missing) { throw ('交付包缺少文件：' + ($missing -join '、')) }

    $sumEntry = $entryMap['SHA256SUMS.txt']
    $reader = New-Object System.IO.StreamReader($sumEntry.Open(), [System.Text.Encoding]::UTF8)
    $sumText = $reader.ReadToEnd()
    $reader.Dispose()

    $expected = @{}
    foreach ($line in ($sumText -split "`r?`n")) {
        $match = [regex]::Match($line, '^([0-9A-Fa-f]{64})\s+(.+)$')
        if ($match.Success) { $expected[$match.Groups[2].Value.Trim().Replace('\', '/')] = $match.Groups[1].Value.ToUpperInvariant() }
    }

    $failures = New-Object System.Collections.Generic.List[string]
    foreach ($name in $required) {
        if ($name -ne 'SHA256SUMS.txt' -and -not $expected.ContainsKey($name)) {
            $failures.Add('必需文件未列入校验清单：' + $name)
        }
    }
    foreach ($name in $expected.Keys) {
        $entry = $entryMap[$name]
        if (-not $entry) { $failures.Add('清单列出但包内缺失：' + $name); continue }
        $stream = $entry.Open()
        $memory = New-Object System.IO.MemoryStream
        $stream.CopyTo($memory)
        $stream.Dispose()
        $actual = ([System.Security.Cryptography.SHA256]::Create().ComputeHash($memory.ToArray()) |
            ForEach-Object { $_.ToString('X2') }) -join ''
        $memory.Dispose()
        if ($actual -ne $expected[$name]) { $failures.Add(('哈希不符：' + $name)) }
    }

    if (-not $SkipPayloadMatch) {
        $exe = Join-Path $root 'artifacts\publish-wpf\hsmodPlus.exe'
        if (Test-Path $exe) {
            $local = (Get-FileHash $exe -Algorithm SHA256).Hash.ToUpperInvariant()
            if ($expected.ContainsKey('hsmodPlus.exe') -and $expected['hsmodPlus.exe'] -ne $local) {
                $failures.Add('包内 hsmodPlus.exe 与本次构建产物不一致')
            }
        }
        $plugin = Join-Path $root 'ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll'
        if (Test-Path $plugin) {
            $local = (Get-FileHash $plugin -Algorithm SHA256).Hash.ToUpperInvariant()
            if ($expected.ContainsKey('ArenaBoxBot.dll') -and $expected['ArenaBoxBot.dll'] -ne $local) {
                $failures.Add('包内 ArenaBoxBot.dll 与本次构建产物不一致')
            }
        }
    }

    $hash = (Get-FileHash $Zip -Algorithm SHA256).Hash
    $sizeMb = [math]::Round((Get-Item $Zip).Length / 1MB, 1)
    Write-Host ('ZIP     : ' + $Zip)
    Write-Host ('SHA256  : ' + $hash)
    Write-Host ('FILES   : ' + $names.Count + '（' + $sizeMb + ' MB）')
    foreach ($name in ($required | Sort-Object)) {
        $value = if ($expected.ContainsKey($name)) { $expected[$name] } else { '(未列入清单)' }
        Write-Host ('  ' + $name.PadRight(26) + $value)
    }
    if ($failures.Count -gt 0) {
        $failures | ForEach-Object { Write-Host ('FAIL: ' + $_) }
        throw ('交付包校验失败：' + $failures.Count + ' 项')
    }
    Write-Host 'PASS: delivery package verified'
}
finally {
    $archive.Dispose()
}
