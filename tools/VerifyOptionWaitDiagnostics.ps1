[CmdletBinding()]
param(
    [string]$Plugin = 'D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [string]$Native = 'D:\Hearthstone\BepInEx\plugins\HsMod.dll',
    [switch]$ExpectLegacyObserver,
    [switch]$ExpectLegacyResponseGate,
    [switch]$ExpectLegacyReadinessCapture
)
$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes('D:\Hearthstone\BepInEx\core\Mono.Cecil.dll')) | Out-Null
$nativeAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Native)
$pluginAsm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
try {
    $adapter = $nativeAsm.MainModule.Types | Where-Object FullName -eq 'HsMod.BoxAiShadowAdapter'
    $target = @($adapter.Methods | Where-Object Name -eq 'TryExecuteOptionWithinBudget')
    if ($target.Count -ne 1 -or $target[0].ReturnType.FullName -ne 'System.Boolean' -or
        $target[0].Parameters[2].ParameterType.FullName -ne 'System.Single') { throw 'Native execution signature differs' }
    $reader = @($adapter.Methods | Where-Object Name -eq 'ReadCurrent')
    if ($reader.Count -ne 1 -or $reader[0].ReturnType.FullName -ne 'HsMod.BoxAiRecommendation') { throw 'Native reader signature differs' }
    $readerCanReset = @($reader[0].Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ResetExecutionTracking' }).Count -gt 0
    if (-not $readerCanReset) { throw 'Reassess reader side effects for this native build' }
    $checks = @{
        'HsMod.BoxAiShadowAdapter' = @('s_actionLedger','s_reactionGate','s_shadowPolicy')
        'HsMod.AutomationActionLedger' = @('entries')
        'HsMod.AutomationReactionGate' = @('key','delaySeconds')
        'HsMod.UserUiShadowPolicy' = @('commitPacketId','commitKey','stageCursor','stages','commitReadyAt')
        'HsMod.BoxAiRecommendation' = @('CapturedAt','RequestId','SchemaVersion','ProducerInstanceId','PublicationSeq','GamePid')
    }
    foreach ($name in $checks.Keys) {
        $type = $nativeAsm.MainModule.Types | Where-Object FullName -eq $name
        foreach ($field in $checks[$name]) {
            if (@($type.Fields | Where-Object Name -eq $field).Count -ne 1) { throw "Native field unavailable: $name/$field" }
        }
    }
    $entry = ($nativeAsm.MainModule.Types | Where-Object FullName -eq 'HsMod.AutomationActionLedger').NestedTypes | Where-Object Name -eq 'Entry'
    foreach ($field in @('State','PacketId','Attempts')) {
        if (@($entry.Fields | Where-Object Name -eq $field).Count -ne 1) { throw "Native ledger entry field unavailable: $field" }
    }
    $observer = $pluginAsm.MainModule.Types | Where-Object FullName -eq 'ArenaBoxBot.NativeOptionExecutionDiagnostics'
    $after = $observer.Methods | Where-Object Name -eq 'AfterAttempt'
    $readAfter = $observer.Methods | Where-Object Name -eq 'AfterNativeRead'
    $legacyCallsReader = @($after.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 's_readCurrent' }).Count -gt 0
    if ($ExpectLegacyObserver) {
        if (-not $legacyCallsReader -or $readAfter) { throw 'Negative control is not the legacy observer' }
        'PASS negative control: legacy diagnostic re-invokes the stateful reader; new passive-read contract rejects it.'
        return
    }
    if ($legacyCallsReader -or -not $readAfter) { throw 'Diagnostic must passively capture native reader results, not invoke reader' }
    foreach ($method in @($after, $readAfter)) {
        if (@($method.Parameters | Where-Object { $_.ParameterType.IsByReference }).Count -ne 0) { throw 'Observer may not mutate native arguments/result' }
    }
    foreach ($method in $observer.Methods) {
        if (-not $method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            $operand = $instruction.Operand
            if ($operand -is [Mono.Cecil.FieldReference] -and $instruction.OpCode.Name -in @('stfld','stsfld') -and
                $operand.DeclaringType.FullName -ne $observer.FullName -and
                -not $operand.DeclaringType.FullName.StartsWith($observer.FullName + '/')) { throw "Observer writes external state: $instruction" }
            if ($operand -is [Mono.Cecil.MethodReference] -and $operand.Name -in @('IsBlocked','ResetExecutionTracking','AbortShadowCommit','SendMappedOption','MapOption','ReactionReady','SetValue')) {
                throw "Observer invokes a native guard/action or setter: $instruction"
            }
        }
    }
    $install = $observer.Methods | Where-Object Name -eq 'EnsureInstalled'
    foreach ($postfixName in @('AfterAttempt','AfterNativeRead')) {
        if (@($install.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq $postfixName }).Count -ne 1) { throw "Postfix registration missing: $postfixName" }
    }
    $responseObserver = $observer.Methods | Where-Object Name -eq 'ObserveResponseGate'
    if ($ExpectLegacyResponseGate) {
        if ($responseObserver) { throw 'Negative control already observes response-gate waits' }
        'PASS negative control: old executor observer cannot report waits before TryExecuteOptionWithinBudget.'
        return
    }
    if (-not $responseObserver -or @($responseObserver.Parameters | Where-Object { $_.ParameterType.IsByReference }).Count) {
        throw 'Response gate observer must exist and may not mutate arguments/result'
    }
    # The gate lives with the HsMod compatibility patches, whose type name may
    # differ from the Arena mode adapter. Locate by the exact private method.
    $gateType = @($pluginAsm.MainModule.Types | Where-Object { @($_.Methods | Where-Object Name -eq 'ResponseStallGate').Count })
    if ($gateType.Count -ne 1) { throw 'Response gate owner ambiguous' }
    $gate = $gateType[0].Methods | Where-Object Name -eq 'ResponseStallGate'
    $core = $gateType[0].Methods | Where-Object Name -eq 'ResponseStallGateCore'
    $body = @($gate.Body.Instructions)
    $calls = @($body | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
    if ($calls.Count -ne 2 -or $calls[0].Operand.Name -ne 'ResponseStallGateCore' -or
        $calls[1].Operand.Name -ne 'ObserveResponseGate') { throw 'Wrapper must invoke original gate core and passive observer once each' }
    if (@($body | Where-Object { $_.OpCode.FlowControl.ToString() -in @('Branch','Cond_Branch','Throw') }).Count) {
        throw 'Observer wrapper may not choose or replace a gate result'
    }
    $resultStore = $body | Where-Object { $_.OpCode.Name -eq 'stloc.0' }
    if (@($resultStore).Count -ne 1 -or $body[-2].OpCode.Name -ne 'ldloc.0' -or $body[-1].OpCode.Name -ne 'ret') {
        throw 'Wrapper must return the exact core result without overwriting it'
    }
    if (@($core.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'ReadCurrent' }).Count -ne 1 -or
        @($responseObserver.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'ReadCurrent' }).Count) {
        throw 'Diagnostics must reuse the existing gate read, never invoke native ReadCurrent again'
    }
    $readiness = $observer.Methods | Where-Object Name -eq 'ObserveNativeReadReadiness'
    if ($ExpectLegacyReadinessCapture) {
        if ($readiness) { throw 'Negative control already captures native reader readiness' }
        'PASS negative control: old observer misses reader readiness before own-turn/execution gates.'
        return
    }
    if (-not $readiness -or @($readiness.Parameters | Where-Object { $_.ParameterType.IsByReference }).Count) {
        throw 'Passive native reader readiness capture missing or mutates parameters'
    }
    if (@($readAfter.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'ObserveNativeReadReadiness' }).Count -ne 1) {
        throw 'Existing reader postfix must call the passive readiness observer exactly once'
    }
    foreach ($name in @('GetTurn','GetFriendlySidePlayer','GetOpposingSidePlayer','IsMulliganManagerActive','IsMulliganBusy','IsBusy')) {
        if (@($readiness.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq $name }).Count -ne 1) {
            throw "Native readiness observation missing or repeated: $name"
        }
    }
    if (@($readiness.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'ReadCurrent' }).Count) {
        throw 'Native readiness capture must not invoke the stateful reader'
    }
    'PASS: exact native members; legacy reader mutation reproduced by negative control; postfix args/results immutable; no external field writes or guard/action/setter calls.'
    'PASS: pre-executor response waits observed; exact gate result returned; no extra stateful recommendation read.'
    'PASS: native reader readiness captured before own-turn/execution gates; native flags read once; no argument/result mutation.'
    'Plugin SHA256: ' + (Get-FileHash -LiteralPath $Plugin).Hash
} finally { $pluginAsm.Dispose(); $nativeAsm.Dispose() }
