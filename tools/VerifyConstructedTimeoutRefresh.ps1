[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$Native='D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [switch]$ExpectLegacyReset
)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes('D:\Hearthstone\BepInEx\core\Mono.Cecil.dll')) | Out-Null
$p=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$n=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Native)
try {
    $owner=$p.MainModule.GetType('ArenaBoxBot.HsModNativeArenaPatch')
    $guard=$owner.Methods | Where-Object Name -eq 'EndTurnFallbackGuard'
    $refresh=$owner.Methods | Where-Object Name -eq 'TryRecoverConstructedTimeout'
    $legacy=@($guard.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -in @('s_constructedTimeoutStreak','s_constructedLastTimeoutAt') })
    if($ExpectLegacyReset){
        if($legacy.Count -ne 2){throw 'Negative control must contain both old unconditional resets after void fallback'}
        'PASS negative control: actual R26 resets its recovery budget after a native hover-only return.'
        return
    }
    if($legacy.Count){throw 'Native fallback invocation must not reset a consumed refresh budget'}
    $policy=$p.MainModule.GetType('ArenaBoxBot.ConstructedTimeoutRefreshBudget')
    if(-not $policy){throw 'Missing authoritative-turn budget'}
    $calls=@($refresh.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    foreach($required in @('GetGameHandle','GetTurn','GetFriendlySidePlayer','GetOpposingSidePlayer','GetTag','IsGameCreated','IsGameOverNowOrPending','WasConcedeRequested','IsMulliganManagerActive','Decide')){
        if($calls -notcontains $required){throw "Missing authoritative turn guard: $required"}
    }
    $decide=$policy.Methods | Where-Object Name -eq 'Decide'
    if(-not $decide){throw 'Missing unknown-turn hold decision'}
    $decisionCalls=@($decide.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    if($decisionCalls -notcontains 'IsNullOrEmpty' -or $decisionCalls -notcontains 'TryRefresh'){throw 'Unknown turn and consumed budget must be separate decisions'}
    if(@($refresh.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'ReadCurrent'}).Count){throw 'Refresh must not call an extra stateful native reader'}
    $fallback=$n.MainModule.GetType('HsMod.WildLadderBot').Methods|Where-Object Name -eq 'ContinueEndTurnFallback'
    $send=$n.MainModule.GetType('HsMod.WildLadderBot').Methods|Where-Object Name -eq 'TrySendEndTurnWithoutLocalConcede'
    if($fallback.ReturnType.FullName -ne 'System.Void' -or $send.ReturnType.FullName -ne 'System.Boolean'){throw 'Native contract changed'}
    $sendCalls=@($send.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    foreach($required in @('ReadTurnSecondsRemaining','SendShadowState','SendOption','get_ID')){if($sendCalls -notcontains $required){throw "Native hover/packet/expiry/submit contract missing: $required"}}
    if(@($send.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'stsfld' -and $_.Operand.Name -eq 's_pendingEndTurnOptionsId'}).Count -lt 2){throw 'Native pending hover latch contract changed'}
    foreach($method in $policy.Methods){if(-not $method.HasBody){continue};foreach($i in $method.Body.Instructions){
        if($i.Operand -is [Mono.Cecil.FieldReference] -and $i.OpCode.Name -in @('stfld','stsfld') -and $i.Operand.DeclaringType.FullName -ne $policy.FullName){throw 'Budget may not write native state'}
    }}
    'PASS: unknown ownership holds separately from consumed own-turn refresh; no reset after native void fallback, no extra ReadCurrent; native hover/packet/expiry/submit contract retained.'
    'Plugin SHA256: '+(Get-FileHash -LiteralPath $Plugin).Hash
} finally {$p.Dispose();$n.Dispose()}
