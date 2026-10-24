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
    internal static class PlaceableTickHooks
    {
        internal static readonly Feature Tick = new Feature("CPU/CentralPlaceableTick");
        private static readonly List<Placeable> Blocks = new List<Placeable>();
        private static readonly Dictionary<Placeable, int> Index = new Dictionary<Placeable, int>();
        private static Action<Placeable> fixedUpdate;
        private static Action<Placeable> resetCache;
        private static GameObject host;

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

        // Unity has already called Start for this instance; disabling now cannot postpone it.
        private static void AfterStart(Placeable __instance)
        {
            if (!Tick.Active) return;
            try
            {
                if (__instance.GetType() != typeof(Placeable) || !__instance.enabled || Index.ContainsKey(__instance)) return;
                Index.Add(__instance, Blocks.Count);
                Blocks.Add(__instance);
                __instance.enabled = false;
            }
            catch (Exception e) { Tick.Fail(e); }
        }

        private static void AfterDestroy(Placeable __instance)
        {
            int at;
            if (!Index.TryGetValue(__instance, out at)) return;
            // Order among Placeable FixedUpdates is unspecified natively and each touches only its own state.
            int last = Blocks.Count - 1;
            Placeable moved = Blocks[last];
            Blocks[at] = moved; Index[moved] = at;
            Blocks.RemoveAt(last); Index.Remove(__instance);
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
            }
            catch (Exception e) { Tick.Fail(e); }
        }

        private static void Restore()
        {
            for (int i = 0; i < Blocks.Count; i++) if (Blocks[i] != null) Blocks[i].enabled = true;
            Blocks.Clear(); Index.Clear();
            if (host != null) host.SetActive(false);
        }

        internal static string Status() { return "ticked=" + Blocks.Count; }
    }

    [DefaultExecutionOrder(-1000)]
    internal sealed class PlaceableTicker : MonoBehaviour
    {
        private void FixedUpdate() { PlaceableTickHooks.Run(); }
    }
}
