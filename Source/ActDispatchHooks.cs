using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.3.0: one central runner per ActMode replaces the per-block act-loop coroutines started in
    // ActiveBlock.OnEnable. Each block's first resume still happens natively (a one-shot handoff
    // coroutine on the block itself, at the exact vanilla queue slot); from its second resume on the
    // runner calls Act in the same relative order (vanilla steady-state order = first-resume order).
    internal static class ActDispatchHooks
    {
        internal static readonly Feature Dispatch = new Feature("CPU/CentralBlockLoops");
        internal static MonoBehaviour Host;
        private static readonly MethodInfo BaseAct = Reflect.DeclaredMethod(typeof(ActiveBlock), "Act");
        private static readonly WaitForFixedUpdate FixedWait = new WaitForFixedUpdate();
        private static readonly Dictionary<Type, bool> EmptyTypes = new Dictionary<Type, bool>();
        private static readonly Dictionary<ActiveBlock, Entry> Current = new Dictionary<ActiveBlock, Entry>();
        private static readonly List<Entry> FixedList = new List<Entry>();
        private static readonly List<Entry> UpdateList = new List<Entry>();
        private static Action<ActiveBlock, float> act;
        private static Func<ActiveBlock, IEnumerator> vanillaFixed, vanillaUpdate;
        private static int replacedTwice;

        private sealed class Entry
        {
            internal ActiveBlock Block;
            internal GameObject Owner;
            internal bool FixedMode, Dead, Listed;
            internal double AddedAt;
        }

        internal static void Install()
        {
            if (Host == null) throw new InvalidOperationException("No coroutine host");
            Guards.Method(BaseAct);
            act = (Action<ActiveBlock, float>)Emit("ActDispatchAct", typeof(void), new Type[] { typeof(ActiveBlock), typeof(float) }, BaseAct)
                .CreateDelegate(typeof(Action<ActiveBlock, float>));
            vanillaFixed = Iterator("actOnFixed");
            vanillaUpdate = Iterator("actOnUpdate");
            Dispatch.Restore = Restore;
            Dispatch.Patch(Reflect.DeclaredMethod(typeof(ActiveBlock), "OnEnable"), typeof(ActDispatchHooks), "BeforeBlockEnable", null, null, null);
            Host.StartCoroutine(FixedRunner());
            Host.StartCoroutine(UpdateRunner());
        }

        private static DynamicMethod Emit(string name, Type result, Type[] parameters, MethodInfo target)
        {
            DynamicMethod method = new DynamicMethod(name, result, parameters, typeof(ActDispatchHooks), true);
            ILGenerator il = method.GetILGenerator();
            for (int i = 0; i < parameters.Length; i++) il.Emit(OpCodes.Ldarg, i);
            il.Emit(target.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            return method;
        }

        private static Func<ActiveBlock, IEnumerator> Iterator(string name)
        {
            MethodInfo method = Reflect.DeclaredMethod(typeof(ActiveBlock), name);
            if (method == null || method.ReturnType != typeof(IEnumerator) || method.GetParameters().Length != 0) throw new InvalidOperationException("ActiveBlock." + name + " changed");
            return (Func<ActiveBlock, IEnumerator>)Emit("ActDispatch_" + name, typeof(IEnumerator), new Type[] { typeof(ActiveBlock) }, method)
                .CreateDelegate(typeof(Func<ActiveBlock, IEnumerator>));
        }

        private static bool Empty(Type type)
        {
            bool empty;
            if (!EmptyTypes.TryGetValue(type, out empty))
            {
                empty = BlockLoopPolicy.InheritsEmptyAct(type, typeof(ActiveBlock), BaseAct);
                EmptyTypes.Add(type, empty);
            }
            return empty;
        }

        private static bool BeforeBlockEnable(ActiveBlock __instance)
        {
            if (!Dispatch.Active) return true;
            try
            {
                if (__instance.ActUpdateMode == ActiveBlock.ActMode.NEVER) return true;
                // An inherited empty Act makes the vanilla loop observably a no-op (same rule as CPU/SkipEmptyBlockLoops).
                if (Empty(__instance.GetType())) return false;
                Entry previous;
                if (Current.TryGetValue(__instance, out previous) && !previous.Dead)
                {
                    // Vanilla kills the old loop only on GameObject deactivation; a pure component re-enable would
                    // run two loops. Audited code never does that; count it so testing would reveal it.
                    previous.Dead = true;
                    replacedTwice++;
                    if (replacedTwice == 1) Feature.Log.LogWarning(Dispatch.Name + ": a block re-registered while its previous loop entry was live (" + __instance.name + ").");
                }
                Entry entry = new Entry();
                entry.Block = __instance;
                entry.Owner = __instance.gameObject;
                entry.FixedMode = __instance.ActUpdateMode == ActiveBlock.ActMode.FIXED;
                Current[__instance] = entry;
                // StartCoroutine runs the first iteration synchronously and queues the first resume exactly like vanilla.
                __instance.StartCoroutine(Handoff(entry));
                return false;
            }
            catch (Exception e) { Dispatch.Fail(e); return true; }
        }

        private static IEnumerator Handoff(Entry entry)
        {
            ActiveBlock block = entry.Block;
            // Iteration 1, identical to the vanilla loop head and body.
            if (entry.Dead || block.MarkedForDestruction) { entry.Dead = true; yield break; }
            if (block.Active && !block.Paused) act(block, entry.FixedMode ? Time.fixedDeltaTime : Time.deltaTime);
            if (entry.FixedMode) yield return FixedWait; else yield return null;
            // First native resume: run this step natively, then join the runner from the next step on.
            if (entry.Dead || !Dispatch.Active) yield break;
            if (block.MarkedForDestruction) { entry.Dead = true; yield break; }
            if (block.Active && !block.Paused) act(block, entry.FixedMode ? Time.fixedDeltaTime : Time.deltaTime);
            entry.AddedAt = Stamp(entry.FixedMode);
            entry.Listed = true;
            (entry.FixedMode ? FixedList : UpdateList).Add(entry);
        }

        private static double Stamp(bool fixedMode) { return fixedMode ? Time.fixedTimeAsDouble : (double)Time.frameCount; }

        private static IEnumerator FixedRunner()
        {
            while (true)
            {
                yield return FixedWait;
                if (Dispatch.Active) Pass(FixedList, true);
            }
        }

        private static IEnumerator UpdateRunner()
        {
            while (true)
            {
                yield return null;
                if (Dispatch.Active) Pass(UpdateList, false);
            }
        }

        private static void Pass(List<Entry> list, bool fixedMode)
        {
            double now = Stamp(fixedMode);
            float delta = fixedMode ? Time.fixedDeltaTime : Time.deltaTime;
            int count = list.Count, write = 0;
            for (int i = 0; i < count; i++)
            {
                Entry entry = list[i];
                if (!entry.Dead)
                {
                    ActiveBlock block = entry.Block;
                    // Vanilla: a destroyed/deactivated owner kills the coroutine; the loop head checks markedForDestruction.
                    if (block == null || !entry.Owner.activeInHierarchy || block.MarkedForDestruction) entry.Dead = true;
                    else if (entry.AddedAt != now && block.Active && !block.Paused)
                    {
                        try { act(block, delta); }
                        catch (Exception e) { Debug.LogException(e); entry.Dead = true; }
                    }
                }
                if (entry.Dead) { entry.Listed = false; if (entry.Block != null) Forget(entry); }
                else list[write++] = entry;
            }
            // Entries handed off while this pass ran (by an Act enabling another block) keep their order.
            for (int i = count; i < list.Count; i++) list[write++] = list[i];
            list.RemoveRange(write, list.Count - write);
        }

        private static void Forget(Entry entry)
        {
            Entry current;
            if (Current.TryGetValue(entry.Block, out current) && ReferenceEquals(current, entry)) Current.Remove(entry.Block);
        }

        internal static void SceneUnloaded()
        {
            // Destroyed blocks are dropped by the pass's null check; this only trims the identity map.
            List<ActiveBlock> stale = null;
            foreach (KeyValuePair<ActiveBlock, Entry> pair in Current)
                if (pair.Key == null || pair.Value.Dead) { if (stale == null) stale = new List<ActiveBlock>(); stale.Add(pair.Key); }
            if (stale != null) for (int i = 0; i < stale.Count; i++) Current.Remove(stale[i]);
        }

        // On feature failure/shutdown hand every listed block back to a native vanilla loop, in list order.
        private static void Restore()
        {
            List<Entry> all = new List<Entry>(FixedList);
            all.AddRange(UpdateList);
            FixedList.Clear(); UpdateList.Clear(); Current.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                Entry entry = all[i];
                ActiveBlock block = entry.Block;
                if (entry.Dead || block == null || !block.gameObject.activeInHierarchy || block.MarkedForDestruction) continue;
                entry.Dead = true;
                try { block.StartCoroutine(entry.FixedMode ? vanillaFixed(block) : vanillaUpdate(block)); }
                catch (Exception e) { Feature.Log.LogWarning(Dispatch.Name + " restore: " + e.Message); }
            }
        }

        internal static string Status() { return "fixed=" + FixedList.Count + " update=" + UpdateList.Count + " replacedTwice=" + replacedTwice; }
    }
}
