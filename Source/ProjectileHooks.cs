using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace UCHGamePerformance
{
    internal static class ProjectileHooks
    {
        private sealed class Entry
        {
            public Entry() { }
            internal Rigidbody2D Body;
            internal Modifiers Settings;
        }
        private static readonly ConditionalWeakTable<Projectile, Entry> Entries = new ConditionalWeakTable<Projectile, Entry>();

        internal static void InstallComponents()
        {
            // Only projectile-owned GetComponent calls are replaced. Character-body queries stay live.
            foreach (string name in new string[] { "Launch", "FixedUpdate", "Pause", "Unpause" })
                Plugin.ProjectileComponents.Patch(Reflect.DeclaredMethod(typeof(Projectile), name), typeof(ProjectileHooks), null, null, "Components", null);
        }

        internal static IEnumerable<CodeInstruction> Components(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int bodies = 0, modifiers = 0;
            for (int i = 0; i < code.Count; i++)
            {
                MethodInfo method = code[i].operand as MethodInfo;
                if (i > 0 && Guards.Calls(code[i], typeof(Component), "GetComponent") && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Rigidbody2D) && code[i - 1].opcode == OpCodes.Ldarg_0)
                {
                    code[i].opcode = OpCodes.Call; code[i].operand = Reflect.Method(typeof(ProjectileHooks), "Body"); bodies++;
                }
                else if (Guards.Calls(code[i], typeof(Modifiers), "GetInstance"))
                {
                    CodeInstruction owner = new CodeInstruction(OpCodes.Ldarg_0);
                    owner.labels.AddRange(code[i].labels); code[i].labels.Clear();
                    owner.blocks.AddRange(code[i].blocks); code[i].blocks.Clear();
                    code[i].opcode = OpCodes.Call; code[i].operand = Reflect.Method(typeof(ProjectileHooks), "Settings");
                    code.Insert(i++, owner); modifiers++;
                }
            }
            Guards.Count(bodies, __originalMethod.Name == "Pause" ? 3 : (__originalMethod.Name == "Unpause" ? 2 : 1), "projectile body query");
            Guards.Count(modifiers, __originalMethod.Name == "Pause" ? 0 : 1, "projectile settings query");
            return code;
        }

        private static Rigidbody2D Body(Projectile projectile)
        {
            if (!Plugin.ProjectileComponents.Active) return projectile.GetComponent<Rigidbody2D>();
            try
            {
                Entry entry = Entries.GetOrCreateValue(projectile);
                if (entry.Body == null) entry.Body = projectile.GetComponent<Rigidbody2D>();
                return entry.Body;
            }
            catch (Exception e) { Plugin.ProjectileComponents.Fail(e); return projectile.GetComponent<Rigidbody2D>(); }
        }

        private static Modifiers Settings(Projectile projectile)
        {
            if (!Plugin.ProjectileComponents.Active) return Modifiers.GetInstance();
            try
            {
                Entry entry = Entries.GetOrCreateValue(projectile);
                if (entry.Settings == null) entry.Settings = Modifiers.GetInstance();
                return entry.Settings;
            }
            catch (Exception e) { Plugin.ProjectileComponents.Fail(e); return Modifiers.GetInstance(); }
        }

        internal static void InstallShotColliders()
        {
            Plugin.ShotColliders.Patch(Reflect.DeclaredMethod(typeof(ProjectileLauncher), "ShootProjectile"), typeof(ProjectileHooks), null, null, "ShotArrays", null);
        }

        internal static IEnumerable<CodeInstruction> ShotArrays(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (method == null || !method.IsGenericMethod || method.Name != "GetComponentsInChildren" || method.GetGenericArguments()[0] != typeof(Collider2D) || method.GetParameters().Length != 0) continue;
                Type owner = method.DeclaringType;
                if (owner != typeof(Component) && owner != typeof(GameObject)) throw new InvalidOperationException("Unexpected collider query owner");
                instruction.opcode = OpCodes.Call;
                instruction.operand = Reflect.Method(typeof(CacheHooks), "ChildColliders", new Type[] { owner }); count++;
            }
            Guards.Count(count, 2, "shot collider queries");
            return code;
        }

        internal static void InstallIgnorePairs()
        {
            Plugin.IgnorePairs.Patch(Reflect.DeclaredMethod(typeof(ProjectileLauncher), "ShootProjectile"), typeof(ProjectileHooks), null, null, "IgnoreCollision", null);
        }

        internal static IEnumerable<CodeInstruction> IgnoreCollision(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            // ShotArrays may already have transformed the same method under this plugin.
            List<CodeInstruction> code = OwnShotStream(input, __originalMethod);
            int count = 0;
            foreach (CodeInstruction instruction in code)
                if (Guards.Calls(instruction, typeof(Physics2D), "IgnoreCollision"))
                {
                    instruction.opcode = OpCodes.Call; instruction.operand = Reflect.Method(typeof(ProjectileHooks), "IgnorePair"); count++;
                }
            Guards.Count(count, 1, "IgnoreCollision");
            return code;
        }

        private static List<CodeInstruction> OwnShotStream(IEnumerable<CodeInstruction> input, MethodBase original)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(input);
            // Normalize only our exact two audited helper calls for full-stream verification.
            List<CodeInstruction> normalized = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in code)
            {
                CodeInstruction copy = new CodeInstruction(instruction);
                MethodInfo method = instruction.operand as MethodInfo;
                if (method != null && method.DeclaringType == typeof(CacheHooks) && method.Name == "ChildColliders")
                {
                    Type owner = method.GetParameters()[0].ParameterType;
                    MethodInfo vanilla = null;
                    foreach (MethodInfo candidate in owner.GetMethods())
                        if (candidate.Name == "GetComponentsInChildren" && candidate.IsGenericMethodDefinition && candidate.GetParameters().Length == 0)
                            vanilla = candidate.MakeGenericMethod(typeof(Collider2D));
                    copy.opcode = OpCodes.Callvirt; copy.operand = vanilla;
                }
                normalized.Add(copy);
            }
            Guards.Stream(normalized, original);
            return code;
        }

        private static void IgnorePair(Collider2D first, Collider2D second, bool ignore)
        {
            if (Plugin.IgnorePairs.Active)
            {
                try { if (Physics2D.GetIgnoreCollision(first, second) == ignore) return; }
                catch (Exception e) { Plugin.IgnorePairs.Fail(e); }
            }
            Physics2D.IgnoreCollision(first, second, ignore);
        }
    }
}
