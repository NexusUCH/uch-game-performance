using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    internal static class ExperimentalHooks
    {
        internal static void InstallSmooth()
        {
            Plugin.Smooth.Patch(Reflect.Constructor(typeof(Smooth.NetworkState), new Type[] { typeof(Smooth.SmoothSync) }), typeof(ExperimentalHooks), null, null, "SmoothInitializer", null);
        }

        internal static IEnumerable<CodeInstruction> SmoothInitializer(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            for (int i = 1; i < code.Count - 1; i++)
            {
                ConstructorInfo ctor = code[i].operand as ConstructorInfo;
                if (code[i].opcode != OpCodes.Newobj || ctor == null || ctor.DeclaringType != typeof(Smooth.State) || ctor.GetParameters().Length != 0) continue;
                if (code[i - 1].opcode != OpCodes.Ldarg_0 || code[i + 1].opcode != OpCodes.Stfld || !Equals(code[i + 1].operand, Reflect.Field(typeof(Smooth.NetworkState), "state")))
                    throw new InvalidOperationException("Unexpected overwritten-state initializer");
                for (int j = i - 1; j <= i + 1; j++) { code[j].opcode = OpCodes.Nop; code[j].operand = null; }
                count++;
            }
            Guards.Count(count, 1, "overwritten State initializer");
            return code;
        }

        internal static void InstallSlowMo()
        {
            Plugin.SlowMo.Patch(Reflect.DeclaredMethod(typeof(SlowMoImageEffect), "OnRenderImage"), typeof(ExperimentalHooks), "BeforeRender", null, null, null);
        }

        private static bool BeforeRender(RenderTexture source, RenderTexture destination, float ___currFisheye, float ___currSat)
        {
            if (!Plugin.SlowMo.Active) return true;
            try
            {
                bool skip = Plugin.ScreenMode == Plugin.ScreenEffectMode.Remove ||
                    (Plugin.ScreenMode == Plugin.ScreenEffectMode.SkipWhenIdle && !Timekeeper.Slowing && ___currFisheye == 0f && ___currSat == 1f);
                if (!skip) return true;
                Graphics.Blit(source, destination);
                return false;
            }
            catch (Exception e) { Plugin.SlowMo.Fail(e); return true; }
        }
    }
}
