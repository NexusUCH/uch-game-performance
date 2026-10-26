using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.4.0: Placeable.Place logs "Placing <name>" for every block. A level start places every block of the level, so
    // a full level writes thousands of these lines (Unity log + every log listener) in the freeze frame. Only this one
    // message is removed, together with its string building; all other logging stays.
    internal static class PlacementLogHooks
    {
        internal static readonly Feature Quiet = new Feature("CPU/QuietPlacementLog");

        internal static void Install()
        {
            Quiet.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Place", new Type[] { typeof(int), typeof(bool), typeof(bool) }), typeof(PlacementLogHooks), null, null, "RemoveLog", null);
        }

        internal static IEnumerable<CodeInstruction> RemoveLog(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            for (int i = 4; i < code.Count; i++)
            {
                if (!Guards.Calls(code[i], typeof(Debug), "Log")) continue;
                int begin = i - 4;
                if (code[begin].opcode != OpCodes.Ldstr || !Equals(code[begin].operand, "Placing ") || code[begin + 1].opcode != OpCodes.Ldarg_0 ||
                    !Guards.Calls(code[begin + 2], typeof(Placeable), "get_UsefulName") || !Guards.Calls(code[begin + 3], typeof(string), "Concat"))
                    throw new InvalidOperationException("Unexpected placement log expression");
                MethodInfo log = (MethodInfo)code[i].operand;
                if (log.GetParameters().Length != 1 || log.GetParameters()[0].ParameterType != typeof(object)) throw new InvalidOperationException("Unexpected log overload");
                for (int j = begin; j <= i; j++) { code[j].opcode = OpCodes.Nop; code[j].operand = null; }
                count++;
            }
            Guards.Count(count, 1, "placement log");
            return code;
        }
    }
}
