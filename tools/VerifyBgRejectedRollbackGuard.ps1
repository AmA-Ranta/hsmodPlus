[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$NativeHsMod='D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [string]$Game='D:\Hearthstone\Hearthstone_Data\Managed\Assembly-CSharp.dll',
    [string]$CecilPath='D:\Hearthstone\BepInEx\core\Mono.Cecil.dll',
    [string]$Output=''
)
# Offline real-client rejection replay. No Harmony installation, connection,
# SendOption, game input, live field writes or invented acknowledgement.
$ErrorActionPreference='Stop'
$f=[Reflection.BindingFlags]'Static,Instance,Public,NonPublic'
# A packaged addon keeps BepInEx beside its loader in BepInEx/core, whereas
# a compiler output copies it beside the addon. Match the real loader context
# explicitly so this replay also tests the final extracted package DLL.
[void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent $CecilPath) 'BepInEx.dll'))
foreach($engine in @('UnityEngine.CoreModule.dll','UnityEngine.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent $Game) $engine))
}
$g=[Reflection.Assembly]::LoadFrom($Game)
$n=[Reflection.Assembly]::LoadFrom($NativeHsMod)
$p=[Reflection.Assembly]::LoadFrom($Plugin)
$guard=$p.GetType('ArenaBoxBot.NativeBgRejectedRollbackGuard',$true)
$results=[Collections.Generic.List[string]]::new()
function Require([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Native([string]$Name){[Activator]::CreateInstance($g.GetType($Name,$true),$true)}
function Prop($Object,[string]$Name,$Value){$Object.GetType().GetProperty($Name,$f).SetValue($Object,$Value)}
function InvokeGuard([string]$Name,[object[]]$Arguments){$guard.GetMethod($Name,$f).Invoke($null,$Arguments)}
$stateType=$g.GetType('GameState',$true)
$last=$stateType.GetField('m_lastOptions',$f)
$selection=$stateType.GetField('m_lastSelectedOption',$f)
function Scenario {
    $state=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($stateType)
    $packet=Native 'Network+Options';Prop $packet 'ID' 417
    # Captured Game1428 packet417: sell option9 is the DragSell receiver19376;
    # selected target20633 is a DIFFERENT entity. Earlier options are unused.
    $option=Native 'Network+Options+Option';$main=Native 'Network+Options+Option+SubOption';Prop $main 'ID' 19376;Prop $option 'Main' $main
    Prop $option 'Type' ([Enum]::Parse($option.GetType().GetProperty('Type').PropertyType,'POWER'))
    $list=[Activator]::CreateInstance($packet.GetType().GetProperty('List').PropertyType)
    for($unused=0;$unused -lt 9;$unused++){$list.Add((Native 'Network+Options+Option'))}
    $list.Add($option);Prop $packet 'List' $list
    $selected=[Activator]::CreateInstance($selection.FieldType,$true)
    foreach($pair in @{m_main=9;m_target=20633;m_sub=-1;m_position=0}.GetEnumerator()){$selection.FieldType.GetField($pair.Key,$f).SetValue($selected,$pair.Value)}
    $last.SetValue($state,$packet);$selection.SetValue($state,$selected)
    return @{state=$state;packet=$packet;option=$option;selected=$selected}
}
function Observe($s){$args2=[object[]]@($s.state,$null);[void](InvokeGuard 'BeginNative' $args2);try{[void](InvokeGuard 'ObserveSend' @($s.state))}finally{[void](InvokeGuard 'EndNative' @($null,$args2[1]))}}
function Rollback($s,$card=$null,[int]$receiver=19376){[bool](InvokeGuard 'ShouldRunRollback' @($s.state,$s.option,$card,$receiver))}

# Call the actual first failing client method with a CLR-null card. `call`
# deliberately enters its original managed body without any Unity singleton.
$unsafe=[Reflection.Emit.DynamicMethod]::new('ReplayOriginalNullRollback',[void],[Type[]]@(),$stateType.Module,$true)
$il=$unsafe.GetILGenerator();$il.Emit([Reflection.Emit.OpCodes]::Ldnull);$il.Emit([Reflection.Emit.OpCodes]::Ldnull)
$il.Emit([Reflection.Emit.OpCodes]::Call,$g.GetType('InputManager').GetMethod('AddHeldCardBackToPlayZone',$f));$il.Emit([Reflection.Emit.OpCodes]::Ret)
$unsafeAction=$unsafe.CreateDelegate([Action])
$thrown=$false
try{$unsafeAction.Invoke()}catch{$e=$_.Exception;while($e.InnerException){$e=$e.InnerException};$thrown=$e -is [NullReferenceException]}
Require $thrown 'Actual client null-card rollback must reproduce the captured NullReferenceException'
$results.Add('PASS original client rollback null reference reproduced without a game connection')

# Actual GameState.OnOptionRejected -> actual listener dispatch -> actual
# ClearLastOptions, with two callbacks. Baseline exception prevents the second
# listener and cleanup; guarded null rollback allows both original operations.
$listenerType=$g.GetType('GameState+OptionRejectedListener',$true)
$callbackType=$g.GetType('GameState+OptionRejectedCallback',$true)
$listeners=$stateType.GetField('m_optionRejectedListeners',$f)
$handler=$stateType.GetMethod('OnOptionRejected',$f,$null,[Type[]]@([int]),$null)
$script:callbacks=0
function Listener([Action[object,object]]$Action){
    $parameters=$callbackType.GetMethod('Invoke').GetParameters()
    $x=[Linq.Expressions.Expression]::Parameter($parameters[0].ParameterType,'option')
    $y=[Linq.Expressions.Expression]::Parameter([object],'userData')
    $body=[Linq.Expressions.Expression]::Invoke([Linq.Expressions.Expression]::Constant($Action),[Linq.Expressions.Expression[]]@([Linq.Expressions.Expression]::Convert($x,[object]),$y))
    $delegate=[Linq.Expressions.Expression]::Lambda($callbackType,$body,[Linq.Expressions.ParameterExpression[]]@($x,$y)).Compile()
    $listener=[Activator]::CreateInstance($listenerType,$true);$listenerType.GetField('m_callback',$f).SetValue($listener,$delegate)
    return $listener
}
foreach($repair in @($false,$true)){
    $s=Scenario;Observe $s;$script:callbacks=0
    $action=[Action[object,object]]{param($option,$userData);if(!$repair -or (Rollback $s)){$unsafeAction.Invoke()}}
    $list=[Activator]::CreateInstance($listeners.FieldType);$list.Add((Listener $action));$list.Add((Listener ([Action[object,object]]{param($option,$userData);$script:callbacks++})))
    $listeners.SetValue($s.state,$list)
    $failed=$false;$handlerError=$null;try{$handler.Invoke($s.state,[object[]]@(417))}catch{$failed=$true;$handlerError=$_.Exception;while($handlerError.InnerException){$handlerError=$handlerError.InnerException}}
    if(!$repair){Require ($failed -and $script:callbacks -eq 0 -and [object]::ReferenceEquals($last.GetValue($s.state),$s.packet)) 'Baseline exception must abort downstream listener and original cleanup'}
    else{Require (!$failed -and $script:callbacks -eq 1 -and $null -eq $last.GetValue($s.state) -and $null -eq $selection.GetValue($s.state)) "Repaired replay must complete downstream listener and ORIGINAL native cleanup; underlying error=$handlerError";[void](InvokeGuard 'AfterRejected' @($s.state,417))}
}
$results.Add('PASS actual rejection handler baseline aborts; guarded handler reaches next listener and original ClearLastOptions')

$s=Scenario;Observe $s
Require (!(Rollback $s) -and [object]::ReferenceEquals($last.GetValue($s.state),$s.packet) -and [object]::ReferenceEquals($selection.GetValue($s.state),$s.selected)) 'Guard itself must not clear native state'
$results.Add('PASS exact observed native rejection skips nonexistent-card rollback without modifying native state')
foreach($case in @('card','unobserved','human-send','receiver','target-as-receiver','option-reference','packet-reference','index','target','sub','position','expired','future','other-state')){
    $s=Scenario;if($case -ne 'unobserved'){Observe $s};$card=$null;$receiver=19376
    switch($case){
        'card' {$card=[object]::new()}
        'human-send' {[void](InvokeGuard 'ObserveSend' @($s.state))}
        'receiver' {$receiver=999}
        'target-as-receiver' {$receiver=20633}
        'option-reference' {$o=Native 'Network+Options+Option';Prop $o 'Main' $s.option.Main;$s.option=$o}
        'packet-reference' {$packet=Native 'Network+Options';Prop $packet 'ID' 417;Prop $packet 'List' $s.packet.List;$last.SetValue($s.state,$packet)}
        'index' {$selection.FieldType.GetField('m_main',$f).SetValue($s.selected,1)}
        'target' {$selection.FieldType.GetField('m_target',$f).SetValue($s.selected,999)}
        'sub' {$selection.FieldType.GetField('m_sub',$f).SetValue($s.selected,0)}
        'position' {$selection.FieldType.GetField('m_position',$f).SetValue($s.selected,1)}
        {$_ -in @('expired','future')} {$table=$guard.GetField('Observations',$f).GetValue($null);$args2=[object[]]@($s.state,$null);[void]$table.GetType().GetMethod('TryGetValue').Invoke($table,$args2);$sent=$args2[1];$delta=if($case -eq 'expired'){-11}else{1};$sent.GetType().GetField('At',$f).SetValue($sent,[long]([Diagnostics.Stopwatch]::GetTimestamp()+$delta*[Diagnostics.Stopwatch]::Frequency))}
        'other-state' {$s.state=(Scenario).state}
    }
    Require (Rollback $s $card $receiver) "Original rollback must be retained for $case"
    $results.Add("PASS retain original $case rejection")
}
$exception=[InvalidOperationException]::new('test scope')
Require ([object]::ReferenceEquals((InvokeGuard 'EndNative' @($exception,$null)),$exception)) 'Scope finalizer must preserve original exception'
Require ([bool](InvokeGuard 'KnownCallback' @($g.GetType('MoveMinionHoverTarget').GetMethod('OnOptionRejected',$f)))) 'Observed callback IL contract changed'
Require (!(InvokeGuard 'KnownCallback' @($handler))) 'Unknown callback body must not be patched'
$results.Add('PASS original exception preserved; only known original null-rollback body accepted')

if (-not ('Mono.Cecil.AssemblyDefinition' -as [type])) {
    [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
}
$meta=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin);$nativeMeta=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($NativeHsMod)
try{
    $type=$meta.MainModule.GetType('ArenaBoxBot.NativeBgRejectedRollbackGuard')
    foreach($method in $type.Methods){if(!$method.HasBody){continue};foreach($i in $method.Body.Instructions){
        if($i.Operand -is [Mono.Cecil.MethodReference]){Require ($i.Operand.Name -notmatch '^(SendOption|SendChoices|SetSelectedOption|ClearLastOptions|SetValue|Concede|MarkSubmitted|Dequeue|ResetExecutionTracking)$') 'Guard cannot send, ACK, clear native state/queue or write reflected fields'}
        if($i.Operand -is [Mono.Cecil.FieldReference] -and $i.OpCode.Name -in @('stfld','stsfld')){Require ($i.Operand.DeclaringType.FullName.StartsWith($type.FullName)) 'No game/native field writes'}
    }}
    $execute=$nativeMeta.MainModule.GetType('HsMod.BgBoxAiBot').Methods|Where-Object Name -eq 'TryExecuteOption'
    Require (@($execute.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'GameState' -and $_.Operand.Name -eq 'SendOption'}).Count -eq 1) 'Native execution must synchronously call original GameState.SendOption within observed scope'
    $registered=@($meta.MainModule.GetType('ArenaBoxBot.ArenaController').Methods|Where-Object {$_.Name -eq 'Update' -and $_.HasBody}|ForEach-Object {$_.Body.Instructions}|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $type.FullName -and $_.Operand.Name -eq 'EnsureInstalled'})
    Require ($registered.Count -eq 1) 'Live guard registration missing'
    $results.Add('PASS synchronous actual native send provenance and live registration; no send/ACK/queue/native field replacement')
}finally{$meta.Dispose();$nativeMeta.Dispose()}
$result=[ordered]@{at=[DateTimeOffset]::Now.ToString('o');passed=$results.Count;results=$results;pluginSha256=(Get-FileHash -LiteralPath $Plugin).Hash;nativeSha256=(Get-FileHash -LiteralPath $NativeHsMod).Hash;gameSha256=(Get-FileHash -LiteralPath $Game).Hash;liveServerRejectionVerified=$false;limitation='Offline actual-client handler replay proves original cleanup after the narrowly guarded null-card callback. A natural live rejection and subsequent real action are still required.'}
if($Output){$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $Output -Encoding utf8}
$result|ConvertTo-Json -Depth 6
