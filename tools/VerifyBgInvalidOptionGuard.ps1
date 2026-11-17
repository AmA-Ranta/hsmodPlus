[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$NativeHsMod='D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [string]$Game='D:\Hearthstone\Hearthstone_Data\Managed\Assembly-CSharp.dll',
    [string]$Output='',
    [string]$CecilPath='D:\万有引力号\pkg\Hearthstone\BepInEx\core\Mono.Cecil.dll'
)
# Offline replay of the actual native option/result types. No connection,
# live Harmony patches, page-refresh request or game input is performed.
$ErrorActionPreference='Stop'
$flags=[Reflection.BindingFlags]'Static,Instance,Public,NonPublic'
$g=[Reflection.Assembly]::LoadFrom($Game)
$n=[Reflection.Assembly]::LoadFrom($NativeHsMod)
$p=[Reflection.Assembly]::LoadFrom($Plugin)
$guard=$p.GetType('ArenaBoxBot.NativeBgInvalidOptionGuard',$true)
$reject=$guard.GetMethod('RejectTargetlessMapping',$flags)
$mappedType=$n.GetType('HsMod.BoxAiMappedAction',$true)
$results=[Collections.Generic.List[string]]::new()
function Require([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function NewNative([string]$Name){return [Activator]::CreateInstance($g.GetType($Name,$true))}
function SetProperty($Object,[string]$Name,$Value){$Object.GetType().GetProperty($Name).SetValue($Object,$Value)}
function NewScenario {
    $packet=NewNative 'Network+Options'
    $option=NewNative 'Network+Options+Option'
    $main=NewNative 'Network+Options+Option+SubOption'
    $error=NewNative 'Network+Options+Option+PlayErrorInfo'
    SetProperty $packet 'ID' 226
    SetProperty $option 'Type' ([Enum]::Parse($option.GetType().GetProperty('Type').PropertyType,'POWER'))
    SetProperty $main 'ID' 16005
    SetProperty $error 'PlayError' ([Enum]::Parse($error.GetType().GetProperty('PlayError').PropertyType,'REQ_TARGET_TO_PLAY'))
    SetProperty $main 'PlayErrorInfo' $error
    SetProperty $option 'Main' $main
    $list=[Activator]::CreateInstance($packet.GetType().GetProperty('List').PropertyType)
    $list.Add($option);SetProperty $packet 'List' $list
    $mapped=[Activator]::CreateInstance($mappedType)
    foreach($entry in @{Legal=$true;Reason='已映射';OptionIndex=0;SubOptionIndex=-1;SourceEntityId=16005;TargetEntityId=0;Position=0}.GetEnumerator()){
        $mappedType.GetField($entry.Key).SetValue($mapped,$entry.Value)
    }
    return @{packet=$packet;option=$option;main=$main;error=$error;mapped=$mapped}
}
function Reject($Scenario){$arguments=[object[]]@($Scenario.packet,$Scenario.mapped,0);return [bool]$reject.Invoke($null,$arguments)}
$s=NewScenario
$allSubs=$n.GetType('HsMod.BgBoxAiBot').GetMethod('AllSubs',$flags)
$original=$allSubs.Invoke($null,[object[]]@($s.option,$false,$false))
Require ($original.Count -eq 1 -and !$original[0].PlayErrorInfo.IsValid()) 'Actual native fallback must expose captured invalid main option'
Require (Reject $s) 'Captured option226 targetless REQ_TARGET_TO_PLAY must be rejected'
Require (!$s.mapped.Legal -and $s.main.ID -eq 16005 -and $s.packet.ID -eq 226 -and $s.main.PlayErrorInfo.PlayError.ToString() -eq 'REQ_TARGET_TO_PLAY') 'Only mapping result may be downgraded; original native packet retained'
$results.Add('PASS actual native AllSubs invalid fallback reproduced; captured targetless mapping rejected; native packet unchanged')
foreach($case in @('legal','target','position','branch','wrong-source','already-illegal','non-power','unknown-error','missing-error','has-targets','has-subs')){
    $s=NewScenario
    switch($case){
        'legal' {SetProperty $s.error 'PlayError' ([Enum]::Parse($s.error.PlayError.GetType(),'NONE'))}
        'target' {$s.mapped.TargetEntityId=900}
        'position' {$s.mapped.Position=1}
        'branch' {$s.mapped.SubOptionIndex=0}
        'wrong-source' {$s.mapped.SourceEntityId=16006}
        'already-illegal' {$s.mapped.Legal=$false}
        'non-power' {SetProperty $s.option 'Type' ([Enum]::ToObject($s.option.Type.GetType(),2))}
        'unknown-error' {SetProperty $s.error 'PlayError' ([Enum]::ToObject($s.error.PlayError.GetType(),2147483647))}
        'missing-error' {SetProperty $s.main 'PlayErrorInfo' $null}
        'has-targets' {$list=[Activator]::CreateInstance($s.main.GetType().GetProperty('Targets').PropertyType);$list.Add((NewNative 'Network+Options+Option+TargetOption'));SetProperty $s.main 'Targets' $list}
        'has-subs' {$list=[Activator]::CreateInstance($s.option.GetType().GetProperty('Subs').PropertyType);$list.Add((NewNative 'Network+Options+Option+SubOption'));SetProperty $s.option 'Subs' $list}
    }
    $before=$s.mapped.Legal
    Require (!(Reject $s) -and $s.mapped.Legal -eq $before) "Preserve original mapping for $case"
    $results.Add("PASS preserve original $case mapping")
}
$budgetType=$p.GetType('ArenaBoxBot.BgInvalidOptionRefreshBudget',$true)
$budget=[Activator]::CreateInstance($budgetType,$true)
$decide=$budgetType.GetMethod('ShouldRefresh',$flags)
function Refresh([string]$Turn,[double]$Now,[bool]$Own=$true){return [bool]$decide.Invoke($budget,[object[]]@($Turn,$Now,$Own))}
Require (!(Refresh '498:21' 10)) 'Allow native state settlement before refresh'
Require (!(Refresh '498:21' 12.4)) 'Do not refresh before2.5seconds'
Require (Refresh '498:21' 12.5) 'First bounded page refresh after settlement'
Require (!(Refresh '498:21' 17.4)) 'Do not flood page refresh requests'
Require (Refresh '498:21' 17.5) 'Second and final page refresh'
Require (!(Refresh '498:21' 50)) 'Same turn cannot replenish refresh budget'
Require (!(Refresh '498:22' 60 $false)) 'Combat must not consume/replenish own-phase refresh budget'
Require (!(Refresh '499:1' 65)) 'New game starts a separate settling interval'
Require (Refresh '499:1' 67.5) 'Real new game/turn can refresh'
Require (!(Refresh '' 70) -and !(Refresh '499:1' ([double]::NaN)) -and !(Refresh '499:1' 2)) 'Missing scope/nonfinite/regressing clock cannot trigger refresh'
$results.Add('PASS two refreshes per actual game/turn; settle/spacing/combat/clock guards; no ACK or options-publication replenishment')
$identity=$budgetType.GetMethod('TurnIdentity',$flags)
function Scope([int]$Handle,[string]$HostName,[uint32]$Port,[long]$ClientHandle,[int]$Turn){return $identity.Invoke($null,[object[]]@($Handle,$HostName,$Port,$ClientHandle,$Turn))}
# Real current-session evidence: game1428 was reused on these two servers.
# A budget touched only on invalid mappings may retain its old key across
# intervening normal matches, so a native-turn number alone cannot reset it.
$old=Scope 1428 '5.42.176.179' 1119 80802853 25
$new=Scope 1428 '5.42.177.233' 1119 80802853 25
Require ($old -ne $new -and $old -eq (Scope 1428 '5.42.176.179' 1119 80802853 25)) 'Actual reused handle on different servers must have separate stable identities'
$budget=[Activator]::CreateInstance($budgetType,$true)
Require (!(Refresh '1428:25' 10) -and (Refresh '1428:25' 12.5) -and (Refresh '1428:25' 17.5) -and !(Refresh '1428:25' 2802.5)) 'Baseline unscoped key must reproduce new-game refresh starvation'
$results.Add('PASS captured same-handle different-server identity; baseline exhausted numeric game/turn key reproduces starvation')
$budget=[Activator]::CreateInstance($budgetType,$true)
Require (!(Refresh $old 10) -and (Refresh $old 12.5) -and (Refresh $old 17.5)) 'Old actual connection consumes exactly two refreshes'
Require (!(Refresh $new 2800) -and (Refresh $new 2802.5) -and !(Refresh $new 2807.4) -and (Refresh $new 2807.5) -and !(Refresh $new 2900)) 'Actual new connection gets a separate settle interval and exactly two bounded refreshes'
$results.Add('PASS new connection with reused handle/native turn gets its own two-refresh budget; existing spacing and cap retained')
Require ($new -ne (Scope 1428 '5.42.177.233' 3724 80802853 25) -and $new -ne (Scope 1428 '5.42.177.233' 1119 80802854 25) -and $new -ne (Scope 1428 '5.42.177.233' 1119 80802853 27)) 'Port, client and native turn belong to the actual identity'
Require ($null -eq (Scope 0 '5.42.177.233' 1119 80802853 25) -and $null -eq (Scope 1428 '' 1119 80802853 25) -and $null -eq (Scope 1428 '5.42.177.233' 0 80802853 25) -and $null -eq (Scope 1428 '5.42.177.233' 1119 80802853 0)) 'Unknown connection cannot invent a budget identity'
Require (!(Refresh (Scope 0 '' 0 0 0) 3000)) 'Unknown identity cannot trigger a page request'
$results.Add('PASS port/client/turn changes distinguished; incomplete native identity cannot trigger refresh')
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
$metadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
try{
    $type=$metadata.MainModule.GetType('ArenaBoxBot.NativeBgInvalidOptionGuard')
    foreach($method in $type.Methods){if(!$method.HasBody){continue};foreach($instruction in $method.Body.Instructions){
        $operand=$instruction.Operand
        if($operand -is [Mono.Cecil.MethodReference]){
            Require ($operand.Name -notmatch '^(ReadCurrent|SendOption|SendChoices|SetSelectedOption|ResetExecutionTracking|InvalidateRecommendations|Concede|DiscardDirectRecommendation|MarkSubmitted|Dequeue)$') 'Guard cannot consume reader, invent actions/completion or reset ledger/queue'
        }
        if($operand -is [Mono.Cecil.FieldReference] -and $instruction.OpCode.Name -in @('stfld','stsfld')){
            Require ($operand.DeclaringType.FullName -eq $type.FullName) 'No direct game/native field write'
        }
    }}
    foreach($prefixName in @('BeforeExecute','BeforeRecover')){
        $prefix=$type.Methods|Where-Object Name -eq $prefixName
        Require (@($prefix.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'OwnRecruitPhase'}).Count -eq 1) 'Both recovery and execution must retain own-phase gate'
    }
    $afterMap=$type.Methods|Where-Object Name -eq 'AfterMap'
    $calls=@($afterMap.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    foreach($required in @('GetGameHandle','GetLastGameServerJoined','get_Address','get_Port','get_ClientHandle','GetTurn','TurnIdentity')){Require ($required -in $calls) "Live refresh adapter must read actual native identity: $required"}
    $registered=@($metadata.MainModule.GetType('ArenaBoxBot.ArenaController').Methods|Where-Object {$_.Name -eq 'Update' -and $_.HasBody}|ForEach-Object {$_.Body.Instructions}|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $type.FullName -and $_.Operand.Name -eq 'EnsureInstalled'})
    Require ($registered.Count -eq 1) 'Live guard registration missing'
    $results.Add('PASS native execution/recovery phase gates and live registration; no extra stateful reader/action/ledger or queue clearing')
}finally{$metadata.Dispose()}
$result=[ordered]@{at=[DateTimeOffset]::Now.ToString('o');passed=$results.Count;results=$results;pluginSha256=(Get-FileHash -LiteralPath $Plugin).Hash;nativeSha256=(Get-FileHash -LiteralPath $NativeHsMod).Hash;liveRefreshVerified=$false;limitation='Offline replay proves invalid send prevention and bounded existing refresh requests. It cannot prove the Box page/backend supplies a newer executable recommendation.'}
if($Output){$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $Output -Encoding utf8}
$result|ConvertTo-Json -Depth 6
