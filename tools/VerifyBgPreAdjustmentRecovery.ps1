[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$NativeHsMod='D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [string]$CecilPath='D:\Hearthstone\BepInEx\core\Mono.Cecil.dll',
    [string]$Output=''
)
# Execute the actual installed pure native evaluator in this offline process.
# No game connection, Harmony installation, native input or config operations.
$ErrorActionPreference='Stop'
$flags=[Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$native=[Reflection.Assembly]::LoadFrom($NativeHsMod)
$addon=[Reflection.Assembly]::LoadFrom($Plugin)
$evaluatorType=$native.GetType('HsMod.Utilities.BattlegroundsPreAdjustment',$true)
$boardType=$native.GetType('HsMod.Utilities.PreAdjustBoardCard',$true)
$moveType=$native.GetType('HsMod.Utilities.BattlegroundsPreAdjustMove',$true)
$evaluate=$evaluatorType.GetMethod('Evaluate',$flags)
$recover=$addon.GetType('ArenaBoxBot.BgPreAdjustmentRecovery',$true).GetMethod('TryReset',$flags)
$results=[Collections.Generic.List[string]]::new()
function Require([bool]$Condition,[string]$Message) {if(!$Condition){throw $Message}}
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
$metadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
try {
    $guard=$metadata.MainModule.GetType('ArenaBoxBot.NativeBgPreAdjustmentRecovery')
    $prefix=$guard.Methods|Where-Object Name -eq 'BeforeEvaluate'
    Require ($prefix.ReturnType.FullName -eq 'System.Void') 'Prefix cannot skip original Evaluate'
    Require (@($prefix.Parameters|Where-Object {$_.Name -eq '__result' -or $_.ParameterType.IsByReference}).Count -eq 0) 'Prefix cannot invent result or replace original arguments'
    foreach($typeName in @('ArenaBoxBot.BgPreAdjustmentRecovery','ArenaBoxBot.NativeBgPreAdjustmentRecovery')) {
        $type=$metadata.MainModule.GetType($typeName)
        foreach($method in @($type.Methods)+@($type.NestedTypes|ForEach-Object Methods)) {
            if(!$method.HasBody){continue}
            foreach($instruction in $method.Body.Instructions) {
                if($instruction.Operand -isnot [Mono.Cecil.MethodReference]){continue}
                Require ($instruction.Operand.Name -notmatch '^(SendOption|SendChoices|SetSelectedOption|SetRuntimeEnabled|DiscardDirectRecommendation|EndCurrentTaskList|ForceKill|Dequeue|SetValue)') 'Recovery must not send actions, mutate packet fields, fake completion or clear native queues'
            }
        }
    }
    $registered=@($metadata.MainModule.GetType('ArenaBoxBot.ArenaController').Methods|Where-Object {$_.Name -eq 'Update' -and $_.HasBody}|ForEach-Object {$_.Body.Instructions}|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'ArenaBoxBot.NativeBgPreAdjustmentRecovery' -and $_.Operand.Name -eq 'EnsureInstalled'})
    Require ($registered.Count -gt 0) 'Live plugin must register the narrow guard'
    $queueGate=$guard.Methods|Where-Object Name -eq 'BeforePreAdjustment'
    Require ($queueGate.ReturnType.FullName -eq 'System.Boolean') 'Queue guard must be an explicit defer prefix'
    Require (@($queueGate.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'PowerProcessor' -and $_.Operand.Name -eq 'HasTaskLists'}).Count -eq 1) 'Queue gate must read the original current/queued Power work'
    $stores=@($queueGate.Body.Instructions|Where-Object {$_.OpCode.Code.ToString() -eq 'Stind_I1'})
    Require ($stores.Count -eq 1 -and $stores[0].Previous.OpCode.Code.ToString() -eq 'Ldc_I4_0') 'Queue guard may only return not-ready; it must never synthesize Ready'
    foreach($instruction in $queueGate.Body.Instructions) {
        if($instruction.Operand -isnot [Mono.Cecil.MethodReference]){continue}
        Require ($instruction.Operand.Name -notmatch '^(Send|SetSelected|Reset|Evaluate|Dequeue|Clear|EndCurrentTaskList|ForceKill|SetValue)') 'Deferred path cannot evaluate/bind a partial board, reset state, send input, or clear work'
    }
    $nativeReady=$metadata.MainModule.AssemblyReferences|Where-Object Name -eq 'Assembly-CSharp'
    Require ($null -ne $nativeReady) 'Queue contract must use actual game assembly reference'
    $results.Add('PASS queue prefix reads actual HasTaskLists and can only defer with false; no Evaluate/Reset/input/queue mutation from deferred path')
    $results.Add('PASS prefix retains original Evaluate/arguments/result; plugin registration exists; no direct input, native field write, runtime disable or queue clearing')
} finally {$metadata.Dispose()}
function SetMember($Object,[string]$Name,$Value) {$Object.GetType().GetField($Name,$flags).SetValue($Object,$Value)}
function NewScenario([bool]$TwoCards=$false) {
    $e=[Activator]::CreateInstance($evaluatorType,$true)
    $b=[Activator]::CreateInstance($boardType,$true)
    foreach($pair in @{EntityId=12438;CardId='BG36_524';Position=1;Attack=195;Health=217}.GetEnumerator()){SetMember $b $pair.Key $pair.Value}
    $m=[Activator]::CreateInstance($moveType,$true)
    foreach($pair in @{SourceEntityId=12438;SourceCardId='BG36_524';SourcePosition=1;MoveTo=1;Attack=217;Health=239}.GetEnumerator()){SetMember $m $pair.Key $pair.Value}
    $definition=[Collections.Generic.List[int]].GetGenericTypeDefinition()
    $board=[Activator]::CreateInstance($definition.MakeGenericType($boardType));$board.Add($b)
    if($TwoCards) {
        $second=[Activator]::CreateInstance($boardType,$true)
        foreach($pair in @{EntityId=12439;CardId='BG36_523_G';Position=2;Attack=107;Health=105}.GetEnumerator()){SetMember $second $pair.Key $pair.Value}
        $board.Add($second);SetMember $m 'MoveTo' 2
    }
    $moves=[Activator]::CreateInstance($definition.MakeGenericType($moveType));$moves.Add($m)
    $scenario=@{e=$e;b=$b;m=$m;board=$board;moves=$moves;key='box-11-199-special';turn=21}
    $first=EvaluateScenario $scenario
    Require ($first.State.ToString() -eq 'Blocked' -and $first.Reason -eq 'move_identity_mismatch') 'Explicit-entity stat lag must produce the actual native mismatch'
    return $scenario
}
function EvaluateScenario($s) {
    return $evaluate.Invoke($s.e,[object[]]@($s.key,$s.turn,$s.moves,$s.board,199,[datetime]::UtcNow))
}
function CatchUp($s) {SetMember $s.b 'Attack' 217;SetMember $s.b 'Health' 239}
function RecoverScenario($s) {
    $arguments=[object[]]@($s.e,$s.key,$s.turn,$s.moves,$s.board,$null)
    return [bool]$recover.Invoke($null,$arguments)
}
$s=NewScenario;CatchUp $s
for($i=0;$i -lt 3;$i++) {
    $still=EvaluateScenario $s
    Require ($still.State.ToString() -eq 'Blocked' -and $still.Reason -eq 'move_identity_mismatch') 'Unmodified native evaluator must reproduce cached failure after exact stat catch-up'
}
$results.Add('PASS actual native regression:195/217 ->217/239 remains cached Blocked on same request without recovery')
Require (RecoverScenario $s) 'Exact caught-up original board must reopen the cached binding'
Require ((EvaluateScenario $s).State.ToString() -eq 'Ready') 'Original native evaluator must independently accept the now-matching no-op move'
Require (!(RecoverScenario $s)) 'No repeat reset when native error is absent'
$results.Add('PASS recovered cached binding, original native Evaluate returns Ready, no repeated reset')
$s=NewScenario $true;CatchUp $s
Require (RecoverScenario $s) 'Matching changed attributes may rebind a genuine required move'
$moveDecision=EvaluateScenario $s
Require ($moveDecision.State.ToString() -eq 'Move' -and $moveDecision.EntityId -eq 12438 -and $moveDecision.MoveTo -eq 2) 'Native evaluator must still require the real move; recovery cannot invent Ready'
$results.Add('PASS non-no-op native decision remains Move for exact entity/destination, never synthetic Ready')
$s=NewScenario $true;CatchUp $s;SetMember $s.b 'Position' 2;SetMember $s.board[1] 'Position' 1
Require (!(RecoverScenario $s)) 'Changed actual entity order must retain the original error even with matching source attributes'
$results.Add('PASS reordered multi-entity board retains error')

