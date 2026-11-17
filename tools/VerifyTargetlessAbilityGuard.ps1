param(
 [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
 [string]$Native=(Join-Path $PSScriptRoot '..\pkg\Native\HsMod.dll'),
 [string]$CecilPath='D:\万有引力号\pkg\Hearthstone\BepInEx\core\Mono.Cecil.dll',
 [switch]$ExpectAbsent
)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))|Out-Null
$pluginAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$nativeAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Native)
try {
 $runtime=$pluginAssembly.MainModule.GetType('ArenaBoxBot.NativeTargetlessAbilityGuard')
 if($ExpectAbsent){if($runtime){throw 'Negative control already contains guard'}; 'PASS: actual previously deployed addon has no targetless ability correction';return}
 if(!$runtime){throw 'Ability guard absent'}
 $after=$runtime.Methods|Where-Object Name -eq 'AfterMap'
 if($after.Parameters.Count -ne 4 -or $after.Parameters[0].ParameterType.FullName -ne 'GameState' -or $after.Parameters[1].ParameterType.FullName -ne 'Network/Options' -or $after.Parameters[3].ParameterType.FullName -ne 'System.Object'){throw 'Mapping-only postfix ABI changed'}
 $calls=@($after.Body.Instructions|Where-Object Operand -is ([Mono.Cecil.MethodReference])|ForEach-Object {$_.Operand.Name})
 foreach($name in @('GetOptionsPacket','GetFriendlySidePlayer','GetOpposingSidePlayer','IsGameCreated','IsGameOverNowOrPending','WasConcedeRequested','IsMulliganManagerActive','IsInChoiceMode','GetFriendlyEntityChoices','GetResponseMode','GetMode','IsBattlegrounds','HasTaskLists','IsBusy','GetFriendlyPlayerId','IsMinion','GetZone','GetZonePosition','GetCardId','Current','Resolve','Invoke')){
  if($calls -notcontains $name){throw ('Missing native readiness/identity/legality guard: '+$name)}
 }
 $packetRead=@($after.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'GetOptionsPacket'})
 if($packetRead.Count -ne 1 -or $packetRead[0].Next.OpCode.Name -ne 'ldarg.1' -or !$packetRead[0].Next.Next.OpCode.Name.StartsWith('bne.un')){throw 'Exact current packet reference comparison absent'}
 foreach($method in $runtime.Methods){if(!$method.HasBody){continue};foreach($i in $method.Body.Instructions){
  $o=$i.Operand
  if($o -is [Mono.Cecil.MethodReference] -and $o.Name -in @('ReadCurrent','SendOption','SendMappedOption','SendEntityChoice','SetSelectedOption','SetSelectedSubOption','MarkSubmitted','ResetExecutionTracking','AbortShadowCommit','Concede','RequestPageStateRefresh')){throw ('Forbidden execution/ledger/reader mutation: '+$i)}
  if($o -is [Mono.Cecil.FieldReference] -and $i.OpCode.Name -in @('stfld','stsfld') -and $o.DeclaringType.FullName -notin @($runtime.FullName,'ArenaBoxBot.TargetlessAbilityOption')){throw ('Unexpected external field write: '+$i)}
 }}
 $startup=$runtime.Methods|Where-Object Name -eq 'EnsureInstalled'
 $strings=@($startup.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldstr'}|ForEach-Object Operand)
 foreach($name in @('MapOption','MapTitanPower','HsMod.BoxAiMappedAction')){if($strings -notcontains $name){throw 'Native mapping-only hook missing'}}
 $adapter=$nativeAssembly.MainModule.GetType('HsMod.BoxAiShadowAdapter')
 foreach($name in @('MapOption','MapTitanPower')){
  $method=@($adapter.Methods|Where-Object Name -eq $name)
  if($method.Count -ne 1 -or $method[0].ReturnType.FullName -ne 'HsMod.BoxAiMappedAction' -or ($method[0].Parameters.ParameterType.FullName -join ',') -cne 'GameState,Network/Options,HsMod.BoxAiRecommendation'){throw 'Original mapper ABI changed'}
 }
 $skill=$adapter.Methods|Where-Object Name -eq 'MapTitanPower'
 $skillCalls=@($skill.Body.Instructions|Where-Object Operand -is ([Mono.Cecil.MethodReference])|ForEach-Object {$_.Operand.Name})
 foreach($name in @('ResolveSource','ResolveTarget','IsOptionMainUsable','HasLegalTarget','IsDisguisedOption')){if($skillCalls -notcontains $name){throw 'Native skill mapper lost an original legality gate'}}
 $types=@($pluginAssembly.MainModule.Types)
 $wired=@(foreach($type in $types){foreach($method in $type.Methods){if(!$method.HasBody){continue};foreach($i in $method.Body.Instructions){if($i.Operand -is [Mono.Cecil.MethodReference] -and $i.Operand.DeclaringType.FullName -eq $runtime.FullName -and $i.Operand.Name -eq 'EnsureInstalled'){$method.FullName}}}})
 if($wired.Count -ne 1){throw 'Guard must have exactly one production startup caller'}
 'PASS: original MapOption postfix only, unique fresh legal targetless child to original MapTitanPower; original recommendation/packet/dispatch/ACK/ledger untouched; BG/choice/queue/opponent/stale guards retained'
 'Plugin SHA256: '+(Get-FileHash -LiteralPath $Plugin).Hash
}finally{$pluginAssembly.Dispose();$nativeAssembly.Dispose()}
