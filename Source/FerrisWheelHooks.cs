using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.6.0: a ferris wheel's per-step work (FerrisWheel.Act) rotates the spinner by speed * deltaTime and re-positions
    // its platform bodies, every physics step, also for wheels far outside the view. The result depends only on the
    // total time: while a wheel is outside the camera rectangle (margin 12 units plus the wheel's own radius, see View)
    // the step is skipped and its deltaTime is kept; the first step back in view runs once with the kept time added, so
    // the wheel stands exactly where the skipped steps would have put it (one rotation by the summed angle instead of
    // many small ones). 2.6.2: the wheel also runs normally while any player or any live projectile is within its reach
    // (View.NearInterest: the wheel's radius plus 1 unit, plus the point's own travel radius; the interest grid is
    // rebuilt every physics step), so nobody and nothing can meet a platform that has not caught up yet. The view
    // rectangle is read after the camera's own FixedUpdate, so a switch to "all cam" flushes in the same physics step.
    // Reset (phase changes, free-play mode switch) drops the kept time. When the option is off or has failed, the
    // prefix only flushes any kept time and runs vanilla.
    // Idea from a friend's FerrisCull plugin (2026-10-26), with the UI-camera trap and the per-step player search removed.
    internal static class FerrisWheelHooks
    {
        internal static readonly Feature Far = new Feature("CPU/FarFerrisWheels");
        internal const float Margin = 12f;

        private sealed class Entry
        {
            internal double Owed;
            internal float Radius;
            internal bool Ready;
        }

        private static readonly ConditionalWeakTable<FerrisWheel, Entry> Table = new ConditionalWeakTable<FerrisWheel, Entry>();
        private static long near, skipped, flushes;

        internal static void Install()
        {
            Far.Patch(Reflect.DeclaredMethod(typeof(FerrisWheel), "Act"), typeof(FerrisWheelHooks), "BeforeAct", null, null, null);
            Far.Patch(Reflect.DeclaredMethod(typeof(FerrisWheel), "Reset"), typeof(FerrisWheelHooks), null, "AfterReset", null, null);
        }

        private static bool BeforeAct(FerrisWheel __instance, ref float __0)
        {
            try
            {
                Entry entry;
                if (!Far.Active)
                {
                    if (Table.TryGetValue(__instance, out entry) && entry.Owed > 0) { __0 += (float)entry.Owed; entry.Owed = 0; }
                    return true;
                }
                Transform spinner = __instance.SpinnerNew;
                if (spinner == null) return true;
                entry = Table.GetOrCreateValue(__instance);
                if (!entry.Ready) { entry.Radius = Radius(__instance, spinner); entry.Ready = true; }
                Vector3 center = spinner.position;
                if (!View.Refresh() || View.Contains(center, Margin + entry.Radius) || View.NearInterest(center, entry.Radius + 1f))
                {
                    near++;
                    if (entry.Owed > 0) { __0 += (float)entry.Owed; entry.Owed = 0; flushes++; }
                    return true;
                }
                entry.Owed += __0;
                skipped++;
                return false;
            }
            catch (Exception e) { Far.Fail(e); return true; }
        }

        private static void AfterReset(FerrisWheel __instance)
        {
            try
            {
                Entry entry;
                if (Table.TryGetValue(__instance, out entry)) entry.Owed = 0;
            }
            catch (Exception e) { Far.Fail(e); }
        }

        private static float Radius(FerrisWheel wheel, Transform spinner)
        {
            float radius = 0f;
            Vector3 center = spinner.position;
            Transform[] targets = wheel.PositionTargets;
            if (targets != null)
                for (int i = 0; i < targets.Length; i++)
                    if (targets[i] != null) { float d = ((Vector2)(targets[i].position - center)).magnitude; if (d > radius) radius = d; }
            return radius + 2f;
        }

        internal static string Status() { return "near=" + near + " skipped=" + skipped + " flushes=" + flushes; }
    }
}
