using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using GameEvent;
using HarmonyLib;

namespace UCHGamePerformance
{
    // 2.3.1: every block registers for five events in Placeable.ChangeListener (every pooled projectile for five more in
    // Projectile.ChangeListener), and GameEventManager.ChangeListener<T> first runs List.Contains over that event's whole
    // listener list using Unity's object equality. The lists hold one entry per block, so a level start costs
    // O(blocks^2): ~6 s for 10,000 blocks (measured). Those two call sites now check membership with a hash set that
    // mirrors each list. The list stays authoritative and keeps its order: adds append, removes take out the same single
    // entry. Any change made elsewhere (other callers, SendEvent's dead-listener cleanup) bumps the list's private
    // _version, which rebuilds the mirror before its next use; a list with duplicate entries is left to the original method.
    internal static class ListenerHooks
    {
        internal static readonly Feature Membership = new Feature("Loading/ListenerMembershipSet");
        private static readonly FieldInfo version = typeof(List<IGameEventListener>).GetField("_version", BindingFlags.NonPublic | BindingFlags.Instance);
        private sealed class Mirror { internal List<IGameEventListener> List; internal HashSet<IGameEventListener> Set; internal int Version; }
        private sealed class ByReference : IEqualityComparer<IGameEventListener>
        {
            public bool Equals(IGameEventListener a, IGameEventListener b) { return ReferenceEquals(a, b); }
            public int GetHashCode(IGameEventListener value) { return RuntimeHelpers.GetHashCode(value); }
        }
        private static readonly ByReference comparer = new ByReference();
        private static readonly Dictionary<Type, Mirror> mirrors = new Dictionary<Type, Mirror>();
        private static long fast, rebuilds, fallbacks;

        internal static void Install()
        {
            if (version == null || version.FieldType != typeof(int)) throw new MissingFieldException("List<T>._version");
            Membership.Restore = delegate { mirrors.Clear(); };
            Membership.Patch(Reflect.DeclaredMethod(typeof(Placeable), "ChangeListener"), typeof(ListenerHooks), null, null, "UseMirror", null);
            Membership.Patch(Reflect.DeclaredMethod(typeof(Projectile), "ChangeListener"), typeof(ListenerHooks), null, null, "UseMirror", null);
            // 2.4.0: every placement checker (CheckColliding, several per block) registers for two events as well.
            Membership.Patch(Reflect.DeclaredMethod(typeof(CheckColliding), "ChangeListener"), typeof(ListenerHooks), null, null, "UseMirror", null);
        }

        internal static IEnumerable<CodeInstruction> UseMirror(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            List<CodeInstruction> code = Guards.Stream(input, __originalMethod);
            MethodInfo replacement = Reflect.DeclaredMethod(typeof(ListenerHooks), "ChangeListener");
            int count = 0;
            foreach (CodeInstruction instruction in code)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (instruction.opcode != OpCodes.Call || method == null || !method.IsGenericMethod || method.DeclaringType != typeof(GameEventManager) || method.Name != "ChangeListener") continue;
                instruction.operand = replacement.MakeGenericMethod(method.GetGenericArguments());
                count++;
            }
            Guards.Count(count, __originalMethod.DeclaringType == typeof(CheckColliding) ? 2 : 5, "event listener registration");
            return code;
        }

        // Same effect as GameEventManager.ChangeListener<T>(listener, adding).
        private static void ChangeListener<T>(IGameEventListener listener, bool adding) where T : global::GameEvent.GameEvent
        {
            if (Membership.Active)
            {
                try { if (Apply(typeof(T), listener, adding)) return; }
                catch (Exception e) { Membership.Fail(e); }
            }
            fallbacks++;
            GameEventManager.ChangeListener<T>(listener, adding);
        }

        // false = let the original method handle this call (no dictionary/list yet, or duplicate entries).
        private static bool Apply(Type type, IGameEventListener listener, bool adding)
        {
            Dictionary<Type, List<IGameEventListener>> all = GameEventManager.listeners;
            List<IGameEventListener> list;
            if (all == null || listener == null || !all.TryGetValue(type, out list) || list == null) return false;
            Mirror mirror;
            if (!mirrors.TryGetValue(type, out mirror)) { mirror = new Mirror(); mirrors[type] = mirror; }
            if (!ReferenceEquals(mirror.List, list) || mirror.Set == null || mirror.Version != (int)version.GetValue(list) || mirror.Set.Count != list.Count)
            {
                mirror.List = list;
                mirror.Set = new HashSet<IGameEventListener>(list, comparer);
                mirror.Version = (int)version.GetValue(list);
                rebuilds++;
                if (mirror.Set.Count != list.Count) { mirror.Set = null; return false; }
            }
            fast++;
            if (adding)
            {
                if (mirror.Set.Add(listener)) list.Add(listener);
                mirror.Version = (int)version.GetValue(list);
                return true;
            }
            if (!mirror.Set.Remove(listener)) return true;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], listener)) { list.RemoveAt(i); mirror.Version = (int)version.GetValue(list); return true; }
            mirror.Set = null;
            return true;
        }

        internal static string Status() { return "fast=" + fast + " rebuilds=" + rebuilds + " fallbacks=" + fallbacks; }
    }
}