$cases=[ordered]@{
    still_mismatched={param($s) SetMember $s.b 'Attack' 195}
    wrong_card={param($s) SetMember $s.b 'CardId' 'BG36_523'}
    replaced_entity={param($s) SetMember $s.b 'EntityId' 12439}
    moved_original_board={param($s) SetMember $s.b 'Position' 2}
    different_request={param($s) $s.key='other'}
    different_turn={param($s) $s.turn=23}
    pending_move={param($s) SetMember $s.e 'pending' ([int[]]@(12438))}
    proposed_move={param($s) SetMember $s.e 'proposed' ([int[]]@(12438))}
    advanced_cursor={param($s) SetMember $s.e 'cursor' 1}
    another_error={param($s) SetMember $s.e 'error' 'move_unconfirmed'}
    invalid_destination={param($s) SetMember $s.m 'MoveTo' 2}
    requested_wrong_card={param($s) SetMember $s.m 'SourceCardId' 'BG36_523'}
    requested_wrong_entity={param($s) SetMember $s.m 'SourceEntityId' 12439}
    requested_wrong_stats={param($s) SetMember $s.m 'Health' 240}
}
foreach($case in $cases.GetEnumerator()) {
    $s=NewScenario;CatchUp $s;& $case.Value $s
    Require (!(RecoverScenario $s)) ('Must retain protection: '+$case.Key)
    Require ($evaluatorType.GetField('error',$flags).GetValue($s.e) -ne $null) ('Negative case must not clear native error: '+$case.Key)
    $results.Add('PASS retains protection: '+$case.Key)
}
$unknownArgs=[object[]]@([object]::new(),'same',21,[object[]]@(),[object[]]@(),$null)
Require (!$recover.Invoke($null,$unknownArgs)) 'Unknown contracts must keep original path'
$results.Add('PASS unknown evaluator contract rejected')

