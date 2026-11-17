[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$Native='D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [switch]$ExpectLegacyUiLookup
)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes('D:\Hearthstone\BepInEx\core\Mono.Cecil.dll'))|Out-Null
$p=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$n=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Native)
try {
    $runtime=$p.MainModule.GetType('ArenaBoxBot.NativeOptionEntityFallback')
    if($ExpectLegacyUiLookup){
        if($runtime){throw 'Negative control already has legal option entity fallback'}
        $source=$n.MainModule.GetType('HsMod.BoxAiShadowAdapter').Methods|Where-Object Name -eq 'ResolveSource'
        $calls=@($source.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
        if($calls -notcontains 'HandCards' -or $calls -notcontains 'FindZoneEntity' -or $calls -contains 'GetOptionsPacket'){throw 'Native UI-only source contract changed'}
        'PASS negative control: actual R27 has no current legal option entity fallback; native hand-card lookup depends on UI zone cards.'
        return
    }
    if(-not $runtime){throw 'Missing fallback runtime'}
    $current=$runtime.Methods|Where-Object Name -eq 'Current'
    $calls=@($current.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    foreach($name in @('GetOptionsPacket','GetFriendlySidePlayer','GetOpposingSidePlayer','GetTag','IsGameCreated','IsGameOverNowOrPending','WasConcedeRequested','IsMulliganManagerActive','IsInChoiceMode','GetFriendlyEntityChoices','GetResponseMode','GetMode','IsCurrent')){
        if($calls -notcontains $name){throw "Missing active own OPTION/choice/packet guard: $name"}
    }
    foreach($name in @('AfterSource','AfterTarget')){
        $m=$runtime.Methods|Where-Object Name -eq $name
        if($m.Parameters.Count -ne 3 -or $m.Parameters[2].ParameterType.FullName -ne 'Entity&'){throw 'Postfix must only fill native Entity result'}
        if(@($m.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'Current'}).Count -ne 1){throw 'Current guard missing from postfix'}
    }
    foreach($method in $runtime.Methods){if(-not $method.HasBody){continue};foreach($i in $method.Body.Instructions){
        $o=$i.Operand
        if($o -is [Mono.Cecil.MethodReference] -and $o.Name -in @('ReadCurrent','IsBlocked','SetValue','MarkSubmitted','SendOption','SendEntityChoice','SetSelectedOption','AbortShadowCommit','ResetExecutionTracking','Concede')){throw "Fallback may not read/mutate acknowledgement state or dispatch: $i"}
        if($o -is [Mono.Cecil.FieldReference] -and $i.OpCode.Name -in @('stfld','stsfld') -and $o.DeclaringType.FullName -notin @($runtime.FullName,'ArenaBoxBot.OptionEntityCandidate')){throw "Unexpected external state write: $i"}
    }}
    $adapter=$n.MainModule.GetType('HsMod.BoxAiShadowAdapter')
    foreach($name in @('ResolveSource','ResolveTarget')){
        $m=@($adapter.Methods|Where-Object Name -eq $name)
        if($m.Count -ne 1 -or $m[0].ReturnType.FullName -ne 'Entity' -or $m[0].Parameters.Count -ne 2 -or $m[0].Parameters[0].ParameterType.FullName -ne 'GameState' -or $m[0].Parameters[1].ParameterType.FullName -ne 'HsMod.BoxAiRecommendation'){throw 'Native resolver signature changed'}
    }
    $map=$adapter.Methods|Where-Object Name -eq 'MapOption'
    $mapCalls=@($map.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    foreach($name in @('ResolveSource','ResolveTarget','IsSourceSuppressed')){if($mapCalls -notcontains $name){throw "Native mapping/source suppression contract missing: $name"}}
    'PASS: null-result entity resolution from current own legal options; exact PID/packet/age/card/position/side/zone/unique identity; native suppression/mapping/ACK/dispatch retained, no extra reader or native guard writes.'
    'Plugin SHA256: '+(Get-FileHash -LiteralPath $Plugin).Hash
}finally{$p.Dispose();$n.Dispose()}
