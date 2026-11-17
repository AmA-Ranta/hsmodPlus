param([string]$Plugin='D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',[switch]$ExpectLegacyObserver)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes('D:\Hearthstone\BepInEx\core\Mono.Cecil.dll'))|Out-Null
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
try {
 $bridge=$assembly.MainModule.GetType('ArenaBoxBot.HsModNativeArena')
 $getters=@($bridge.Methods|Where-Object {$_.Name -in 'IsNativeArenaModeSelected','SelectedModeText','ConfigRequestsArena'})
 $oldReads=@(foreach($method in $getters){$method.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and
    ($_.Operand.FullName -match 'System.IO.File::ReadAllText' -or $_.Operand.Name -eq 'ConfigRequestsArenaInFile') }})
 if($ExpectLegacyObserver){if(!$oldReads.Count){throw 'Legacy negative control unexpectedly has no direct config reads'};Write-Output "Legacy negative control rejected: $($oldReads.Count) direct file/parser calls in hot mode getters.";return}
 if($oldReads.Count){throw 'Hot mode getters still reopen config/bridge files'}
 $cache=$assembly.MainModule.GetType('ArenaBoxBot.TimeCachedBoolean')
 if(!$cache){throw 'Missing bounded mode observation cache'}
 foreach($method in $getters){if(!($method.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq $cache.FullName -and $_.Operand.Name -eq 'Read' })){throw "Getter is not using shared bounded observations: $($method.Name)"}}
 $setter=$bridge.Methods|Where-Object Name -eq SetModeBridge
 if(@($setter.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'InvalidateModeIntent' }).Count -lt 2){throw 'Manual mode setter must invalidate before and after writes'}
 $read=$cache.Methods|Where-Object Name -eq Read
 if(!($read.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'System.Threading.Monitor' -and $_.Operand.Name -eq 'Enter' })){throw 'Concurrent native/API callers need single-flight refresh'}
 foreach($method in $cache.Methods|Where-Object HasBody){foreach($instruction in $method.Body.Instructions){$operand=$instruction.Operand
  if($operand -is [Mono.Cecil.FieldReference] -and $instruction.OpCode.Name -in 'stfld','stsfld' -and $operand.DeclaringType.FullName -ne $cache.FullName -and !$operand.DeclaringType.FullName.StartsWith($cache.FullName+'/')){throw "External cache field mutation: $instruction"}
  if($operand -is [Mono.Cecil.MethodReference] -and $operand.Name -match '^Sleep$|^Delay$|^SetValue$|^SetRuntimeEnabled$|^SendOption$|^Quit$|^Collect$|FindObjects|Capture'){throw "Cache contains blocking or native action: $instruction"}
 }}
 Write-Output 'PASS: hot mode getters share bounded observations; manual mode writes invalidate immediately; concurrent refresh is single-flight; no native action or screen capture.'
} finally {$assembly.Dispose()}
