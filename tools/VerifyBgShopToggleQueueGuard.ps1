[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$NativeHsMod='D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [string]$CecilPath='D:\Hearthstone\BepInEx\core\Mono.Cecil.dll',
    [string]$Output=''
)
# Offline native-gate replay and compiled hook contract checks. No game API,
# Harmony installation, native input, queue advancement or frozen-tag writes.
$ErrorActionPreference='Stop'
$results=[Collections.Generic.List[string]]::new()
function Require([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
$addonMetadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$nativeMetadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($NativeHsMod)
try{
    $guard=$addonMetadata.MainModule.GetType('ArenaBoxBot.NativeBgShopToggleQueueGuard')
    Require ($null -ne $guard) 'Compiled guard missing'
    $prefix=$guard.Methods|Where-Object Name -eq 'BeforeToggle'
    Require ($prefix.ReturnType.FullName -eq 'System.Boolean') 'Guard must only choose whether original void method runs'
    Require (@($prefix.Parameters|Where-Object {$_.ParameterType.IsByReference}).Count -eq 0) 'Guard must not alter arguments or invent an action result'
    $calls=@($prefix.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]})
    Require (@($calls|Where-Object {$_.Operand.DeclaringType.FullName -eq 'PowerProcessor' -and $_.Operand.Name -eq 'HasTaskLists'}).Count -eq 1) 'Read real native current/queued work exactly once'
    Require (@($prefix.Body.Instructions|Where-Object {$_.Operand -ceq 'freeze'}).Count -eq 1) 'Freeze advice must be scoped explicitly'
    Require (@($prefix.Body.Instructions|Where-Object {$_.Operand -ceq 'unfreeze'}).Count -eq 1) 'Unfreeze advice must retain symmetric queue safety'
    foreach($call in $calls){
        Require ($call.Operand.Name -notmatch '^(Send|SetSelected|SetTag|SetValue|Reset|Observe|Submitted|Evaluate|Dequeue|Clear|EndCurrentTaskList|ForceKill|SetRuntimeEnabled|Discard)') 'Deferred prefix cannot send input, change gate/queue/tags/ledger, or acknowledge advice'
    }
    foreach($instruction in $prefix.Body.Instructions){
        if($instruction.Operand -is [Mono.Cecil.FieldReference] -and $instruction.OpCode.Code.ToString() -match '^St'){
            Require ($instruction.Operand.DeclaringType.FullName -eq $guard.FullName) 'Only local diagnostic bookkeeping fields may be written'
        }
    }
    $nativeToggle=$nativeMetadata.MainModule.GetType('HsMod.BgBoxAiBot').Methods|Where-Object Name -eq 'ExecuteShopToggle'
    Require ($nativeToggle.ReturnType.FullName -eq 'System.Void' -and $nativeToggle.Parameters.Count -eq 4 -and
        $nativeToggle.Parameters[0].ParameterType.FullName -eq 'GameState' -and
        $nativeToggle.Parameters[1].ParameterType.FullName -eq 'Network/Options' -and
        $nativeToggle.Parameters[2].ParameterType.FullName -eq 'HsMod.BoxAiRecommendation' -and
        $nativeToggle.Parameters[3].ParameterType.FullName -eq 'System.String') 'Actual installed native hook signature changed'
    $registration=@($addonMetadata.MainModule.GetType('ArenaBoxBot.ArenaController').Methods|Where-Object {$_.Name -eq 'Update' -and $_.HasBody}|ForEach-Object {$_.Body.Instructions}|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $guard.FullName -and $_.Operand.Name -eq 'EnsureInstalled'})
    Require ($registration.Count -eq 1) 'Live update must register the guard once'
    $results.Add('PASS compiled prefix uses native HasTaskLists, scopes freeze/unfreeze, preserves arguments and only writes diagnostic fields; original native signature and live registration verified')
}finally{$addonMetadata.Dispose();$nativeMetadata.Dispose()}

