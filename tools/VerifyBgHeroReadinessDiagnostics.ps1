[CmdletBinding()]
param(
    [string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$NativeHsMod='D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [string]$Client='D:\Hearthstone\Hearthstone_Data\Managed\Assembly-CSharp.dll',
    [string]$CecilPath='D:\Hearthstone\BepInEx\core\Mono.Cecil.dll',
    [switch]$ExpectMissingObserver,
    [string]$Output=''
)
# Offline ABI and non-interference checks for the observed 69.966s hero wait.
# No Unity execution, Harmony installation, game connection or input.
$ErrorActionPreference='Stop'
if(-not ('Mono.Cecil.AssemblyDefinition' -as [type])){
    [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
}
$addon=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$native=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($NativeHsMod)
$clientMetadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Client)
$checks=[Collections.Generic.List[string]]::new()
function Require([bool]$Ok,[string]$Message){if(!$Ok){throw $Message}}
try{
    $observer=$addon.MainModule.GetType('ArenaBoxBot.NativeBgHeroChoiceDiagnostics')
    if($ExpectMissingObserver){
        Require ($null -eq $observer) 'Negative control already has hero readiness observations'
        $checks.Add('PASS R48 negative control cannot identify the failing hero UI readiness predicate')
    }else{
        Require ($null -ne $observer) 'Hero readiness observer missing'
        $target=$native.MainModule.GetType('HsMod.Utilities.MulliganInteraction').Methods|Where-Object Name -eq 'IsUiReady'
        Require ($target.ReturnType.FullName -eq 'System.Boolean' -and $target.Parameters.Count -eq 2 -and
            $target.Parameters[0].ParameterType.FullName -eq 'GameState' -and $target.Parameters[1].ParameterType.FullName -eq 'MulliganManager') 'Original UI readiness ABI changed'
        $checks.Add('PASS actual original IsUiReady ABI matches; original result is observed')
        $post=$observer.Methods|Where-Object Name -eq 'AfterUiReady'
        Require ($post.ReturnType.FullName -eq 'System.Void' -and $post.Parameters.Count -eq 3 -and
            @($post.Parameters|Where-Object {$_.ParameterType.IsByReference}).Count -eq 0 -and
            $post.Parameters[2].Name -eq '__result' -and $post.Parameters[2].ParameterType.FullName -eq 'System.Boolean') 'Postfix may not replace original result or arguments'
        $calls=@($post.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})
        foreach($name in 'IsBattlegrounds','GetFriendlyEntityChoices','get_ChoiceType','get_isActiveAndEnabled','IsMulliganActive','IsMulliganIntroActive','IsTransitioning','GetBooleanGameOption','GetMulliganButton','get_activeInHierarchy','PermitDecisionMakingInput'){
            Require ($calls -contains $name) "Actual predicate/scope observation missing: $name"
        }
        Require (@($post.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'MULLIGAN'}).Count -eq 1) 'Only original BG mulligan choices may be logged'
        $checks.Add('PASS BG mulligan scope and actual UI predicate reads retained')
        foreach($ins in $post.Body.Instructions){
            $operand=$ins.Operand
            if($ins.OpCode.Name -in 'stfld','stsfld'){
                Require ($operand.DeclaringType.FullName -eq $observer.FullName) "Observer writes external field: $ins"
            }
            if($operand -is [Mono.Cecil.MethodReference]){
                Require ($operand.Name -notmatch '^(set_|Send|Submit|Confirm|Select|Advance|Cancel|Reset|ReadCurrent$|SetValue$|Observe$|IsUiReady$|HeroMulliganNotReady$)') "Observer calls stateful reader/gate/action: $ins"
            }
        }
        $checks.Add('PASS postfix has no result writes, native field writes, recommendation reads, input, queue or gate calls')
        $permit=$clientMetadata.MainModule.GetType('InputManager').Methods|Where-Object Name -eq 'PermitDecisionMakingInput'
        $permitCalls=@($permit.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.FullName})
        $allowed=@('GameMgr GameMgr::Get()','System.Boolean GameMgr::IsSpectator()','Player GameState::GetFriendlySidePlayer()','System.Boolean EntityBase::HasTag(GAME_TAG)')
        Require (@($permitCalls|Where-Object {$_ -notin $allowed}).Count -eq 0 -and
            @($permit.Body.Instructions|Where-Object {$_.OpCode.Name -match '^(stfld|stsfld|starg|throw|newobj)'}).Count -eq 0) 'Reassess PermitDecisionMakingInput side effects for current client'
        $checks.Add('PASS actual client input-permission getter only reads spectator/player state; no mutation')
        $manager=$clientMetadata.MainModule.GetType('MulliganManager')
        Require (@($manager.Fields|Where-Object {$_.Name -eq 'm_waitingForUserInput' -and $_.FieldType.FullName -eq 'System.Boolean'}).Count -eq 1) 'Waiting predicate field ABI changed'
        $install=$observer.Methods|Where-Object Name -eq 'EnsureInstalled'
        Require (@($install.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'AfterUiReady'}).Count -eq 1) 'Original postfix registration missing'
        Require (@($post.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldc.r4' -and $_.Operand -eq 1}).Count -eq 1 -and
            @($post.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldc.r4' -and $_.Operand -eq 5}).Count -eq 1) 'Sampling/repeat intervals changed'
        $checks.Add('PASS exact waiting field and postfix registration; one-second samples/five-second unchanged repeats')
    }
    $result=[ordered]@{at=[DateTimeOffset]::Now.ToString('o');plugin=(Get-FileHash -LiteralPath $Plugin).Hash;native=(Get-FileHash -LiteralPath $NativeHsMod).Hash;client=(Get-FileHash -LiteralPath $Client).Hash;passed=$checks.Count;checks=@($checks);liveVerified=$false}
    if($Output){$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $Output -Encoding utf8}
    $result|ConvertTo-Json -Depth 5
}finally{$addon.Dispose();$native.Dispose();$clientMetadata.Dispose()}
