[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$NativeHsMod=(Join-Path $PSScriptRoot '..\pkg\Native\HsMod.dll'),
    [string]$Game='D:\Hearthstone\Hearthstone_Data\Managed\Assembly-CSharp.dll',
    [string]$CecilPath='D:\Hearthstone\BepInEx\core\Mono.Cecil.dll'
)
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
$taskPlugin=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$taskNative=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($NativeHsMod)
$taskGame=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Game)
try {
    $taskGuard=$taskPlugin.MainModule.GetType('ArenaBoxBot.NativeBgPositionProgressGuard')
    if(-not $taskGuard){throw 'Missing BG position progress guard'}
    $taskBot=$taskNative.MainModule.GetType('HsMod.BgBoxAiBot')
    $taskMove=$taskBot.Methods|Where-Object Name -eq 'TryRepositionOnce'
    if($taskMove.ReturnType.FullName -ne 'System.Boolean' -or $taskMove.Parameters.Count -ne 2 -or
       $taskMove.Parameters[0].ParameterType.FullName -ne 'GameState' -or
       $taskMove.Parameters[1].ParameterType.FullName -ne 'Network/Options'){throw 'Native position method signature changed'}
    $taskSends=@($taskMove.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'GameState' -and $_.Operand.Name -eq 'SendOption'})
    if($taskSends.Count -ne 1){throw 'Expected one original native send inside the position method'}
    $taskGameState=$taskGame.MainModule.GetType('GameState')
    foreach($taskName in @('GetSelectedOption','GetSelectedOptionPosition')){
        $taskGetter=$taskGameState.Methods|Where-Object {$_.Name -eq $taskName -and $_.Parameters.Count -eq 0}
        if($taskGetter.ReturnType.FullName -ne 'System.Int32'){throw "Unsupported original selected-option getter: $taskName"}
    }
    $taskPrefix=$taskGuard.Methods|Where-Object Name -eq 'BeforeReposition'
    $taskPrefixCalls=@($taskPrefix.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    foreach($taskRequired in @('Snapshot','Observe')){if($taskPrefixCalls -notcontains $taskRequired){throw "Missing position progress gate: $taskRequired"}}
    $taskObserve=$taskGuard.Methods|Where-Object Name -eq 'ObserveSend'
    $taskObserveCalls=@($taskObserve.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    foreach($taskRequired in @('GetSelectedOption','GetSelectedOptionPosition','Sent')){if($taskObserveCalls -notcontains $taskRequired){throw "Missing original send observation: $taskRequired"}}
    $taskPolicy=$taskPlugin.MainModule.GetType('ArenaBoxBot.BgPositionProgress')
    $taskObservePolicy=$taskPolicy.Methods|Where-Object Name -eq 'Observe'
    if(($taskObservePolicy.Parameters.ParameterType.FullName -join ',') -ne 'System.String,System.Int32,System.String,System.Double,System.Boolean&'){
        throw 'Position progress must depend on game/turn/actual board/time, not options or publication IDs'
    }
    foreach($taskType in @($taskGuard)+@($taskGuard.NestedTypes)){
        foreach($taskMethod in $taskType.Methods){if(-not $taskMethod.HasBody){continue};foreach($taskInstruction in $taskMethod.Body.Instructions){
            $taskOperand=$taskInstruction.Operand
            if($taskOperand -is [Mono.Cecil.FieldReference] -and $taskInstruction.OpCode.Name -in @('stfld','stsfld') -and
               $taskOperand.DeclaringType.FullName -in @('GameState','ChoiceCardMgr','PowerProcessor','PowerTaskList','HsMod.BgBoxAiBot')){throw 'Guard writes original/native state'}
            if($taskOperand -is [Mono.Cecil.MethodReference] -and ($taskOperand.Name -match '^SetSelectedOption' -or
               $taskOperand.Name -in @('SendOption','SendChoices','SetRuntimeEnabled','EndCurrentTaskList','ForceKill','Dequeue','FindObjectsOfType','FindObjectOfType'))){
                throw ('Guard invents execution/completion or scans the scene: '+$taskOperand.Name)
            }
        }}
    }
    'PASS: native position/send signatures and actual-board policy; scoped original-send observation; no direct action/ACK/native queue writes or scene scans.'
    'Plugin SHA256: '+(Get-FileHash -LiteralPath $Plugin).Hash
    'Native HsMod SHA256: '+(Get-FileHash -LiteralPath $NativeHsMod).Hash
} finally {$taskPlugin.Dispose();$taskNative.Dispose();$taskGame.Dispose()}
