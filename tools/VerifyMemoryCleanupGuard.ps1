param(
 [string]$Plugin = 'D:\万有引力号\ArenaBoxBot\bin\Release\net48\ArenaBoxBot.dll',
 [string]$Native = 'D:\Hearthstone\BepInEx\plugins\HsMod.dll',
 [switch]$ExpectLegacyObserver
)
$ErrorActionPreference='Stop'
[Reflection.Assembly]::Load([IO.File]::ReadAllBytes('D:\Hearthstone\BepInEx\core\Mono.Cecil.dll'))|Out-Null
$nativeAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Native)
$pluginAssembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($Plugin)
try {
 $nativeType=$nativeAssembly.MainModule.GetType('HsMod.Plugin')
 $tick=@($nativeType.Methods|Where-Object Name -eq MemoryCleanupTick)
 if($tick.Count -ne 1 -or $tick[0].ReturnType.FullName -ne 'System.Void' -or $tick[0].Parameters.Count -ne 0){throw 'Native cleanup signature changed'}
 $timer=$nativeType.Fields|Where-Object Name -eq s_nextMemoryCleanup
 if(!$timer -or !$timer.IsStatic -or $timer.FieldType.FullName -ne 'System.Single'){throw 'Native cleanup timer contract changed'}
 $instructions=$tick[0].Body.Instructions
 if(@($instructions|Where-Object { $_.OpCode.Name -eq 'ldc.r4' -and $_.Operand -eq 300 }).Count -ne 1 -or
    @($instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -eq 'System.Void Hearthstone.HearthstoneApplication::UnloadUnusedAssets()' }).Count -ne 1){throw 'Native cadence or eviction changed'}
 $guard=$pluginAssembly.MainModule.GetType('ArenaBoxBot.NativeMemoryCleanupGuard')
 if($ExpectLegacyObserver){
  if($guard){throw 'Legacy negative control unexpectedly has the guard'}
  Write-Output 'Legacy negative control rejected: native resource cleanup has no plugin boundary gate.'
  return
 }
 if(!$guard){throw 'Missing cleanup boundary guard'}
 $before=$guard.Methods|Where-Object Name -eq BeforeCleanup
 if($before.ReturnType.FullName -ne 'System.Boolean' -or $before.Parameters.Count -ne 1 -or
    $before.Parameters[0].ParameterType.FullName -ne 'System.Int64&'){throw 'Prefix must only return a boundary decision and its own timing state'}
 $install=$guard.Methods|Where-Object Name -eq EnsureInstalled
 $guardTypes=@($guard)+@($guard.NestedTypes)
 $registrationStrings=@(foreach($type in $guardTypes){foreach($method in $type.Methods|Where-Object HasBody){$method.Body.Instructions|Where-Object { $_.OpCode.Name -eq 'ldstr' -and $_.Operand -eq 'MemoryCleanupTick' }}})
 if($registrationStrings.Count -ne 1 -or !($install.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'HarmonyLib.Harmony' -and $_.Operand.Name -eq 'Patch' })){throw 'Guard registration does not target native cleanup'}
 foreach($method in $guard.Methods|Where-Object HasBody){
  foreach($instruction in $method.Body.Instructions){
   $operand=$instruction.Operand
   if($operand -is [Mono.Cecil.FieldReference] -and $instruction.OpCode.Name -in 'stfld','stsfld' -and
      $operand.DeclaringType.FullName -ne $guard.FullName -and !$operand.DeclaringType.FullName.StartsWith($guard.FullName+'/')){throw "External field mutation: $instruction"}
   if($operand -is [Mono.Cecil.MethodReference] -and $operand.Name -match '^SetValue$|^SetRuntimeEnabled$|^UnloadUnusedAssets$|^Collect$|^Quit$|^SendOption$|^FindGame$|^Kill$'){throw "Guard may not force cleanup, change script state or send actions: $instruction"}
  }
 }
 $calls=@($before.Body.Instructions|Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] })
 foreach($required in 'IsRuntimeEnabled','GetMode','IsTransitioning','IsFindingGame','Observe'){
  if(!($calls|Where-Object {$_.Operand.Name -eq $required})){throw "Missing cleanup boundary evidence: $required"}
 }
 $diagnostic=$pluginAssembly.MainModule.GetType('ArenaBoxBot.GameFrameTimingDiagnostics')
 if(!$diagnostic){throw 'Missing passive frame timing diagnostic'}
 foreach($method in $diagnostic.Methods|Where-Object HasBody){foreach($instruction in $method.Body.Instructions){
  $operand=$instruction.Operand
  if($operand -is [Mono.Cecil.MethodReference] -and ($operand.Name -match 'FindObjects|Capture|ReadAll|UnloadUnusedAssets|^Collect$|SendOption|SetRuntimeEnabled|^Kill$' -or $operand.DeclaringType.FullName -match 'UnityEngine.GUI|UnityEngine.Input')){throw "Frame observer contains costly or mutating call: $instruction"}
 }}
 Write-Output 'Native cleanup cadence retained; gameplay/draft/transition/queue gates, no external mutation/actions, and passive frame sampling contract passed.'
} finally { $nativeAssembly.Dispose();$pluginAssembly.Dispose() }
