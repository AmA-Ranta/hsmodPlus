using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace ArenaBoxBot;

internal static class NativeTimeoutPattern
{
    internal static bool IsActionTimeoutStore(IList<CodeInstruction> code, int i)
    {
        if (i < 5 || i >= code.Count) return false;
        bool Constant(int offset, float value) => code[i - offset].opcode == OpCodes.Ldc_R4 &&
            code[i - offset].operand is float actual && actual == value;
        var op = code[i].opcode;
        return (op == OpCodes.Stloc || op == OpCodes.Stloc_S) &&
            Constant(5, 8f) && Constant(3, 12f) && Constant(1, 4f) &&
            (code[i - 4].opcode == OpCodes.Br || code[i - 4].opcode == OpCodes.Br_S) &&
            (code[i - 2].opcode == OpCodes.Br || code[i - 2].opcode == OpCodes.Br_S);
    }
}
