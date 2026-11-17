[CmdletBinding()]
param(
    [string]$Plugin = 'D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$Native = 'D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [switch]$ExpectLegacyMissingGate
)
$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes('D:\Hearthstone\BepInEx\core\Mono.Cecil.dll')) | Out-Null
$pluginAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
$nativeAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Native)
try {
    $owner = $pluginAsm.MainModule.GetType('ArenaBoxBot.HsModNativeArenaPatch')
    $core = $owner.Methods | Where-Object Name -eq 'ResponseStallGateCore'
    $helper = $owner.Methods | Where-Object Name -eq 'PollAfterMissingRecommendationWait'
    $policy = $pluginAsm.MainModule.GetType('ArenaBoxBot.MissingRecommendationResponseGate')
    $callsHelper = @($core.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'PollAfterMissingRecommendationWait' }).Count
    if ($ExpectLegacyMissingGate) {
        if ($helper -or $policy -or $callsHelper) { throw 'Negative control already has bounded missing-recommendation polling' }
        'PASS negative control: actual R21 direct response gate has no bounded empty-recommendation path; helper-prefix recovery cannot repair the replaced call.'
        return
    }
    if (-not $helper -or -not $policy -or $callsHelper -ne 1) { throw 'Direct gate must call bounded missing-recommendation helper exactly once' }
    if (($policy.Fields | Where-Object Name -eq 'WaitSeconds').Constant -ne 2) { throw 'Missing recommendation budget changed' }
    $helperCalls = @($helper.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
    foreach ($required in @('IsGameCreated','IsGameOverNowOrPending','WasConcedeRequested','IsMulliganManagerActive','GetOptionsPacket','GetFriendlySidePlayer','GetOpposingSidePlayer','GetTag','GetGameHandle','GetTurn','Observe')) {
        if ($helperCalls -notcontains $required) { throw "Own active game/packet gate missing: $required" }
    }
    if (@($helper.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 's_boxStrategyMethod' }).Count -ne 1) { throw 'Native Box strategy must authorize polling' }
    foreach ($method in @($helper) + @($policy.Methods)) {
        if (-not $method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            $operand = $instruction.Operand
            if ($operand -is [Mono.Cecil.MethodReference] -and $operand.Name -in @('SetValue','MapOption','SendMappedOption','ResetExecutionTracking','AbortShadowCommit','ReadCurrent','SetRuntimeEnabled','Concede','EndTurn')) {
                throw "Polling helper/policy may not read another recommendation, dispatch or mutate a native guard: $instruction"
            }
            if ($operand -is [Mono.Cecil.FieldReference] -and $instruction.OpCode.Name -in @('stfld','stsfld') -and $operand.DeclaringType.FullName -notin @($policy.FullName,$owner.FullName)) {
                throw "Polling helper writes native/external state: $instruction"
            }
        }
    }
    if (@($core.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'ReadCurrent' }).Count -ne 1) { throw 'Gate must keep its single existing reader call' }
    foreach ($action in @('choice','replace')) {
        if (@($core.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $action }).Count -ne 1) { throw "Existing acknowledgement action guard missing: $action" }
    }
    $endTurnProof = $owner.Methods | Where-Object Name -eq 'CanPollUnsubmittedEndTurn'
    if ($endTurnProof) {
        if (@($core.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'CanPollUnsubmittedEndTurn' }).Count -ne 1) { throw 'End-turn acknowledgement proof must remain in the direct gate' }
        & (Join-Path $PSScriptRoot 'VerifyUnsubmittedEndTurnRecovery.ps1') -Plugin $Plugin
    }
    elseif (@($core.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'end_turn' }).Count -ne 1) { throw 'Legacy end-turn acknowledgement guard missing' }
    $nativeBot = $nativeAsm.MainModule.GetType('HsMod.WildLadderBot')
    $strategy = @($nativeBot.Methods | Where-Object Name -eq 'IsBoxAiRecommendStrategy')
    if ($strategy.Count -ne 1 -or $strategy[0].Parameters.Count -ne 0 -or $strategy[0].ReturnType.FullName -ne 'System.Boolean') { throw 'Native strategy predicate contract changed' }
    $adapter = $nativeAsm.MainModule.GetType('HsMod.BoxAiShadowAdapter')
    $execute = $adapter.Methods | Where-Object Name -eq 'TryExecuteOptionWithinBudget'
    $code = @($execute.Body.Instructions)
    $readerIndex = -1
    for ($i=0; $i -lt $code.Count; $i++) {
        if ($code[$i].Operand -is [Mono.Cecil.MethodReference] -and $code[$i].Operand.Name -eq 'ReadCurrent') { $readerIndex=$i; break }
    }
    if ($readerIndex -lt 0) { throw 'Native executor reader missing' }
    $nullGuard = $false
    for ($i=$readerIndex; $i -lt [Math]::Min($readerIndex+15,$code.Count-3); $i++) {
        if ($code[$i].OpCode.Name -eq 'ldloc.1' -and $code[$i+1].OpCode.Name -eq 'brtrue.s' -and
            $code[$i+2].OpCode.Name -eq 'ldc.i4.0' -and $code[$i+3].OpCode.Name -eq 'ret') { $nullGuard=$true;break }
    }
    if (-not $nullGuard) { throw 'Native no-recommendation false-return contract changed; reassess before deployment' }
    'PASS: exact native Box predicate and null-executor guard; bounded direct-gate polling with active own game/packet identity; existing choice/replace/end-turn ACK guards; no extra reader, native mutations or action dispatch.'
    'Plugin SHA256: ' + (Get-FileHash -LiteralPath $Plugin).Hash
} finally { $pluginAsm.Dispose(); $nativeAsm.Dispose() }
