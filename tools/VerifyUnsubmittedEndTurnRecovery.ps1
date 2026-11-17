[CmdletBinding()]
param(
    [string]$Plugin = 'D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
    [switch]$ExpectLegacyEndTurnHold
)
$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes('D:\Hearthstone\BepInEx\core\Mono.Cecil.dll')) | Out-Null
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
try {
    $owner = $assembly.MainModule.GetType('ArenaBoxBot.HsModNativeArenaPatch')
    $core = $owner.Methods | Where-Object Name -eq 'ResponseStallGateCore'
    $helper = $owner.Methods | Where-Object Name -eq 'CanPollUnsubmittedEndTurn'
    if ($ExpectLegacyEndTurnHold) {
        if ($helper) { throw 'Negative control already supports proof of unsubmitted end-turn recovery' }
        'PASS negative control: actual R24 has no current-packet ledger/shadow evidence path for unsubmitted end turns.'
        return
    }
    if (-not $helper) { throw 'End-turn recovery helper missing' }
    $calls = @($core.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
    if (@($calls | Where-Object { $_ -eq 'CanPollUnsubmittedEndTurn' }).Count -ne 1) { throw 'Direct response gate must guard end turns exactly once' }
    if (@($core.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'ReadCurrent' }).Count -ne 1) { throw 'Existing single native recommendation read changed' }
    $helperCalls = @($helper.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } | ForEach-Object { $_.Operand.Name })
    foreach ($required in @('IsGameCreated','IsGameOverNowOrPending','WasConcedeRequested','IsMulliganManagerActive','GetFriendlySidePlayer','GetOpposingSidePlayer','GetOptionsPacket','GetFriendlyEntityChoices','IsInChoiceMode','PacketEntryBlocks','CanPoll','Contains')) {
        if ($helperCalls -notcontains $required) { throw "Safety evidence missing: $required" }
    }
    $constants = @($helper.Body.Instructions | Where-Object { $_.OpCode.Name -eq 'ldstr' } | ForEach-Object Operand)
    foreach ($required in @('s_actionLedger','entries','s_shadowPolicy','HasActiveCommit','ExecutionKey','PacketId','Domain','CapturedAt','OptionId','GamePid','RequestId')) {
        if ($constants -notcontains $required) { throw "Exact native metadata missing: $required" }
    }
    foreach ($method in @($helper) + @($assembly.MainModule.GetType('ArenaBoxBot.UnsubmittedEndTurnRecoveryPolicy').Methods)) {
        if (-not $method.HasBody) { continue }
        foreach ($instruction in $method.Body.Instructions) {
            $operand = $instruction.Operand
            if ($operand -is [Mono.Cecil.MethodReference] -and $operand.Name -in @('ReadCurrent','AlreadyExecuted','IsBlocked','Reset','MarkSubmitted','SendOption','TryExecuteOptionWithinBudget','EndTurn','SetValue','Kill','CloseMainWindow')) {
                throw "Recovery evidence may not reread/mutate a ledger or dispatch commands: $instruction"
            }
            if ($operand -is [Mono.Cecil.FieldReference] -and $instruction.OpCode.Name -in @('stfld','stsfld') -and $operand.DeclaringType.Namespace -ne 'ArenaBoxBot') {
                throw "Recovery writes native fields: $instruction"
            }
        }
    }
    'PASS: direct gate guards fresh unsubmitted end turns using current PID/packet/time, known empty current-packet ledger, no shadow/choice; no second ReadCurrent, stateful ledger call, native field write, or action dispatch in helper.'
    'Plugin SHA256: ' + (Get-FileHash -LiteralPath $Plugin -Algorithm SHA256).Hash
}
finally { $assembly.Dispose() }
