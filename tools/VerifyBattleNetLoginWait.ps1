[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Controller,
    [string]$CecilPath='D:\Hearthstone\BepInEx\core\Mono.Cecil.dll',
    [switch]$ExpectMissingProbe,
    [string]$Output=''
)
# Offline inspection of the actual published controller. Does not load or
# execute UIA, launch an application, access credentials or send any input.
$ErrorActionPreference='Stop'
if(-not ('Mono.Cecil.AssemblyDefinition' -as [type])){
    [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($CecilPath))
}
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Controller)
$checks=[Collections.Generic.List[string]]::new()
function Require([bool]$Ok,[string]$Message){if(!$Ok){throw $Message}}
function BodyOf($Type,[string]$Name){
    $method=$Type.Methods|Where-Object Name -eq $Name|Select-Object -First 1
    Require ($null -ne $method) "Missing method $Name"
    $async=$method.CustomAttributes|Where-Object {$_.AttributeType.Name -eq 'AsyncStateMachineAttribute'}
    if($async){return ($Type.NestedTypes|Where-Object FullName -eq $async.ConstructorArguments[0].Value.FullName).Methods|Where-Object Name -eq 'MoveNext'}
    return $method
}
function Calls($Method){@($Method.Body.Instructions|Where-Object {$_.Operand -is [Mono.Cecil.MethodReference]}|ForEach-Object {$_.Operand.Name})}
try{
    $probe=$assembly.MainModule.GetType('ArenaController.BattleNetLoginProbe')
    if($ExpectMissingProbe){
        Require ($null -eq $probe) 'Negative control already has the new internal login probe'
        $checks.Add('PASS prior controller lacks internal login-modal observations')
    }else{
        Require ($null -ne $probe) 'Published login probe missing'
        $window=BodyOf $probe 'ReadWindow'
        $strings=@($window.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldstr'}|ForEach-Object Operand)
        foreach($name in 'UIAutomationClient','UIAutomationTypes','LoginWindow','AutomationIdProperty','IsOffscreenProperty','IsEnabledProperty','NameProperty','FindFirst','FindAll','GetCurrentPropertyValue'){
            Require ($strings -contains $name) "Actual read-only modal/play predicate missing: $name"
        }
        $checks.Add('PASS internal LoginWindow plus visible/enabled game-specific Play predicates are in published code')
        foreach($method in $probe.Methods|Where-Object HasBody){
            foreach($instruction in $method.Body.Instructions){
                if($instruction.Operand -is [Mono.Cecil.MethodReference]){
                    Require ($instruction.Operand.Name -notmatch '^(SendInput|SendKeys|SetValue|SetFocus|Click|InvokePattern|CloseMainWindow|Kill|Start|WriteAll|SetForegroundWindow)$') "Probe contains input or process mutation: $instruction"
                }
                if($instruction.OpCode.Name -in 'stfld','stsfld'){
                    Require ($instruction.Operand.DeclaringType.FullName.StartsWith($probe.FullName)) "Probe writes outside its own observation cache: $instruction"
                }
            }
        }
        $checks.Add('PASS probe performs no credential input, process mutation or external state writes')
        $read=BodyOf $probe 'Read'
        Require ((Calls $read) -contains 'Run' -and (Calls $read) -contains 'get_IsCompleted') 'UIA must run on the bounded background observation task'
        Require (@($probe.Fields|Where-Object Name -eq '_pending').Count -eq 1 -and @($read.Body.Instructions|Where-Object {$_.OpCode.Name -eq 'ldc.r8' -and $_.Operand -eq 5}).Count -eq 1) 'Single pending task/five-second observation cache changed'
        $checks.Add('PASS single background observation and five-second cache keep UI polling bounded')
        $form=$assembly.MainModule.GetType('ArenaController.MainForm')
        foreach($name in 'StartTaskAsync','ReconnectHearthstoneAsync','RebuildBattleNetSessionWhileRunningAsync','RecoverStuckClientAsync'){
            $body=BodyOf $form $name
            Require (@($body.Body.ExceptionHandlers|Where-Object {$_.CatchType.FullName -eq 'ArenaController.BattleNetAuthenticationRequiredException'}).Count -eq 1 -and (Calls $body) -contains 'PauseForBattleNetPrerequisite') "Recovery path does not preserve authentication wait: $name"
        }
        $checks.Add('PASS startup and all three recovery paths explicitly catch authentication prerequisites')
        $pause=BodyOf $form 'PauseForBattleNetPrerequisite'
        $writes=@($pause.Body.Instructions|Where-Object {$_.OpCode.Name -in 'stfld','stsfld'}|ForEach-Object {$_.Operand.Name})
        Require (@($writes|Where-Object {$_ -notin '_startupRetryCount','_awaitingBattleNetLogin','_lastStartupRetryAt'}).Count -eq 0) 'Login wait changes task baseline, running state or quota'
        Require (@((Calls $pause)|Where-Object {$_ -match '^(RequestStop|Publish|Disable|Enable|StartTask|Reconnect|Write)' }).Count -eq 0) 'Login wait must not submit a stop/start or native mutation'
        Require ((Calls $pause) -contains 'get_LastKnown') 'A pending or failed probe cannot reset the failed-batch baseline'
        $checks.Add('PASS prerequisite wait retains running-task baseline/quota and publishes no native stop/start')
        foreach($name in 'LaunchHearthstoneViaBattleNet','WaitForHearthstoneProcessAsync'){
            Require ((Calls (BodyOf $form $name)) -contains 'HasBattleNetLoginWindow') "Original launch/login path missing prerequisite guard: $name"
        }
        Require ((Calls (BodyOf $form 'RequestStop')) -contains 'Reset') 'Manual stop must cancel prerequisite wait'
        Require ((Calls (BodyOf $form 'MonitorTick')) -contains 'CanResumeBattleNetPrerequisite') 'Periodic recovery must use the one-resume state gate'
        $checks.Add('PASS official launch/wait guards and manual cancellation connect to the state gate')
        $directory=Split-Path -Parent ([IO.Path]::GetFullPath($Controller))
        foreach($name in 'UIAutomationClient.dll','UIAutomationTypes.dll'){
            $path=Join-Path $directory $name
            Require (Test-Path -LiteralPath $path -PathType Leaf) "Missing bundled desktop dependency $name"
            $metadata=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
            try{
                if($name -eq 'UIAutomationClient.dll'){
                    $element=$metadata.MainModule.GetType('System.Windows.Automation.AutomationElement')
                    foreach($member in 'FromHandle','FindFirst','FindAll','GetCurrentPropertyValue'){
                        Require (@($element.Methods|Where-Object Name -eq $member).Count -gt 0) "Bundled UIA ABI missing $member"
                    }
                    Require ($null -ne $metadata.MainModule.GetType('System.Windows.Automation.PropertyCondition')) 'PropertyCondition ABI missing'
                }else{
                    foreach($type in 'AutomationProperty','TreeScope','ControlType'){
                        Require ($null -ne $metadata.MainModule.GetType("System.Windows.Automation.$type")) "Bundled UIA ABI missing $type"
                    }
                }
            }finally{$metadata.Dispose()}
        }
        $checks.Add('PASS self-contained package ships required UIA metadata; no external runtime installation required')
    }
    $result=[ordered]@{at=[DateTimeOffset]::Now.ToString('o');controller=(Get-FileHash -LiteralPath $Controller).Hash;passed=$checks.Count;checks=@($checks);liveUiExecuted=$false;liveGameExecuted=$false}
    if($Output){$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $Output -Encoding utf8}
    $result|ConvertTo-Json -Depth 5
}finally{$assembly.Dispose()}
