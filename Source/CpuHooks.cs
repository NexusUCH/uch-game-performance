using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    internal static class CpuHooks
    {
        internal static readonly WaitForFixedUpdate FixedWait = new WaitForFixedUpdate();
        private static readonly PhysicsModifier[] EmptyModifiers = new PhysicsModifier[0];
        private static readonly Dictionary<Type, bool> EmptyTypes = new Dictionary<Type, bool>();
        private static readonly MethodInfo BaseAct = Reflect.DeclaredMethod(typeof(ActiveBlock), "Act");
        private static readonly List<WeakReference> IdleOwned = new List<WeakReference>();
        private static ConditionalWeakTable<CheckColliding, IdleEntry> idleEntries = new ConditionalWeakTable<CheckColliding, IdleEntry>();
        private static ConditionalWeakTable<MultipieceBlock, MultiEntry> multiEntries = new ConditionalWeakTable<MultipieceBlock, MultiEntry>();
        private static Func<CheckColliding, bool> isOn;
        private static long multiEpoch;
        private sealed class IdleEntry { public IdleEntry() { } internal bool Owned; }
        private sealed class MultiEntry
        {
            public MultiEntry() { }
            internal bool Initialized, Separable;
            internal long Epoch;
            internal MultipiecePart[] Parts;
            internal object Hovered;
        }

        internal static void InstallWait()
        {
            MethodInfo move = null;
            foreach (Type nested in typeof(ActiveBlock).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (nested.Name.StartsWith("<actOnFixed>", StringComparison.Ordinal))
                {
                    if (move != null) throw new InvalidOperationException("Ambiguous fixed iterator");
                    move = Reflect.DeclaredMethod(nested, "MoveNext");
                }
            Plugin.Wait.Patch(move, typeof(CpuHooks), null, null, "ReuseFixedWait", null);
        }

        internal static IEnumerable<CodeInstruction> ReuseFixedWait(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                ConstructorInfo ctor = instruction.operand as ConstructorInfo;
                if (instruction.opcode != OpCodes.Newobj || ctor == null || ctor.DeclaringType != typeof(WaitForFixedUpdate) || ctor.GetParameters().Length != 0) continue;
                count++;
                instruction.opcode = OpCodes.Ldsfld;
                instruction.operand = Reflect.Field(typeof(CpuHooks), "FixedWait");
            }
            Guards.Count(count, 1, "fixed wait");
            return code;
        }

        internal static void InstallEmptyLoops()
        {
            Guards.Method(BaseAct);
            if (BaseAct.GetMethodBody().GetILAsByteArray().Length != 1 || BaseAct.GetMethodBody().GetILAsByteArray()[0] != 0x2a)
                throw new InvalidOperationException("Base Act is not exactly ret");
            Plugin.EmptyLoops.Patch(Reflect.DeclaredMethod(typeof(ActiveBlock), "OnEnable"), typeof(CpuHooks), "BeforeBlockEnable", null, null, null);
        }

        private static bool BeforeBlockEnable(ActiveBlock __instance)
        {
            if (!Plugin.EmptyLoops.Active) return true;
            try
            {
                if (__instance.ActUpdateMode == ActiveBlock.ActMode.NEVER) return true;
                Type type = __instance.GetType();
                bool empty;
                if (!EmptyTypes.TryGetValue(type, out empty))
                {
                    empty = BlockLoopPolicy.InheritsEmptyAct(type, typeof(ActiveBlock), BaseAct);
                    EmptyTypes.Add(type, empty);
                }
                return !empty;
            }
            catch (Exception e) { Plugin.EmptyLoops.Fail(e); return true; }
        }

        internal static void InstallPhysMods()
        {
            Plugin.PhysMods.Patch(Reflect.DeclaredMethod(typeof(Placeable), "GetPhysicsModifier"), typeof(CpuHooks), null, null, "EmptyArrays", null);
            Plugin.PhysMods.Patch(Reflect.DeclaredMethod(typeof(Placeable), "GetPhysicsModifiers"), typeof(CpuHooks), null, null, "EmptyArrays", null);
        }

        internal static IEnumerable<CodeInstruction> EmptyArrays(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            for (int i = 1; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Newarr && Equals(code[i].operand, typeof(PhysicsModifier)) && code[i - 1].opcode == OpCodes.Ldc_I4_0)
                {
                    code[i - 1].opcode = OpCodes.Nop; code[i - 1].operand = null;
                    code[i].opcode = OpCodes.Ldsfld; code[i].operand = Reflect.Field(typeof(CpuHooks), "EmptyModifiers");
                    count++;
                }
            Guards.Count(count, __originalMethod.Name == "GetPhysicsModifier" ? 1 : 2, "base empty modifier arrays");
            return code;
        }

        internal static void InstallIdle()
        {
            FieldInfo on = Reflect.Field(typeof(CheckColliding), "On");
            if (on == null || on.FieldType != typeof(bool)) throw new InvalidOperationException("CheckColliding.On changed");
            DynamicMethod getter = new DynamicMethod("CheckCollidingOn", typeof(bool), new Type[] { typeof(CheckColliding) }, typeof(CpuHooks), true);
            ILGenerator il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, on); il.Emit(OpCodes.Ret);
            isOn = (Func<CheckColliding, bool>)getter.CreateDelegate(typeof(Func<CheckColliding, bool>));
            Plugin.Idle.Restore = RestoreIdle;
            Plugin.Idle.Patch(Reflect.DeclaredMethod(typeof(CheckColliding), "SwitchToMode"), typeof(CpuHooks), null, "AfterCheckMode", null, null);
            Plugin.Idle.Patch(Reflect.DeclaredMethod(typeof(CheckColliding), "handleEvent"), typeof(CpuHooks), null, "AfterCheckMode", null, null);
        }

        private static void AfterCheckMode(CheckColliding __instance)
        {
            if (!Plugin.Idle.Active) return;
            try
            {
                IdleEntry entry = idleEntries.GetOrCreateValue(__instance);
                bool on = isOn(__instance);
                if (!on && __instance.enabled)
                {
                    if (!entry.Owned) { entry.Owned = true; IdleOwned.Add(new WeakReference(__instance)); }
                    // Unity calls the game's original OnDisable, which clears collision state.
                    __instance.enabled = false;
                }
                else if (on && entry.Owned) __instance.enabled = true;
            }
            catch (Exception e) { Plugin.Idle.Fail(e); }
        }

        internal static void RestoreIdle()
        {
            for (int i = 0; i < IdleOwned.Count; i++)
            {
                CheckColliding check = IdleOwned[i].Target as CheckColliding;
                if (check != null && !check.enabled) check.enabled = true;
            }
            IdleOwned.Clear();
            idleEntries = new ConditionalWeakTable<CheckColliding, IdleEntry>();
        }

        internal static void PruneIdle()
        {
            // Scene-bound maintenance, never a recurring scene scan.
            for (int i = IdleOwned.Count - 1; i >= 0; i--)
                if (IdleOwned[i].Target as CheckColliding == null) IdleOwned.RemoveAt(i);
        }

        internal static void InstallMagnet()
        {
            Plugin.Magnet.Patch(Reflect.DeclaredMethod(typeof(AttractorRepulsor), "Update"), typeof(CpuHooks), "BeforeMagnet", null, null, null);
        }

        private static bool BeforeMagnet(AttractorRepulsor __instance)
        {
            if (!Plugin.Magnet.Active) return true;
            try
            {
                CircleCollider2D collider = __instance.GetComponent<CircleCollider2D>();
                if (collider.radius != __instance.radius) collider.radius = __instance.radius;
                return false;
            }
            catch (Exception e) { Plugin.Magnet.Fail(e); return true; }
        }

        internal static void InstallMultipiece()
        {
            Plugin.Multipiece.Patch(Reflect.DeclaredMethod(typeof(MultipieceBlock), "Update"), typeof(CpuHooks), "BeforeMulti", "AfterMulti", null, null);
            foreach (MethodBase method in new MethodBase[] {
                Reflect.DeclaredMethod(typeof(Placeable), "set_MarkedForDestruction"),
                Reflect.DeclaredMethod(typeof(MultipieceBlock), "RemovePart"),
                Reflect.DeclaredMethod(typeof(MultipiecePart), "OnDestroy") })
                Plugin.Multipiece.Patch(method, typeof(CpuHooks), "DirtyMulti", null, null, null);
        }

        private static void DirtyMulti() { unchecked { multiEpoch++; } }

        private static bool BeforeMulti(MultipieceBlock __instance, out bool __state)
        {
            __state = false;
            if (!Plugin.Multipiece.Active) return true;
            try
            {
                MultiEntry entry = multiEntries.GetOrCreateValue(__instance);
                __state = !entry.Initialized || entry.Epoch != multiEpoch || !ReferenceEquals(entry.Parts, __instance.Parts)
                    || entry.Separable != __instance.Separable || !ReferenceEquals(entry.Hovered, __instance.HoveredCursors);
                return __state;
            }
            catch (Exception e) { Plugin.Multipiece.Fail(e); return true; }
        }

        private static void AfterMulti(MultipieceBlock __instance, bool __state)
        {
            if (!Plugin.Multipiece.Active || !__state) return;
            try
            {
                MultiEntry entry = multiEntries.GetOrCreateValue(__instance);
                entry.Initialized = true; entry.Epoch = multiEpoch; entry.Parts = __instance.Parts;
                entry.Separable = __instance.Separable; entry.Hovered = __instance.HoveredCursors;
            }
            catch (Exception e) { Plugin.Multipiece.Fail(e); }
        }

        internal static void InstallLogs()
        {
            foreach (MethodBase method in new MethodBase[] {
                Reflect.DeclaredMethod(typeof(Placeable), "FixedUpdate"),
                Reflect.DeclaredMethod(typeof(ObjPool), "GetObjFromPool"),
                Reflect.DeclaredMethod(typeof(ObjPool), "AddObjToPool") })
                Plugin.Logs.Patch(method, typeof(CpuHooks), null, null, "QuietLogs", null);
        }

        internal static IEnumerable<CodeInstruction> QuietLogs(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            string expected = __originalMethod.DeclaringType == typeof(Placeable) ? "Log" : (__originalMethod.Name == "GetObjFromPool" ? "LogError" : "LogWarning");
            for (int i = 0; i < code.Count; i++)
                if (Guards.Calls(code[i], typeof(Debug), expected))
                {
                    MethodInfo method = (MethodInfo)code[i].operand;
                    if (method.GetParameters().Length != 1 || method.GetParameters()[0].ParameterType != typeof(object)) throw new InvalidOperationException("Unexpected log overload");
                    int begin = i - 1;
                    if (__originalMethod.DeclaringType == typeof(Placeable))
                    {
                        begin = i - 5;
                        if (begin < 0 || code[begin].opcode != OpCodes.Ldarg_0 || !Guards.Calls(code[begin + 1], typeof(UnityEngine.Object), "get_name") ||
                            !Guards.Calls(code[begin + 2], typeof(object), "ToString") || code[begin + 3].opcode != OpCodes.Ldstr ||
                            !Equals(code[begin + 3].operand, " is getting it's relativeAttachPositionReset during Fixed Update.  Why?") ||
                            !Guards.Calls(code[begin + 4], typeof(string), "Concat")) throw new InvalidOperationException("Unexpected placement log expression");
                    }
                    else
                    {
                        string message = __originalMethod.Name == "GetObjFromPool" ? "Pool Queue overrun! Doubling pool size..." : "Pool already contained this object";
                        if (begin < 0 || code[begin].opcode != OpCodes.Ldstr || !Equals(code[begin].operand, message)) throw new InvalidOperationException("Unexpected pool log message");
                    }
                    for (int j = begin; j <= i; j++) { code[j].opcode = OpCodes.Nop; code[j].operand = null; }
                    count++;
                }
            Guards.Count(count, 1, "known hot-path log");
            return code;
        }
    }
}
