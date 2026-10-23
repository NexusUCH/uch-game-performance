using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    internal static class CacheHooks
    {
        private sealed class ColliderEntry
        {
            public ColliderEntry() { }
            internal readonly List<Collider2D> Live = new List<Collider2D>();
            internal readonly ArraySnapshot<Collider2D> Snapshot = new ArraySnapshot<Collider2D>();
        }
        private sealed class WaterEntry
        {
            public WaterEntry() { }
            internal WaterSplash[] Update = new WaterSplash[0], Create = new WaterSplash[0];
        }
        private static ConditionalWeakTable<Component, ColliderEntry> self = new ConditionalWeakTable<Component, ColliderEntry>();
        private static ConditionalWeakTable<GameObject, ColliderEntry> children = new ConditionalWeakTable<GameObject, ColliderEntry>();
        private static ConditionalWeakTable<WaterPool, WaterEntry> water = new ConditionalWeakTable<WaterPool, WaterEntry>();

        internal static void Clear()
        {
            self = new ConditionalWeakTable<Component, ColliderEntry>();
            children = new ConditionalWeakTable<GameObject, ColliderEntry>();
            water = new ConditionalWeakTable<WaterPool, WaterEntry>();
        }

        internal static void InstallColliderArrays()
        {
            foreach (string name in new string[] { "Enable", "Disable" })
                Plugin.ColliderArrays.Patch(Reflect.DeclaredMethod(typeof(ColliderModeControl), name), typeof(CacheHooks), null, null, "ColliderArrays", null);
        }

        internal static IEnumerable<CodeInstruction> ColliderArrays(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (!Guards.Calls(instruction, typeof(Component), "GetComponents") || !method.IsGenericMethod || method.GetGenericArguments()[0] != typeof(Collider2D) || method.GetParameters().Length != 0) continue;
                instruction.opcode = OpCodes.Call; instruction.operand = Reflect.Method(typeof(CacheHooks), "SelfColliders"); count++;
            }
            Guards.Count(count, 1, "collider array query");
            return code;
        }

        private static Collider2D[] SelfColliders(Component owner)
        {
            if (!Plugin.ColliderArrays.Active) return owner.GetComponents<Collider2D>();
            try
            {
                ColliderEntry entry = self.GetOrCreateValue(owner);
                entry.Live.Clear();
                // A live, allocation-free membership check is stronger than a hierarchy-change callback:
                // Unity has no managed event for adding/removing a component on the same GameObject.
                owner.GetComponents<Collider2D>(entry.Live);
                return entry.Snapshot.Read(entry.Live);
            }
            catch (Exception e) { Plugin.ColliderArrays.Fail(e); return owner.GetComponents<Collider2D>(); }
        }

        internal static Collider2D[] ChildColliders(Component owner) { return ChildColliders(owner.gameObject); }
        internal static Collider2D[] ChildColliders(GameObject owner)
        {
            if (!Plugin.ShotColliders.Active) return owner.GetComponentsInChildren<Collider2D>();
            try
            {
                ColliderEntry entry = children.GetOrCreateValue(owner);
                entry.Live.Clear();
                owner.GetComponentsInChildren<Collider2D>(false, entry.Live);
                return entry.Snapshot.Read(entry.Live);
            }
            catch (Exception e) { Plugin.ShotColliders.Fail(e); return owner.GetComponentsInChildren<Collider2D>(); }
        }

        internal static void InstallWater()
        {
            Plugin.Water.Patch(Reflect.DeclaredMethod(typeof(WaterPool), "Update"), typeof(CacheHooks), null, null, "WaterArrays", null);
            Plugin.Water.Patch(Reflect.DeclaredMethod(typeof(WaterPool), "createSplash", new Type[] { typeof(GameObject), typeof(Vector3) }), typeof(CacheHooks), null, null, "WaterArrays", null);
        }

        internal static IEnumerable<CodeInstruction> WaterArrays(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            for (int i = 0; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Newarr && Equals(code[i].operand, typeof(WaterSplash)))
                {
                    CodeInstruction owner = new CodeInstruction(OpCodes.Ldarg_0);
                    owner.labels.AddRange(code[i].labels); code[i].labels.Clear();
                    owner.blocks.AddRange(code[i].blocks); code[i].blocks.Clear();
                    code[i].opcode = OpCodes.Call;
                    code[i].operand = Reflect.Method(typeof(CacheHooks), __originalMethod.Name == "Update" ? "WaterUpdateArray" : "WaterCreateArray");
                    code.Insert(i++, owner); count++;
                }
            Guards.Count(count, 1, "water snapshot allocation");
            return code;
        }

        private static WaterSplash[] WaterUpdateArray(int size, WaterPool owner) { return WaterArray(size, owner, true); }
        private static WaterSplash[] WaterCreateArray(int size, WaterPool owner) { return WaterArray(size, owner, false); }
        private static WaterSplash[] WaterArray(int size, WaterPool owner, bool update)
        {
            if (!Plugin.Water.Active) return new WaterSplash[size];
            try
            {
                WaterEntry entry = water.GetOrCreateValue(owner);
                WaterSplash[] array = update ? entry.Update : entry.Create;
                if (array.Length != size)
                {
                    array = new WaterSplash[size];
                    if (update) entry.Update = array; else entry.Create = array;
                }
                return array;
            }
            catch (Exception e) { Plugin.Water.Fail(e); return new WaterSplash[size]; }
        }
    }
}
