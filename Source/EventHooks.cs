using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using GameEvent;
using HarmonyLib;

namespace UCHGamePerformance
{
    internal static class EventHooks
    {
        [ThreadStatic] private static NestedScratch<int> scratch;

        internal static void InstallScratch()
        {
            Plugin.Events.Patch(Reflect.DeclaredMethod(typeof(GameEventManager), "SendEvent"), typeof(EventHooks), "BeginSend", null, "ScratchAllocation", "EndSend");
        }

        private static void BeginSend(out int __state)
        {
            __state = -1;
            if (!Plugin.Events.Active) return;
            try
            {
                if (scratch == null) scratch = new NestedScratch<int>();
                __state = scratch.Enter();
            }
            catch (Exception e) { Plugin.Events.Fail(e); }
        }

        private static Exception EndSend(int __state, Exception __exception)
        {
            if (__state >= 0)
            {
                try { scratch.Exit(__state); }
                catch (Exception e) { Plugin.Events.Fail(e); }
            }
            // Preserve exceptions from the original method, including listener-list mutation errors.
            return __exception;
        }

        internal static IEnumerable<CodeInstruction> ScratchAllocation(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                ConstructorInfo ctor = instruction.operand as ConstructorInfo;
                if (instruction.opcode == OpCodes.Newobj && ctor != null && ctor.DeclaringType == typeof(List<int>) && ctor.GetParameters().Length == 0)
                {
                    instruction.opcode = OpCodes.Call; instruction.operand = Reflect.Method(typeof(EventHooks), "CleanupList"); count++;
                }
            }
            Guards.Count(count, 1, "event cleanup list allocation");
            return code;
        }

        private static List<int> CleanupList()
        {
            if (!Plugin.Events.Active) return new List<int>();
            try { return scratch.Current(); }
            catch (Exception e) { Plugin.Events.Fail(e); return new List<int>(); }
        }

        internal static void InstallInactive()
        {
            Guards.Method(Reflect.DeclaredMethod(typeof(Projectile), "Reset"));
            Plugin.Inactive.Patch(Reflect.DeclaredMethod(typeof(Projectile), "handleEvent"), typeof(EventHooks), "BeforeProjectileEvent", null, null, null);
        }

        private static bool BeforeProjectileEvent(Projectile __instance, global::GameEvent.GameEvent e)
        {
            if (!Plugin.Inactive.Active) return true;
            try
            {
                // Idle projectiles must still track Pause/Scoreboard flags. Their Reset sets number=-1,
                // so an inactive pooled instance cannot be an active ProjectileDestroyed recipient.
                return e.GetType() != typeof(NetworkMessageReceivedEvent) || __instance.gameObject.activeSelf || __instance.projectileNumber != -1;
            }
            catch (Exception error) { Plugin.Inactive.Fail(error); return true; }
        }
    }
}
