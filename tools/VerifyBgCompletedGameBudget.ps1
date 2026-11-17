[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$NativeHsMod=(Join-Path $PSScriptRoot '..\pkg\Native\HsMod.dll'),
    [string]$Game='D:\Hearthstone\Hearthstone_Data\Managed\Assembly-CSharp.dll',
    [string]$CecilPath='D:\Hearthstone\BepInEx\core\Mono.Cecil.dll'
)
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
$pluginAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$nativeAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($NativeHsMod)
$gameAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Game)
try {
    $guard=$pluginAssembly.MainModule.GetType('ArenaBoxBot.NativeBgCompletedGameGuard')
    if(-not $guard){throw 'Missing terminal-based BG game budget guard'}
    $result=@($gameAssembly.MainModule.GetType('GameState').Methods | Where-Object {
        $_.Name -eq 'OnGameOver' -and $_.Parameters.Count -eq 1 -and $_.Parameters[0].ParameterType.FullName -eq 'TAG_PLAYSTATE'
    })
    $reset=@($nativeAssembly.MainModule.GetType('HsMod.BgSessionStats').Methods | Where-Object {
        $_.Name -eq 'ResetSessionCount' -and $_.IsStatic -and $_.Parameters.Count -eq 0 -and $_.ReturnType.FullName -eq 'System.Void'
    })
    if($result.Count -ne 1 -or $reset.Count -ne 1){throw 'Unsupported native terminal or explicit session reset contract'}
    $install=$guard.Methods | Where-Object Name -eq 'EnsureInstalled'
    $names=@($install.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldstr'} | ForEach-Object Operand)
    foreach($name in @('OnGameOver','ObserveGameOver','OnPowerHistory','ObserveServerComplete','ResetSessionCount','ResetSession','wildLadderStopAfterGames')){
        if($names -notcontains $name){throw "Missing native hook/config binding: $name"}
    }
    foreach($hook in @('ObserveGameOver','ObserveServerComplete')){
        $method=$guard.Methods | Where-Object Name -eq $hook
        if($method.ReturnType.FullName -ne 'System.Void' -or @($method.Parameters | Where-Object {$_.ParameterType.IsByReference}).Count){throw 'Completion hooks must only observe, not skip or replace native input'}
        $hookCalls=@($method.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]} | ForEach-Object {$_.Operand.Name})
        if($hookCalls -notcontains 'RecordEvidence'){throw "Missing independent completion evidence: $hook"}
    }
    $server=$guard.Methods | Where-Object Name -eq 'ObserveServerComplete'
    $serverCalls=@($server.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]} | ForEach-Object {$_.Operand.Name})
    foreach($name in @('GetGameEntity','GetEntityId')){if($serverCalls -notcontains $name){throw "Server COMPLETE must bind the actual game entity: $name"}}
    $observe=$guard.Methods | Where-Object Name -eq 'RecordEvidence'
    $observeCalls=@($observe.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]} | ForEach-Object {$_.Operand.Name})
    foreach($name in @('Eligible','Record','BlockNextMatchIfReached')){if($observeCalls -notcontains $name){throw "Missing terminal path: $name"}}
    $policy=$pluginAssembly.MainModule.GetType('ArenaBoxBot.BgCompletedGameBudget').Methods | Where-Object Name -eq 'Record'
    if($policy.Parameters.Count -ne 4 -or $policy.Parameters[3].Name -ne 'serverComplete'){throw 'Terminal budget requires independent server completion evidence'}
    $stop=$pluginAssembly.MainModule.GetType('ArenaBoxBot.ConstructedStopRuntime').Methods | Where-Object Name -eq 'BeforeFindGame'
    $calls=@($stop.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]} | ForEach-Object {$_.Operand.Name})
    if($calls[0] -ne 'BlockNextMatchIfReached' -or $calls -notcontains 'TryCompleteStop'){throw 'Quota must gate the existing next-match boundary and retain idle stop completion'}
    $request=$guard.Methods | Where-Object Name -eq 'BlockNextMatchIfReached'
    $requestCalls=@($request.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]} | ForEach-Object {$_.Operand.Name})
    foreach($name in @('Reached','ReadLimit','ToJson','Move')){if($requestCalls -notcontains $name){throw "Missing existing stop request path: $name"}}
    $fields=@($request.Body.Instructions | Where-Object {$_.Operand -is [Mono.Cecil.FieldReference] -and $_.OpCode.Name -eq 'stfld'} | ForEach-Object {$_.Operand.Name})
    foreach($name in @('GameProcessId','GameStartedAtUtcTicks','RequestedAtUtcTicks','Mode')){if($fields -notcontains $name){throw "Missing exact stop request identity: $name"}}
    foreach($method in $guard.Methods){if(-not $method.HasBody){continue};foreach($instruction in $method.Body.Instructions){
        $operand=$instruction.Operand
        if($operand -is [Mono.Cecil.MethodReference] -and $operand.Name -in @('SetRuntimeEnabled','SendOption','SendChoices','FindGame','FindGameInternal','EndCurrentTaskList','Dequeue','AddRecord')){throw ('Guard bypasses native stop/action/stat contract: '+$operand.Name)}
        if($operand -is [Mono.Cecil.FieldReference] -and $instruction.OpCode.Name -in @('stfld','stsfld') -and $operand.DeclaringType.FullName -match '^(GameState|PowerProcessor|HsMod\.)'){throw 'Guard writes native gameplay/stat fields'}
        if($instruction.OpCode.Name -eq 'ldstr' -and [string]$operand -match 'bg_session\.csv|ladder_session\.csv'){throw 'Terminal quota cannot depend on or invent a rating ledger'}
    }}
    'PASS: native BG terminal plus raw server COMPLETE, exact typed request, next-match stop boundary, no CSV dependency or synthetic gameplay/stat result.'
    'Plugin SHA256: '+(Get-FileHash -LiteralPath $Plugin).Hash
} finally {$pluginAssembly.Dispose();$nativeAssembly.Dispose();$gameAssembly.Dispose()}
