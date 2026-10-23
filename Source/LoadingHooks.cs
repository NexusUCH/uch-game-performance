using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    internal static class LoadingHooks
    {
        [ThreadStatic] private static int passDepth;
        [ThreadStatic] private static Dictionary<string, string[]> paths;
        private static Func<List<Placeable>, int> listVersion;
        private sealed class Entry
        {
            public Entry() { }
            internal readonly MembershipMirror<Placeable> Mirror = new MembershipMirror<Placeable>();
        }
        private static ConditionalWeakTable<List<Placeable>, Entry> placed = new ConditionalWeakTable<List<Placeable>, Entry>();

        internal static void Clear()
        {
            passDepth = 0;
            if (paths != null) paths.Clear();
            placed = new ConditionalWeakTable<List<Placeable>, Entry>();
        }

        internal static void InstallPaths()
        {
            foreach (MethodBase method in new MethodBase[] {
                Reflect.DeclaredMethod(typeof(QuickSaver), "RestoreSaveables"),
                Reflect.DeclaredMethod(typeof(QuickSaver), "LoadSnapshotFromXmlDocument"),
                Reflect.DeclaredMethod(typeof(QuickSaver), "ClearInitialDestroyedObjects"),
                Reflect.DeclaredMethod(typeof(GameControl), "RpcPropagateBlockIDs") })
                Plugin.Paths.Patch(method, typeof(LoadingHooks), "EnterPass", null, null, "ExitPass");
            Plugin.Paths.Patch(Reflect.DeclaredMethod(typeof(QuickSaver), "GetTransformFromHierarchyPath"), typeof(LoadingHooks), null, null, "PathSplit", null);
        }

        private static void EnterPass(out bool __state)
        {
            __state = false;
            if (!Plugin.Paths.Active) return;
            try
            {
                if (paths == null) paths = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (passDepth == 0) paths.Clear();
                passDepth++; __state = true;
            }
            catch (Exception e) { Plugin.Paths.Fail(e); }
        }

        private static Exception ExitPass(bool __state, Exception __exception)
        {
            if (__state)
            {
                try { if (--passDepth <= 0) { passDepth = 0; paths.Clear(); } }
                catch (Exception e) { Plugin.Paths.Fail(e); }
            }
            return __exception;
        }

        internal static IEnumerable<CodeInstruction> PathSplit(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            foreach (CodeInstruction instruction in code)
                if (Guards.Calls(instruction, typeof(string), "Split"))
                {
                    MethodInfo method = (MethodInfo)instruction.operand;
                    Type[] signature = Array.ConvertAll(method.GetParameters(), delegate(ParameterInfo parameter) { return parameter.ParameterType; });
                    if (signature.Length != 2 || signature[0] != typeof(char) || signature[1] != typeof(StringSplitOptions)) throw new InvalidOperationException("Unexpected path Split signature: " + method);
                    instruction.opcode = OpCodes.Call; instruction.operand = Reflect.Method(typeof(LoadingHooks), "SplitPath"); count++;
                }
            Guards.Count(count, 1, "hierarchy path split");
            return code;
        }

        private static string[] SplitPath(string path, char separator, StringSplitOptions options)
        {
            if (!Plugin.Paths.Active || passDepth == 0) return path.Split(separator, options);
            try
            {
                string[] result;
                if (!paths.TryGetValue(path, out result))
                {
                    result = path.Split(separator, options);
                    // Bound pass-local retained storage on arbitrarily large custom levels.
                    if (paths.Count < 8192) paths.Add(path, result);
                }
                return result;
            }
            catch (Exception e) { Plugin.Paths.Fail(e); return path.Split(separator, options); }
        }

        internal static void InstallPlacedSet()
        {
            FieldInfo version = typeof(List<Placeable>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic);
            if (version == null || version.FieldType != typeof(int)) throw new NotSupportedException("List version field is unavailable");
            DynamicMethod getter = new DynamicMethod("PlacedListVersion", typeof(int), new Type[] { typeof(List<Placeable>) }, typeof(LoadingHooks), true);
            ILGenerator il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, version); il.Emit(OpCodes.Ret);
            listVersion = (Func<List<Placeable>, int>)getter.CreateDelegate(typeof(Func<List<Placeable>, int>));
            Plugin.PlacedSet.Patch(Reflect.DeclaredMethod(typeof(GameControl), "handleEvent"), typeof(LoadingHooks), null, null, "PlacedMembership", null);
        }

        internal static IEnumerable<CodeInstruction> PlacedMembership(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            FieldInfo field = Reflect.Field(typeof(GameControl), "placedBlocks");
            int contains = 0, adds = 0;
            for (int i = 2; i < code.Count; i++)
            {
                if (Guards.Calls(code[i], typeof(List<Placeable>), "Contains") && code[i - 2].opcode == OpCodes.Ldfld && Equals(code[i - 2].operand, field))
                {
                    code[i].opcode = OpCodes.Call; code[i].operand = Reflect.Method(typeof(LoadingHooks), "ContainsPlaced"); contains++;
                }
                else if (Guards.Calls(code[i], typeof(List<Placeable>), "Add"))
                {
                    code[i].opcode = OpCodes.Call; code[i].operand = Reflect.Method(typeof(LoadingHooks), "AddObserved"); adds++;
                }
            }
            Guards.Count(contains, 1, "placedBlocks.Contains only");
            Guards.Count(adds, 4, "placeable list Add notifications");
            return code;
        }

        private static bool ContainsPlaced(List<Placeable> list, Placeable value)
        {
            if (!Plugin.PlacedSet.Active) return list.Contains(value);
            try { return placed.GetOrCreateValue(list).Mirror.Contains(list, listVersion(list), value); }
            catch (Exception e) { Plugin.PlacedSet.Fail(e); return list.Contains(value); }
        }

        private static void AddObserved(List<Placeable> list, Placeable value)
        {
            int before = 0;
            Entry entry = null;
            if (Plugin.PlacedSet.Active)
            {
                try { if (placed.TryGetValue(list, out entry)) before = listVersion(list); }
                catch (Exception e) { Plugin.PlacedSet.Fail(e); }
            }
            list.Add(value); // Exactly one authoritative write; do not catch or repeat vanilla failures.
            if (!Plugin.PlacedSet.Active || entry == null) return;
            try { entry.Mirror.Added(list, before, listVersion(list), value); }
            catch (Exception e) { Plugin.PlacedSet.Fail(e); }
        }
    }
}
