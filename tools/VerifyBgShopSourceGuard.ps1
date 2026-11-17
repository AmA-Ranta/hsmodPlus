[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$Native='D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [string]$GameAssembly='D:\Hearthstone\Hearthstone_Data\Managed\Assembly-CSharp.dll',
    [string]$CecilPath='D:\Hearthstone\BepInEx\core\Mono.Cecil.dll',
    [string]$Output=''
)
# Offline exact captured purchase identity selection and compiled hook checks.
# No game connection, input, state mutation or replacement recommendation.
$ErrorActionPreference='Stop'
$results=[Collections.Generic.List[string]]::new()
function Require([bool]$Ok,[string]$Message){if(!$Ok){throw $Message}}
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
$addon=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$nativeMetadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Native)
try{
    $guard=$addon.MainModule.GetType('ArenaBoxBot.NativeBgShopSourceGuard')
    Require ($null -ne $guard) 'Compiled shop source guard missing'
    $source=@($nativeMetadata.MainModule.GetType('HsMod.BgBoxAiBot').Methods|Where-Object Name -eq 'ResolveSource')
    Require ($source.Count -eq 1 -and $source[0].ReturnType.FullName -eq 'Entity' -and
        $source[0].Parameters.Count -eq 2 -and $source[0].Parameters[0].ParameterType.FullName -eq 'GameState' -and
        $source[0].Parameters[1].ParameterType.FullName -eq 'HsMod.BoxAiRecommendation') 'Actual native resolver ABI changed'
    $nativeCalls=@($source[0].Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    Require ($nativeCalls -contains 'GetEntityMap' -and $nativeCalls -notcontains 'GetOptionsPacket' -and $nativeCalls -notcontains 'TargetAllowed') 'Native first entity-map match must reproduce the missing purchase-eligibility check'
    $results.Add('PASS actual native ResolveSource scans entity map without legal purchase target filtering; hook ABI verified')
    $post=$guard.Methods|Where-Object Name -eq 'AfterSource'
    Require ($post.ReturnType.FullName -eq 'System.Void' -and $post.Parameters.Count -eq 3 -and
        $post.Parameters[2].Name -eq '__result' -and $post.Parameters[2].ParameterType.FullName -eq 'Entity&') 'Only original resolver result may be corrected'
    $postCalls=@($post.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    Require ($postCalls -contains 'GetCardType' -and $postCalls -contains 'IsPurchaseCardType') 'Captured CARDTYPE42 Tavern spells must reach the compiled purchase type policy; ordinary IsSpell alone excludes them'
    $current=$guard.Methods|Where-Object Name -eq 'Current'
    $calls=@($current.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
    foreach($name in @('GetOptionsPacket','GetFriendlySidePlayer','GetOpposingSidePlayer','IsGameCreated','IsGameOverNowOrPending','WasConcedeRequested','GetTurn','IsMulliganManagerActive','IsInChoiceMode','GetFriendlyEntityChoices','GetResponseMode','GetMode','GetTag','HasTaskLists','IsCurrent')){
        Require ($calls -contains $name) "Missing current own OPTION/quiet native queue guard: $name"
    }
    foreach($method in $guard.Methods){if(!$method.HasBody){continue};foreach($i in $method.Body.Instructions){
        if($i.Operand -is [Mono.Cecil.MethodReference]){
            Require ($i.Operand.Name -notmatch '^(ReadCurrent|SendOption|SendChoices|SendEntityChoice|SetSelected|SetTag|SetValue|Reset|Submitted|RecordSubmitted|MarkSubmitted|Discard|Dequeue|Clear|EndCurrentTaskList|ForceKill|SetRuntimeEnabled|Concede|Invalidate)') 'Resolver must not send input, change advice/gates/ledger/queue or manufacture acknowledgement'
        }
        if($i.Operand -is [Mono.Cecil.FieldReference] -and $i.OpCode.Code.ToString() -match '^St'){
            Require ($i.Operand.DeclaringType.FullName -in @($guard.FullName,'ArenaBoxBot.OptionEntityCandidate')) 'Only diagnostic fields and local candidate snapshots may be written'
        }
    }}
    $registration=@($addon.MainModule.GetType('ArenaBoxBot.ArenaController').Methods|Where-Object {$_.Name -eq 'Update' -and $_.HasBody}|ForEach-Object {$_.Body.Instructions}|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $guard.FullName -and $_.Operand.Name -eq 'EnsureInstalled'})
    Require ($registration.Count -eq 1) 'Guard must be registered in the live update'
    $results.Add('PASS compiled postfix only corrects Entity result; own phase, choice, packet, natural queue drain and no-dispatch/no-state-write contracts verified')
}finally{$addon.Dispose();$nativeMetadata.Dispose()}

$asm=[Reflection.Assembly]::LoadFrom($Plugin)
$flags=[Reflection.BindingFlags]'Static,Instance,Public,NonPublic'
$candidateType=$asm.GetType('ArenaBoxBot.OptionEntityCandidate',$true)
$policy=$asm.GetType('ArenaBoxBot.OptionEntityFallbackPolicy',$true)
$scope=$asm.GetType('ArenaBoxBot.BgShopSourcePolicy',$true).GetMethod('IsCurrent',$flags)
$purchaseType=$asm.GetType('ArenaBoxBot.BgShopSourcePolicy',$true).GetMethod('IsPurchaseCardType',$flags)
Require ($null -ne $purchaseType) 'Compiled purchase type policy missing'
$resolve=$policy.GetMethod('Resolve',$flags)
function Candidate([int]$Id,[string]$Card='BG28_604',[int]$Position=6,[int]$Owner=9,[string]$Zone='PLAY',[bool]$Legal=$true){
    $c=[Activator]::CreateInstance($candidateType,$true)
    foreach($pair in @{Id=$Id;CardId=$Card;Position=$Position;Controller=$Owner;Zone=$Zone;Legal=$Legal}.GetEnumerator()){$candidateType.GetField($pair.Key,$flags).SetValue($c,$pair.Value)}
    return $c
}
function Resolve($Items,[string]$Card='BG28_604',[int]$Position=6,[int]$Owner=9){
    $array=[Array]::CreateInstance($candidateType,@($Items).Count)
    for($i=0;$i -lt @($Items).Count;$i++){$array.SetValue(@($Items)[$i],$i)}
    [int]$resolve.Invoke($null,[object[]]@($array,$Card,$Position,$Owner,'PLAY'))
}
function Scope([string]$Action='buy_special',[int]$Explicit=0,[string]$Zone='',
    [int]$ProcessIdNow=22688,[int]$RecPid=22688,[int]$Packet=108,[int]$RecPacket=108,[double]$Age=.042,[bool]$Active=$true){
    [bool]$scope.Invoke($null,[object[]]@($ProcessIdNow,$RecPid,$Packet,$RecPacket,$Age,$Active,$Action,$Explicit,$Zone))
}
# Actual Game2171/packet60: BG28_800 old hand6743 and shop6838 share slot6.
# Power declares CARDTYPE=BATTLEGROUND_SPELL42. The old IsSpell predicate
# compares only against SPELL5 and therefore filters out the valid shop card.
$gameMetadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($GameAssembly)
try{
    $cardTypes=$gameMetadata.MainModule.GetType('TAG_CARDTYPE')
    $tavernSpell=($cardTypes.Fields|Where-Object Name -eq 'BATTLEGROUND_SPELL').Constant
    $ordinarySpell=($cardTypes.Fields|Where-Object Name -eq 'SPELL').Constant
    $minion=($cardTypes.Fields|Where-Object Name -eq 'MINION').Constant
    Require ($tavernSpell -eq 42 -and $ordinarySpell -eq 5 -and $minion -eq 4) 'Actual game card type ABI changed'
    $nativeIsSpell=$gameMetadata.MainModule.GetType('EntityBase').Methods|Where-Object Name -eq 'IsSpell'
    $nativeIsBacon=$gameMetadata.MainModule.GetType('EntityBase').Methods|Where-Object Name -eq 'IsBaconSpell'
    Require (@($nativeIsSpell.Body.Instructions|Where-Object {$_.OpCode.Code.ToString() -eq 'Ldc_I4_5'}).Count -eq 1 -and
        @($nativeIsBacon.Body.Instructions|Where-Object {$_.Operand -eq 42}).Count -eq 1) 'Actual predicates must prove ordinary and Tavern spells differ'
    Require ([bool]$purchaseType.Invoke($null,[object[]]@('buy_special',[int]$tavernSpell))) 'Captured actual Tavern spell type42 must be eligible for exact live shop target selection'
    Require ([bool]$purchaseType.Invoke($null,[object[]]@('buy_special',[int]$ordinarySpell))) 'Existing ordinary spell support must remain'
    Require ([bool]$purchaseType.Invoke($null,[object[]]@('buy',[int]$minion))) 'Ordinary minion purchase must remain'
    foreach($type in @($cardTypes.Fields|Where-Object HasConstant)){
        $value=[int]$type.Constant
        if($value -notin @([int]$tavernSpell,[int]$ordinarySpell)){
            Require (!([bool]$purchaseType.Invoke($null,[object[]]@('buy_special',$value)))) ('Non-spell purchase type must remain excluded: '+$type.Name)
        }
        if($value -ne [int]$minion){Require (!([bool]$purchaseType.Invoke($null,[object[]]@('buy',$value)))) 'Ordinary buy cannot redirect to a spell or another card type'}
    }
    Require (!([bool]$purchaseType.Invoke($null,[object[]]@('special',42)))) 'Playing a hand spell is not a purchase correction'
    $results.Add('PASS actual EntityBase.IsSpell excludes CARDTYPE42; compiled policy includes Tavern spells and preserves ordinary spells/minions while rejecting every unrelated actual game card type')
}finally{$gameMetadata.Dispose()}
Require ((Resolve @((Candidate 6743 -Card 'BG28_800' -Owner 1 -Zone 'HAND' -Legal $false),(Candidate 6838 -Card 'BG28_800')) -Card 'BG28_800') -eq 6838) 'Captured packet60 must choose the current legal shop spell6838 instead of already-bought same-card/slot hand6743'
Require ((Resolve @((Candidate 6838 -Card 'BG28_800' -Legal $false)) -Card 'BG28_800') -eq 0) 'Recognizing Tavern spell type cannot invent an unavailable legal purchase'
$results.Add('PASS Game2171 packet60 captured same-card/slot identity chooses legal shop6838; unavailable target keeps original result')
# Captured Game825/packet108: own HAND11303 at slot6 is not a DragBuy target;
# actual DragBuy_Spell12321 has legal target12306, cardBG28_604/shopslot6.
Require ((Resolve @((Candidate 12306))) -eq 12306) 'Captured exact eligible shop target must resolve'
$results.Add('PASS captured packet108 resolves legal shop target12306, instead of own hand11303 at the same slot')
Require ((Resolve @((Candidate 11303 -Zone 'HAND' -Owner 1 -Legal $false),(Candidate 12306))) -eq 12306) 'Non-shop result must not mask the current unique legal target'
Require ((Resolve @((Candidate 12306),(Candidate 12306))) -eq 12306) 'Repeated targets for one identity are not ambiguity'
$results.Add('PASS same-card/slot own hand identity is excluded; duplicate entries for the same eligible shop identity are preserved')
Require ((Resolve @((Candidate 12306),(Candidate 12307))) -eq 0) 'Two exact legal entities are ambiguous'
Require ((Resolve @()) -eq 0) 'No legal target must preserve original result'
$results.Add('PASS ambiguity and unavailable purchase options cannot invent a source')
foreach($bad in @((Candidate 12306 -Position 5),(Candidate 12306 -Owner 1),(Candidate 12306 -Zone 'HAND'),(Candidate 12306 -Card 'BG28_604_G'),(Candidate 12306 -Legal $false))){Require ((Resolve @($bad)) -eq 0) 'Wrong slot/owner/zone/card or illegal target must be rejected'}
$results.Add('PASS exact requested card, shop position, opposing owner, PLAY zone and legal target are required')
Require (Scope) 'Captured fresh current buy_special scope'
Require (Scope -Action 'buy' -Zone 'baconshop') 'Ordinary buy remains supported through its own legal DragBuy button'
foreach($action in @('play','special','sell','freeze','end_turn','choice','pre_adjust')){Require (!(Scope -Action $action)) 'Other native actions must remain untouched'}
$results.Add('PASS only buy/buy_special are eligible; other actions retain their original resolver')
Require (!(Scope -Explicit 11303)) 'Explicit entity identity cannot be replaced'
Require (!(Scope -Explicit -1)) 'Missing schema cannot be treated as an omitted identity'
Require (!(Scope -Zone 'hand')) 'Explicit non-shop source zone cannot be replaced'
$results.Add('PASS explicit identities and non-shop zones cannot be redirected')
Require (!(Scope -RecPid 15128)) 'Old process recommendation cannot be redirected'
Require (!(Scope -RecPacket 107)) 'Old option packet cannot be redirected'
foreach($age in @(-.001,10.001,[double]::NaN,[double]::PositiveInfinity)){Require (!(Scope -Age $age)) 'Future, stale or invalid recommendation age must be rejected'}
Require (!(Scope -Active $false)) 'Combat, choice, game-over, pending queue and unknown active state must remain original'
$results.Add('PASS current PID/packet/age and active own phase are required; no stale advice or pending queue bypass')
$report=[ordered]@{at=Get-Date -Format o;passed=$true;testCount=$results.Count;pluginHash=(Get-FileHash -LiteralPath $Plugin).Hash;nativeHash=(Get-FileHash -LiteralPath $Native).Hash;tests=@($results.ToArray());scope='Offline captured identity policy and compiled/native ABI. Actual installed corrected source -> native mapper/send -> real purchase Power still requires live verification.'}
if($Output){$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $Output -Encoding utf8}
$report|ConvertTo-Json -Depth 6
