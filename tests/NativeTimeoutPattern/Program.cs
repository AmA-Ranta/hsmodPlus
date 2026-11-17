using System.Reflection;
using System.Reflection.Emit;
using ArenaBoxBot;
using HarmonyLib;
using Mono.Cecil;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
    .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Name!);
var checks = 0;
void Check(bool value, string why) { checks++; if (!value) throw new InvalidOperationException(why); }
foreach (var source in new[] { "1030更新/HsMod.dll" })
{
    using var assembly = AssemblyDefinition.ReadAssembly(Path.Combine(root, source));
    var method = assembly.MainModule.GetType("HsMod.WildLadderBot").Methods.Single(m => m.Name == "HandleGame" && m.Parameters.Count == 1);
    for (var repeat = 0; repeat < 3; repeat++)
    {
        var code = method.Body.Instructions.Select(i => new CodeInstruction(opcodes[i.OpCode.Name], i.Operand)).ToList();
        var matches = Enumerable.Range(0, code.Count).Where(i => NativeTimeoutPattern.IsActionTimeoutStore(code, i)).ToArray();
        Check(matches.Length == 1, "Unique timeout expression in actual native body: " + source);
        var index = matches.Single();
        var local = ((Mono.Cecil.Cil.VariableDefinition)method.Body.Instructions[index].Operand).Index;
        Check(local == 29, "Observed compiler-local shift covered");
        var previous = code[index - 3].operand;
        code[index - 3].operand = 11f;
        Check(!NativeTimeoutPattern.IsActionTimeoutStore(code, index), "Changed native threshold cannot be patched");
        code[index - 3].operand = previous;
        var branch = code[index - 2].opcode;
        code[index - 2].opcode = OpCodes.Call;
        Check(!NativeTimeoutPattern.IsActionTimeoutStore(code, index), "Unrelated branch/call expression rejected");
        code[index - 2].opcode = branch;
        Check(NativeTimeoutPattern.IsActionTimeoutStore(code, index), "Original expression restored");
        Check(!NativeTimeoutPattern.IsActionTimeoutStore(code, index + 5), "Elapsed-time local next to expression is not a target");
    }
}
Console.WriteLine($"PASS: {checks} actual 1030 native IL timeout-shape assertions; 3 repeats each; no local-slot dependency or live game");