# Game728 round9: exact local Power order, not a changed-board bypass.
# Non-requested other-card attributes are irrelevant to the original binding.
$definition=[Collections.Generic.List[int]].GetGenericTypeDefinition()
function Captured728Board([string]$Phase) {
    $entries=@(
        @(8712,'BG36_204_G'),@(8718,'BG36_202'),@(8725,'BG36_763'),@(8726,'BG36_201'),
        @(8728,'BG36_356'),@(10200,'BG36_208'),@(9551,'BG36_201'))
    if($Phase -eq 'drained'){$entries=@($entries|Where-Object {$_[0] -ne 8728});$entries+= ,@(10339,'BG36_206')}
    $list=[Activator]::CreateInstance($definition.MakeGenericType($boardType));$position=0
    foreach($entry in $entries) {
        $card=[Activator]::CreateInstance($boardType,$true);$position++
        foreach($pair in @{EntityId=[int]$entry[0];CardId=[string]$entry[1];Position=$position;Attack=0;Health=0}.GetEnumerator()){SetMember $card $pair.Key $pair.Value}
        if($position -eq 1){SetMember $card 'Attack' $(if($Phase -eq 'old'){68}else{73});SetMember $card 'Health' $(if($Phase -eq 'old'){68}else{73})}
        $list.Add($card)
    }
    return ,$list
}
$move=[Activator]::CreateInstance($moveType,$true)
foreach($pair in @{SourceEntityId=0;SourceCardId='BG36_204_G';SourcePosition=1;MoveTo=1;Attack=73;Health=73}.GetEnumerator()){SetMember $move $pair.Key $pair.Value}
$moves728=[Activator]::CreateInstance($definition.MakeGenericType($moveType));$moves728.Add($move)
$captured=@{e=[Activator]::CreateInstance($evaluatorType,$true);board=(Captured728Board 'old');moves=$moves728;key='box-9-114-sell';turn=17}
$initial728 = EvaluateScenario $captured
if ($initial728.Reason -eq 'move_identity_mismatch') {
    $captured.board=Captured728Board 'stats-caught-up'
    Require (RecoverScenario $captured) 'Legacy exact source catch-up must reopen only cached mismatch'
    Require ((EvaluateScenario $captured).State.ToString() -eq 'Ready') 'Intermediate same-board catch-up returns native Ready'
    $captured.board=Captured728Board 'drained'
    Require ((EvaluateScenario $captured).Reason -eq 'board_changed') 'Legacy changed-board protection remains'
    Require (!(RecoverScenario $captured)) 'Changed board cannot be reset by exact catch-up recovery'
    $results.Add('PASS legacy Game728 replay: stat mismatch -> exact reset/Ready -> changed-board block retained')
} else {
    Require ($initial728.State.ToString() -eq 'Ready') 'Updated native positional binding must supply its own Ready'
    Require (!(RecoverScenario $captured)) 'No addon reset for updated native positional binding'
    $captured.board=Captured728Board 'stats-caught-up'
    Require ((EvaluateScenario $captured).State.ToString() -eq 'Ready') 'Updated native accepts its own unchanged positional source after stats catch up'
    Require (!(RecoverScenario $captured)) 'No reset introduced for caught-up positional source'
    $captured.board=Captured728Board 'drained'
    $drained728 = EvaluateScenario $captured
    Require ($drained728.State.ToString() -eq 'Blocked' -and $drained728.Reason -eq 'board_changed') 'Updated evaluator must retain its own positional decision or changed-board refusal'
    Require (!(RecoverScenario $captured)) 'Addon cannot reset an updated native board-change decision'
    $results.Add('PASS updated Game728 positional binding no longer compares old requested stats; native changed-board decision=' + $drained728.State + '/' + $drained728.Reason + '; addon reset never used')
}
$deferred=[Activator]::CreateInstance($evaluatorType,$true)
Require ($null -eq $evaluatorType.GetField('key',$flags).GetValue($deferred)) 'Deferred evaluator must remain unbound while local Power arrives'
$captured.e=$deferred
Require ((EvaluateScenario $captured).State.ToString() -eq 'Ready') 'First original binding after natural local queue drain must validate final7-card board normally'
SetMember $move 'MoveTo' 2
$captured.e=[Activator]::CreateInstance($evaluatorType,$true)
Require ((EvaluateScenario $captured).State.ToString() -eq 'Move') 'Deferral must preserve genuine required position submission, never skip it'
$results.Add('PASS deferred first binding on final captured identities validates original no-op and still requires real non-no-op move')
$report=[ordered]@{at=[DateTimeOffset]::Now.ToString('o');nativeHash=(Get-FileHash -LiteralPath $NativeHsMod).Hash;pluginHash=(Get-FileHash -LiteralPath $Plugin).Hash;nativeAssembly=$NativeHsMod;pluginAssembly=$Plugin;tests=$results.ToArray();passCount=$results.Count;liveVerified=$false;limits='Offline actual native evaluator regression and negative controls; does not prove live Harmony installation or live callback recovery.'}
if($Output){$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $Output -Encoding utf8}
$report|ConvertTo-Json -Depth 6
