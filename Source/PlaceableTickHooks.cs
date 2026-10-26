using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.3.0: plain placed blocks (exact type Placeable) only have Placeable.FixedUpdate as a per-frame message, yet
    // Unity pays a native dispatch for each of them every physics step. After a block's Start has run, this disables
    // the component (Placeable has no OnEnable/OnDisable/Update; the game never reads Placeable.enabled; collision
    // messages, coroutines and OnDestroy are unaffected) and one early FixedUpdate runs the same logic for all of them:
    // unparented blocks only need the per-step CanPlace cache reset; attached blocks run the original method.
    // 2.7.0: CPU/CentralPartTick extends this to the exact types MultipiecePart, Ice, HoneyPiece (barbed wire, ice and
    // glue pieces: ~1,900 native FixedUpdate calls per step on a full wire level) and their invisible parent
    // MultipieceBlock. All four inherit ActiveBlock's empty Act (their act loop is a no-op that CPU/SkipEmptyBlockLoops
    // already drops) and declare no Unity message of their own except MultipieceBlock.Update, which the ticker now
    // calls once per frame for every registered parent (through the patched method, so CPU/MultipieceUpdateGate still
    // applies). Disabling the component changes nothing else: ActiveBlock.OnEnable only starts that empty loop, the
    // game never reads a block's enabled flag, coroutines, collision messages and OnDestroy keep working. Verified by
    // reflection at install: any extra message on these types turns the option off.
    internal static class PlaceableTickHooks
    {
        internal static readonly Feature Tick = new Feature("CPU/CentralPlaceableTick");
        internal static readonly Feature Parts = new Feature("CPU/CentralPartTick");
        private static readonly List<Placeable> Blocks = new List<Placeable>();
        private static readonly Dictionary<Placeable, int> Index = new Dictionary<Placeable, int>();
        private static readonly List<MultipieceBlock> Multi = new List<MultipieceBlock>();
        private static readonly Dictionary<MultipieceBlock, int> MultiIndex = new Dictionary<MultipieceBlock, int>();
        private static readonly string[] Messages = { "FixedUpdate", "Update", "LateUpdate", "OnEnable", "OnDisable" };
        private static Action<Placeable> fixedUpdate;
        private static Action<Placeable> resetCache;
        private static Action<MultipieceBlock> multiUpdate;
        private static GameObject host;
        private static long partTicks, multiUpdates;

        internal static void Install()
        {
            MethodInfo original = Reflect.DeclaredMethod(typeof(Placeable), "FixedUpdate");
            Guards.Method(original);
            FieldInfo flag = Reflect.Field(typeof(Placeable), "CanPlaceCheckThisFixedUpdate");
            if (flag == null || flag.FieldType != typeof(bool) || flag.DeclaringType != typeof(Placeable)) throw new InvalidOperationException("Placeable.CanPlaceCheckThisFixedUpdate changed");
            DynamicMethod call = new DynamicMethod("PlaceableTickFixedUpdate", typeof(void), new Type[] { typeof(Placeable) }, typeof(PlaceableTickHooks), true);
            ILGenerator il = call.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, original); il.Emit(OpCodes.Ret);
            fixedUpdate = (Action<Placeable>)call.CreateDelegate(typeof(Action<Placeable>));
            DynamicMethod clear = new DynamicMethod("PlaceableTickResetCache", typeof(void), new Type[] { typeof(Placeable) }, typeof(PlaceableTickHooks), true);
            il = clear.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, flag); il.Emit(OpCodes.Ret);
            resetCache = (Action<Placeable>)clear.CreateDelegate(typeof(Action<Placeable>));
            Tick.Restore = Restore;
            host = new GameObject("UCHGamePerformance.PlaceableTick");
            host.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<PlaceableTicker>();
            Tick.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Start"), typeof(PlaceableTickHooks), null, "AfterStart", null, null);
            Tick.Patch(Reflect.DeclaredMethod(typeof(Placeable), "OnDestroy"), typeof(PlaceableTickHooks), null, "AfterDestroy", null, null);
        }

        // 2.7.0: rides on CPU/CentralPlaceableTick. Every assumption about the four part types is checked here.
        internal static void InstallParts()
        {
            if (!Tick.Active) throw new NotSupportedException("CPU/CentralPlaceableTick is off");
            MethodInfo baseAct = Reflect.DeclaredMethod(typeof(ActiveBlock), "Act");
            Guards.Method(baseAct);
            foreach (Type type in new Type[] { typeof(MultipiecePart), typeof(Ice), typeof(HoneyPiece), typeof(MultipieceBlock) })
            {
                if (!BlockLoopPolicy.InheritsEmptyAct(type, typeof(ActiveBlock), baseAct)) throw new InvalidOperationException(type.Name + " overrides Act");
                for (Type t = type; t != typeof(ActiveBlock); t = t.BaseType)
                    foreach (string message in Messages)
                    {
                        MethodInfo m = t.GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                        if (m != null && !(t == typeof(MultipieceBlock) && message == "Update")) throw new InvalidOperationException(t.Name + " declares " + message);
                    }
            }
            foreach (string message in Messages)
                if (message != "OnEnable" && typeof(ActiveBlock).GetMethod(message, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) != null)
                    throw new InvalidOperationException("ActiveBlock declares " + message);
            MethodInfo update = Reflect.DeclaredMethod(typeof(MultipieceBlock), "Update");
            if (update == null || update.ReturnType != typeof(void) || update.GetParameters().Length != 0) throw new InvalidOperationException("MultipieceBlock.Update changed");
            DynamicMethod call = new DynamicMethod("MultipieceTickUpdate", typeof(void), new Type[] { typeof(MultipieceBlock) }, typeof(PlaceableTickHooks), true);
            ILGenerator il = call.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, update); il.Emit(OpCodes.Ret);
            multiUpdate = (Action<MultipieceBlock>)call.CreateDelegate(typeof(Action<MultipieceBlock>));
            Parts.Restore = RestoreParts;
            host.AddComponent<MultipieceTicker>();
        }

        private static bool PartType(Type type)
        {
            return type == typeof(MultipiecePart) || type == typeof(Ice) || type == typeof(HoneyPiece) || type == typeof(MultipieceBlock);
        }

        // Unity has already called Start for this instance; disabling now cannot postpone it.
        private static void AfterStart(Placeable __instance)
        {
            if (!Tick.Active) return;
            Type type = null;
            try
            {
                type = __instance.GetType();
                if (!__instance.enabled || Index.ContainsKey(__instance)) return;
                if (type != typeof(Placeable) && !(Parts.Active && PartType(type))) return;
                Index.Add(__instance, Blocks.Count);
                Blocks.Add(__instance);
                MultipieceBlock multi = __instance as MultipieceBlock;
                if (multi != null) { MultiIndex.Add(multi, Multi.Count); Multi.Add(multi); }
                __instance.enabled = false;
            }
            // 2.7.1: a failure on a part type switches off only CPU/CentralPartTick.
            catch (Exception e) { if (type != null && PartType(type)) Parts.Fail(e); else Tick.Fail(e); }
        }

        private static void AfterDestroy(Placeable __instance)
        {
            try
            {
                int at;
                if (Index.TryGetValue(__instance, out at))
                {
                    // Order among Placeable FixedUpdates is unspecified natively and each touches only its own state.
                    int last = Blocks.Count - 1;
                    Placeable moved = Blocks[last];
                    Blocks[at] = moved; Index[moved] = at;
                    Blocks.RemoveAt(last); Index.Remove(__instance);
                }
                MultipieceBlock multi = __instance as MultipieceBlock;
                if (multi != null && MultiIndex.TryGetValue(multi, out at))
                {
                    int last = Multi.Count - 1;
                    MultipieceBlock moved = Multi[last];
                    Multi[at] = moved; MultiIndex[moved] = at;
                    Multi.RemoveAt(last); MultiIndex.Remove(multi);
                }
            }
            catch (Exception e) { Tick.Fail(e); }   // 2.7.1: never let an exception escape Placeable.OnDestroy
        }

        internal static void Run()
        {
            if (!Tick.Active) return;
            try
            {
                for (int i = 0; i < Blocks.Count; i++)
                {
                    Placeable p = Blocks[i];
                    // Only attached blocks can take the relative-position repair branch, and that branch must not
                    // run for inactive objects (native FixedUpdate would not be called for them).
                    if (ReferenceEquals(p.ParentPiece, null)) resetCache(p);
                    else if (p.gameObject.activeInHierarchy) fixedUpdate(p);
                }
                partTicks++;
            }
            catch (Exception e) { Tick.Fail(e); }
        }

        // MultipieceBlock.Update for every registered parent block (native Update is only sent to active objects).
        internal static void RunUpdate()
        {
            if (!Parts.Active) return;
            try
            {
                for (int i = 0; i < Multi.Count; i++)
                {
                    MultipieceBlock m = Multi[i];
                    if (m != null && m.gameObject.activeInHierarchy) { multiUpdate(m); multiUpdates++; }
                }
            }
            catch (Exception e) { Parts.Fail(e); }
        }

        private static void Restore()
        {
            for (int i = 0; i < Blocks.Count; i++) if (Blocks[i] != null) Blocks[i].enabled = true;
            Blocks.Clear(); Index.Clear(); Multi.Clear(); MultiIndex.Clear();
            if (host != null) host.SetActive(false);
        }

        // Only the part types go back to native ticking; plain blocks stay with CPU/CentralPlaceableTick.
        private static void RestoreParts()
        {
            int write = 0;
            for (int i = 0; i < Blocks.Count; i++)
            {
                Placeable p = Blocks[i];
                if (ReferenceEquals(p, null)) continue;
                if (p == null) { Index.Remove(p); continue; }   // 2.7.1: destroyed instance leaves no stale index entry
                if (p.GetType() != typeof(Placeable)) { p.enabled = true; Index.Remove(p); continue; }
                Index[p] = write; Blocks[write++] = p;
            }
            Blocks.RemoveRange(write, Blocks.Count - write);
            Multi.Clear(); MultiIndex.Clear();
        }

        internal static string Status() { return "ticked=" + Blocks.Count + " multi=" + Multi.Count + " partTicks=" + partTicks + " multiUpdates=" + multiUpdates; }
    }

    [DefaultExecutionOrder(-1000)]
    internal sealed class PlaceableTicker : MonoBehaviour
    {
        private void FixedUpdate() { PlaceableTickHooks.Run(); }
    }

    // Late in the frame, after the cursors have updated their hover state for this frame.
    [DefaultExecutionOrder(1000)]
    internal sealed class MultipieceTicker : MonoBehaviour
    {
        private void Update() { PlaceableTickHooks.RunUpdate(); }
    }
}
