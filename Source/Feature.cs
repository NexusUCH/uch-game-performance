using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace UCHGamePerformance
{
    // A distinct Harmony owner makes installation/rollback independent for every feature.
    internal sealed class Feature
    {
        internal static ManualLogSource Log;
        internal static readonly List<Feature> All = new List<Feature>();
        internal readonly string Name;
        internal readonly Harmony Harmony;
        internal bool Active;
        internal Action Restore;
        private bool failed;

        internal Feature(string name)
        {
            Name = name;
            Harmony = new Harmony(Plugin.Guid + "." + name.Replace('/', '.'));
            All.Add(this);
        }

        internal void Start(bool requested, Action install, string unavailable)
        {
            string reason = null;
            if (!requested) reason = "config disabled";
            else if (!Guards.KnownBuild) reason = "Assembly-CSharp/firstpass MVID mismatch";
            else if (unavailable != null) reason = unavailable;
            else
            {
                try { Active = true; install(); }
                catch (Exception e)
                {
                    Active = false;
                    reason = e.GetType().Name + ": " + e.Message;
                    try { Harmony.UnpatchSelf(); } catch (Exception cleanup) { Log.LogWarning(Name + " rollback: " + cleanup.Message); }
                    TryRestore();
                }
            }
            Log.LogInfo(Name + " requested=" + requested + " / applied=" + Active + " / bypassed(" + (reason ?? "none") + ")");
        }

        internal void Patch(MethodBase target, Type hooks, string prefix, string postfix, string transpiler, string finalizer)
        {
            Guards.Method(target);
            Harmony.Patch(target, Hook(hooks, prefix), Hook(hooks, postfix), Hook(hooks, transpiler), Hook(hooks, finalizer), null);
        }

        private static HarmonyMethod Hook(Type type, string name)
        {
            if (name == null) return null;
            MethodInfo method = Reflect.DeclaredMethod(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return new HarmonyMethod(method);
        }

        internal void Fail(Exception e)
        {
            Active = false;
            if (failed) return;
            failed = true;
            // Callbacks use Active to fall back; do not rebuild a live detour from its own callback.
            Log.LogWarning(Name + " disabled only this feature after " + e.GetType().Name + ": " + e.Message);
            TryRestore();
        }

        private void TryRestore()
        {
            if (Restore == null) return;
            try { Restore(); }
            catch (Exception e) { Log.LogWarning(Name + " restoration failed: " + e.Message); }
        }

        internal void Stop()
        {
            Active = false;
            try { Harmony.UnpatchSelf(); }
            catch (Exception e) { Log.LogWarning(Name + " unpatch failed: " + e.Message); }
            TryRestore();
        }
    }
}
