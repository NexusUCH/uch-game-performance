using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.10.0: every blower's particle controller (UpblowerParticleControl.Update) flips Physics2D.queriesHitTriggers,
    // box-casts along the stream, looks up Placeable/CollisionTag per hit and writes the particle system's start
    // lifetime and start rotation every frame (lab 2026-10-30: 21 calls x 13.5 us on Eternity). All of it only shapes
    // the visible particle stream: the push on players and projectiles is BlowerPusher (trigger callbacks plus its own
    // FixedUpdate box cast), which reads nothing this controller writes; nothing else in the game reads the start
    // lifetime of this particle system. The controller keeps no state between calls (each call recomputes the stream
    // length and rotation from scratch, and restores queriesHitTriggers before it returns), so skipping it while the
    // whole stream (its emitter, start and end points) lies outside the camera view plus the 12-unit pose-cull margin
    // leaves nothing to catch up: the first call back in view sets the same values vanilla would. The vanilla early-out
    // (start/end missing or the parent block marked for destruction: lifetime 0) always runs. Without a valid gameplay
    // camera (see View) nothing is skipped.
    internal static class BlowerParticleHooks
    {
        internal static readonly Feature Offscreen = new Feature("Graphics/OffscreenBlowerParticles");
        internal const float Margin = AnimatorPoseCullHooks.Margin;

        private sealed class Entry
        {
            public Entry() { }
            internal Transform Self;
        }

        private static ConditionalWeakTable<UpblowerParticleControl, Entry> table = new ConditionalWeakTable<UpblowerParticleControl, Entry>();
        private static long skipped, ran, tracked;

        internal static void Install()
        {
            MethodInfo update = Reflect.DeclaredMethod(typeof(UpblowerParticleControl), "Update");
            if (update == null || update.ReturnType != typeof(void) || update.GetParameters().Length != 0) throw new InvalidOperationException("UpblowerParticleControl.Update changed");
            FieldInfo parent = Reflect.Field(typeof(UpblowerParticleControl), "parentPiece");
            if (parent == null || parent.FieldType != typeof(Placeable) || parent.IsStatic) throw new InvalidOperationException("UpblowerParticleControl.parentPiece changed");
            Offscreen.Restore = Restore;
            Offscreen.Patch(update, typeof(BlowerParticleHooks), "BeforeUpdate", null, null, null);
        }

        private static bool BeforeUpdate(UpblowerParticleControl __instance, Placeable ___parentPiece)
        {
            if (!Offscreen.Active) return true;
            try
            {
                Transform start = __instance.start, end = __instance.end;
                // Vanilla's early-out (lifetime 0) always runs.
                if (start == null || end == null || (___parentPiece != null && ___parentPiece.MarkedForDestruction)) { ran++; return true; }
                if (!View.Refresh()) { ran++; return true; }
                Entry entry;
                if (!table.TryGetValue(__instance, out entry))
                {
                    entry = table.GetOrCreateValue(__instance);
                    entry.Self = __instance.transform;
                    tracked++;
                }
                Vector3 a = start.position, b = end.position, c = entry.Self.position;
                float x0 = Math.Min(a.x, Math.Min(b.x, c.x)), x1 = Math.Max(a.x, Math.Max(b.x, c.x));
                float y0 = Math.Min(a.y, Math.Min(b.y, c.y)), y1 = Math.Max(a.y, Math.Max(b.y, c.y));
                if (View.Overlaps(x0, y0, x1, y1, Margin)) { ran++; return true; }
                skipped++;
                return false;
            }
            catch (Exception e) { Offscreen.Fail(e); return true; }
        }

        private static void Restore()
        {
            table = new ConditionalWeakTable<UpblowerParticleControl, Entry>();
        }

        internal static string Status() { return "skipped=" + skipped + " ran=" + ran + " tracked=" + tracked; }
    }
}