$native=[Reflection.Assembly]::LoadFrom($NativeHsMod)
$flags=[Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$gateType=$native.GetType('HsMod.Utilities.BattlegroundsShopToggleGate',$true)
$observe=$gateType.GetMethod('Observe',$flags)
$submitted=$gateType.GetMethod('Submitted',$flags)
$start=[DateTime]::SpecifyKind([DateTime]::Parse('2026-10-16T22:50:42.078'),[DateTimeKind]::Utc)
function NewGate{[Activator]::CreateInstance($gateType,$true)}
function Decision($Gate,[string]$Shop,[int]$Packet,[int]$Frozen,[bool]$WantFrozen,[double]$Seconds,[int]$Turn=1,[int]$Count=4){
    $observe.Invoke($Gate,[object[]]@($Turn,$Shop,$Packet,$Count,$Frozen,$WantFrozen,$start.AddSeconds($Seconds))).ToString()
}
function Submitted($Gate,[int]$Packet,[int]$Frozen,[double]$Seconds,[int]$Count=4){
    $submitted.Invoke($Gate,[object[]]@($Packet,$Count,$Frozen,$start.AddSeconds($Seconds)))|Out-Null
}
# These two shop identities come from the captured Galakrond replacement.
# Their exact value at each native call was not logged; this reproduces the
# native pending-state loss condition, not an invented live field snapshot.
$oldShop='458,459,460,461';$newShop='458,460,461,475'
$baseline=NewGate
Require ((Decision $baseline $oldShop 4 0 $true 0) -eq 'Send') 'Native baseline first freeze must send'
Submitted $baseline 4 0 0
Require ((Decision $baseline $oldShop 5 0 $true 0.530) -ne 'Send') 'Options advance alone must not be blamed for this native reset'
$results.Add('PASS actual native gate waits on unchanged shop despite newer Options; avoids misidentifying the cause')
Require ((Decision $baseline $newShop 5 0 $true 0.530) -eq 'Send') 'Native pending gate loss on a partial changed-shop snapshot must reproduce'
$results.Add('PASS actual native gate reproduces duplicate freeze when shop identity changes before local freeze catches up')

# Replay the compiled prefix contract: while native Power is pending, the
# original method (including Observe/Submitted) is not entered. Once work
# naturally drains, only actual captured desired state is passed to native.
$guarded=NewGate
Require ((Decision $guarded $newShop 4 0 $true 0) -eq 'Send') 'Stable shop still allows original first freeze'
Submitted $guarded 4 0 0
# The compiled-prefix checks above establish that the deferred path cannot
# call Observe/Submitted or write native fields. Replay resumes only after
# the actual frozen-tag values have caught up; do not inject a fake ACK.
Require ((Decision $guarded $newShop 5 4 $true 1.244) -eq 'Satisfied') 'After real frozen tags catch up, native gate must be satisfied without another toggle'
$results.Add('PASS natural freeze acknowledgement resumes original Satisfied path without a second toggle or synthesized state')

$changedAfterDrain=NewGate
Require ((Decision $changedAfterDrain $oldShop 4 0 $true 0) -eq 'Send') 'First send setup'
Submitted $changedAfterDrain 4 0 0
Require ((Decision $changedAfterDrain $newShop 5 4 $true 1.244) -eq 'Satisfied') 'Changed shop after natural drain must not need another toggle when actual tags already satisfy advice'
$results.Add('PASS changed shop after natural drain retains original validation against real satisfied tags')

$unfreeze=NewGate
Require ((Decision $unfreeze $newShop 20 4 $false 0) -eq 'Send') 'Unfreeze must retain original first send'
Submitted $unfreeze 20 4 0
Require ((Decision $unfreeze $newShop 21 0 $false 1) -eq 'Satisfied') 'Unfreeze acknowledgement remains original Satisfied'
$results.Add('PASS legitimate unfreeze and natural acknowledgement retain native behaviour')

$retry=NewGate
Require ((Decision $retry $newShop 30 0 $true 0) -eq 'Send') 'Retry setup'
Submitted $retry 30 0 0
Require ((Decision $retry $newShop 31 0 $true 7.999) -ne 'Send') 'Native original retry threshold must be retained'
Require ((Decision $retry $newShop 31 0 $true 8.001) -eq 'Send') 'Once queue is empty, native timeout retry must remain possible'
$results.Add('PASS original bounded retry is retained when real queue is empty and no acknowledgement arrives')

# The later round13 capture directly logs frozen=0/6 then frozen=0/7 for
# the same advice within 685ms. Different item counts prove changed shop
# identity, even though the exact first six IDs were not logged.
$round13=NewGate
$partialShop='captured-six-item-snapshot';$fullShop='captured-seven-item-snapshot'
Require ((Decision $round13 $partialShop 266 0 $true 0 -Turn 25 -Count 6) -eq 'Send') 'Round13 first partial-shop toggle setup'
Submitted $round13 266 0 0 -Count 6
Require ((Decision $round13 $fullShop 267 0 $true 0.685 -Turn 25 -Count 7) -eq 'Send') 'Round13 actual 6-to-7 shop transition must reproduce duplicate native toggle'
$round13Drained=NewGate
Require ((Decision $round13Drained $fullShop 266 0 $true 0 -Turn 25 -Count 7) -eq 'Send') 'After earlier work drains, actual seven-item snapshot must allow original send'
Submitted $round13Drained 266 0 0 -Count 7
Require ((Decision $round13Drained $fullShop 267 7 $true 2.366 -Turn 25 -Count 7) -eq 'Satisfied') 'After actual seven frozen tags, original gate must satisfy without duplicate send'
$results.Add('PASS captured round13 0/6->0/7 within685ms reproduces native pending reset; deferred full seven-item snapshot and real tag catch-up require only one toggle')

$report=[ordered]@{at=[DateTimeOffset]::Now.ToString('o');passed=$true;testCount=$results.Count;nativeHash=(Get-FileHash -LiteralPath $NativeHsMod -Algorithm SHA256).Hash;pluginHash=(Get-FileHash -LiteralPath $Plugin -Algorithm SHA256).Hash;tests=@($results.ToArray());scope='Offline actual native gate plus compiled prefix contract; no game connection or input. Exact live wait/drain/no-duplicate recovery still requires installed runtime evidence.'}
if($Output){$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $Output -Encoding utf8}
$report|ConvertTo-Json -Depth 6
