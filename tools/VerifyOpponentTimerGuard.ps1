[CmdletBinding()]
param([string]$Plugin = 'D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll', [switch]$ExpectLegacyOwnerBlind)
$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes('D:\Hearthstone\BepInEx\core\Mono.Cecil.dll')) | Out-Null
$a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
try {
    $type = $a.MainModule.GetType('ArenaBoxBot.NativeOpponentTimerGuard')
    if ($ExpectLegacyOwnerBlind) {
        if ($type) { throw 'Negative control already has timer ownership guard' }
        'PASS negative control: actual R24 has no expired-opponent timer guard.'
        return
    }
    if (-not $type) { throw 'Timer ownership guard missing' }
    $hook = $type.Methods | Where-Object Name -eq 'EnsureInstalled'
    $post = $type.Methods | Where-Object Name -eq 'AfterRemaining'
    foreach ($name in @('ReadTurnSecondsRemaining','m_currentTimerBelongsToFriendlySidePlayer')) {
        if (-not @($hook.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $name }).Count) { throw "Exact native contract missing: $name" }
    }
    $calls = @($post.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
    foreach ($name in @('IsGameCreated','IsGameOverNowOrPending','WasConcedeRequested','IsMulliganManagerActive','GetResponseMode','GetOptionsPacket','get_List','GetFriendlySidePlayer','GetOpposingSidePlayer','GetTag','HasCountdownTimeout','GetCurrentTurnTimerState','Remaining')) {
        if ($calls -notcontains $name) { throw "Current own option/timer safety missing: $name" }
    }
    foreach ($name in @('ReadCurrent','IsBlocked','AbortShadowCommit','Reset','EndTurn','SendOption','SetValue','SetTag','Kill')) {
        if ($calls -contains $name) { throw "Timer guard must not alter protocol/ledger/timer state or send actions: $name" }
    }
    foreach ($i in $post.Body.Instructions) {
        if ($i.Operand -is [Mono.Cecil.FieldReference] -and $i.OpCode.Name -in @('stfld','stsfld') -and $i.Operand.DeclaringType.FullName -ne $type.FullName) { throw "Guard writes external field: $i" }
    }
    if (-not @($post.Body.Instructions | Where-Object OpCode -EQ ([Mono.Cecil.Cil.OpCodes]::Stind_R4)).Count) { throw 'Postfix must update only its returned remaining value' }
    'PASS: current own OPTION/list/game/side guards; exact private timer owner; only expired opponent0 becomes unknown, no ledger/read/dispatch or native timer writes.'
    'Plugin SHA256: ' + (Get-FileHash -LiteralPath $Plugin).Hash
}
finally { $a.Dispose() }
