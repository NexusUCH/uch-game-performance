using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.4.0: every phase start (PLACE, PLAY, sudden death, END) and every free-play build/run switch calls
    // Modifiers.OnModifiersDynamicChange, which runs FindObjectsOfType<Character>() - a search through the whole scene
    // (~13 ms on a full level) - only to call RefreshScale on each character. The game already keeps every character in
    // Character.AllCharacters (added in Awake, removed in OnDestroy); the same set (characters on active objects, as
    // FindObjectsOfType returns) is taken from that list. RefreshScale does not depend on the order.
    internal static class RoundHooks
    {
        internal static readonly Feature Lookup = new Feature("CPU/PhaseCharacterLookup");
        private static readonly List<Character> scratch = new List<Character>(16);

        internal static void Install()
        {
            Lookup.Patch(Reflect.DeclaredMethod(typeof(Modifiers), "OnModifiersDynamicChange"), typeof(RoundHooks), null, null, "UseList", null);
        }

        internal static IEnumerable<CodeInstruction> UseList(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (instruction.opcode != OpCodes.Call || method == null || !method.IsGenericMethod || method.Name != "FindObjectsOfType" ||
                    method.DeclaringType != typeof(UnityEngine.Object) || method.GetGenericArguments()[0] != typeof(Character) || method.GetParameters().Length != 0) continue;
                instruction.operand = Reflect.DeclaredMethod(typeof(RoundHooks), "ActiveCharacters");
                count++;
            }
            Guards.Count(count, 1, "phase character search");
            return code;
        }

        private static Character[] ActiveCharacters()
        {
            if (!Lookup.Active) return UnityEngine.Object.FindObjectsOfType<Character>();
            try
            {
                List<Character> all = Character.AllCharacters;
                if (all == null) return UnityEngine.Object.FindObjectsOfType<Character>();
                scratch.Clear();
                for (int i = 0; i < all.Count; i++)
                {
                    Character c = all[i];
                    if (c != null && c.gameObject.activeInHierarchy) scratch.Add(c);
                }
                return scratch.ToArray();
            }
            catch (Exception e) { Lookup.Fail(e); return UnityEngine.Object.FindObjectsOfType<Character>(); }
        }
    }
}
