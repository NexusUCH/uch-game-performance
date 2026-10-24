using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.3.0: AkGameObj.Update re-reads position/forward/up and compares them with the last values it sent to Wwise
    // every frame; for an emitter that did not move it changes nothing. This skips that Update while the emitter's
    // transform has not changed since its last full Update and its environment/portal lists are unchanged, which
    // are exactly the inputs of both branches. Subclasses (virtual GetPosition overrides) always stay vanilla.
    internal static class AudioHooks
    {
        internal static readonly Feature Emitters = new Feature("CPU/AudioEmitterIdleSkip");
        private static readonly Dictionary<AkGameObj, Transform> Seen = new Dictionary<AkGameObj, Transform>();
        private static Func<AkGameObj, bool> environmentDirty;

        internal static void Install()
        {
            FieldInfo env = Reflect.Field(typeof(AkGameObj), "m_envData");
            FieldInfo list = Reflect.Field(typeof(AkGameObjEnvironmentData), "hasEnvironmentListChanged");
            FieldInfo portals = Reflect.Field(typeof(AkGameObjEnvironmentData), "hasActivePortalListChanged");
            if (env == null || env.FieldType != typeof(AkGameObjEnvironmentData) || list == null || list.FieldType != typeof(bool) || portals == null || portals.FieldType != typeof(bool))
                throw new InvalidOperationException("AkGameObj environment fields changed");
            // env != null && (env.hasEnvironmentListChanged || env.hasActivePortalListChanged)
            DynamicMethod dirty = new DynamicMethod("AkEnvironmentDirty", typeof(bool), new Type[] { typeof(AkGameObj) }, typeof(AudioHooks), true);
            ILGenerator il = dirty.GetILGenerator();
            LocalBuilder data = il.DeclareLocal(typeof(AkGameObjEnvironmentData));
            Label no = il.DefineLabel(), yes = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, env); il.Emit(OpCodes.Stloc, data);
            il.Emit(OpCodes.Ldloc, data); il.Emit(OpCodes.Brfalse, no);
            il.Emit(OpCodes.Ldloc, data); il.Emit(OpCodes.Ldfld, list); il.Emit(OpCodes.Brtrue, yes);
            il.Emit(OpCodes.Ldloc, data); il.Emit(OpCodes.Ldfld, portals); il.Emit(OpCodes.Ret);
            il.MarkLabel(yes); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
            il.MarkLabel(no); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
            environmentDirty = (Func<AkGameObj, bool>)dirty.CreateDelegate(typeof(Func<AkGameObj, bool>));
            Emitters.Restore = delegate { Seen.Clear(); };
            Emitters.Patch(Reflect.DeclaredMethod(typeof(AkGameObj), "Update"), typeof(AudioHooks), "BeforeUpdate", "AfterUpdate", null, null);
        }

        private static bool BeforeUpdate(AkGameObj __instance, out Transform __state)
        {
            __state = null;
            if (!Emitters.Active) return true;
            try
            {
                if (__instance.GetType() != typeof(AkGameObj)) return true;
                Transform t;
                if (!Seen.TryGetValue(__instance, out t))
                {
                    t = __instance.transform;
                    Seen.Add(__instance, t);
                    __state = t;
                    return true;
                }
                if (t.hasChanged || environmentDirty(__instance)) { __state = t; return true; }
                return false;
            }
            catch (Exception e) { Emitters.Fail(e); return true; }
        }

        // The full Update just consumed the current transform; anything that moves it later sets the flag again.
        private static void AfterUpdate(Transform __state)
        {
            if (__state != null) __state.hasChanged = false;
        }

        internal static void SceneUnloaded()
        {
            List<AkGameObj> stale = null;
            foreach (KeyValuePair<AkGameObj, Transform> pair in Seen)
                if (pair.Key == null) { if (stale == null) stale = new List<AkGameObj>(); stale.Add(pair.Key); }
            if (stale != null) for (int i = 0; i < stale.Count; i++) Seen.Remove(stale[i]);
        }
    }
}
