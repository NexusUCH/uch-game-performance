using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Scripting;

namespace UCHGamePerformance
{
    // 2.4.0: the level start (GameControl.SetupStart: restore all blocks, then the shooters' projectile pools in the
    // same frame) allocates a lot at once; four full garbage collections were measured inside that single frame.
    // Automatic collection is paused for that frame only and switched back on after the end of the next frame, when the
    // incremental collector catches up in small slices. Memory may peak a little higher during the load.
    internal static class GcHooks
    {
        internal static readonly Feature Pause = new Feature("Loading/NoGCDuringLevelStart");
        internal static MonoBehaviour Host;
        private static bool paused;
        private static long pauses;

        internal static void Install()
        {
            if (Host == null) throw new InvalidOperationException("no coroutine host");
            Pause.Restore = Resume;
            Pause.Patch(Reflect.DeclaredMethod(typeof(GameControl), "SetupStart"), typeof(GcHooks), "BeforeSetupStart", null, null, null);
        }

        private static void BeforeSetupStart()
        {
            if (!Pause.Active || paused) return;
            try
            {
                if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled) return;
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                paused = true; pauses++;
                Host.StartCoroutine(ResumeLater());
            }
            catch (Exception e) { Resume(); Pause.Fail(e); }
        }

        private static IEnumerator ResumeLater()
        {
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();
            Resume();
        }

        private static void Resume()
        {
            if (!paused) return;
            paused = false;
            try { GarbageCollector.GCMode = GarbageCollector.Mode.Enabled; }
            catch (Exception e) { Feature.Log.LogWarning("GC resume failed: " + e.Message); }
        }

        internal static string Status() { return "pauses=" + pauses + " pausedNow=" + paused; }
    }
}
