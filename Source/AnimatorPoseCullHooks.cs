using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.6.0: after Graphics/CullInvisibleAnimators (2.5.0) the animators left on a full level are the ones that drive
    // gameplay (Eternity, 8VKK-7G4K: ~300 enabled, 5-13 of them on screen). Unity still evaluates every one of them every
    // frame and writes their poses into transforms the physics step then has to re-sync. Unity's culling mode
    // "CullUpdateTransforms" keeps the state machine, the parameters and the animation events running while no renderer
    // of the animator is visible and only skips the pose/property writes. Verified live (lab profile, 2026-10-26): an
    // off-screen boxing glove culled this way finished its punch in 200 frames with EnableHazard/DisableHazard/
    // OnPunchAnimFinished firing; an off-screen spike's state time advanced while its hazard collider stayed unwritten.
    //
    // Two reviewed groups, each its own option:
    //  * Graphics/CullOffscreenAnimatorPoses: controllers whose clips never write a collider pose or state (measured by
    //    sampling every Collider2D below the animator while the clips played): arrow shooters (the arrow spawns from a
    //    fixed offset of the block root, the clip only draws the bow; ShootProjectile stays an event), animal cannons,
    //    teleporters, beehives, blowers, fans, crumble blocks (collider switches and the fall are script code), treadmills
    //    (the belt parts are positioned by script), punching plants. Off screen, only pixels nobody sees are skipped.
    //  * Graphics/CullOffscreenMoverAnimators: controllers whose clips do move or switch colliders (boxing glove arm, saw
    //    blade, spikes, flipping block, swinging axe, lighter flame, both door types). Off screen the collider keeps the
    //    pose it had when it left the view and snaps to the live pose the moment the block is back in view. Players are
    //    always on screen; only off-screen projectiles can meet a frozen collider. Owner's decision (2026-10-26).
    //
    // Unity decides "visible" from the animator's renderers, which also fails for a block in view whose sprites are
    // switched off or blank, so this hook does not rely on it alone: a view loop keeps every tracked animator inside the
    // camera rectangle (plus a 12-unit margin) in its original mode and culls only animators outside it. The gameplay
    // camera is found as the enabled camera whose world rectangle at the block plane contains every living player (UCH's
    // Camera.main is the UI camera); if no camera qualifies, nothing is culled that frame.
    internal static class AnimatorPoseCullHooks
    {
        internal static readonly Feature Poses = new Feature("Graphics/CullOffscreenAnimatorPoses");
        internal static readonly Feature Movers = new Feature("Graphics/CullOffscreenMoverAnimators");
        internal static MonoBehaviour Host;
        internal const float Margin = 12f;
        private const int PerFrame = 400;
        // Reviewed controllers (Eternity census + game code, 2026-10-26). Names are the runtime controller names.
        private static readonly HashSet<string> EventOnly = new HashSet<string> {
            "ArrowShooter", "AnimalCannonController", "teleporter_W83_143_0", "BeehiveController", "UpBlowerAnimator",
            "spinningFan", "CrumblingBlockAnimator", "TreadMill.000", "PunchingPlant_Controller" };
        private static readonly HashSet<string> MoverList = new HashSet<string> {
            "BoxingGlove-Punch", "LinearSaw", "PressureTriggerAnimator", "FlippingBlock", "SwingingAxe", "Lighter",
            "autoDoor1x4", "Oneway Door-OnewayDoor1x4.000" };

        private sealed class Entry
        {
            internal Animator Animator;
            internal Transform Transform;
            internal AnimatorCullingMode Original;
            internal bool Mover, Culled;
        }

        private static readonly List<Entry> Tracked = new List<Entry>();
        private static readonly List<Animator> Scratch = new List<Animator>();
        private static int cursor, culledNow;
        private static long seen, adopted, culls, unculls;
        private static bool loopStarted;

        internal static void Install()
        {
            if (Host == null) throw new InvalidOperationException("No coroutine host");
            Poses.Restore = delegate { Restore(false); };
            Poses.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Awake"), typeof(AnimatorPoseCullHooks), null, "AfterPlaceableAwake", null, null);
            if (!loopStarted) { loopStarted = true; Host.StartCoroutine(ViewLoop()); }
        }

        // Rides on Graphics/CullOffscreenAnimatorPoses and patches nothing itself.
        internal static void InstallMovers()
        {
            if (!Poses.Active) throw new NotSupportedException("Graphics/CullOffscreenAnimatorPoses is off");
            Movers.Restore = delegate { Restore(true); };
        }

        // Every block's animators are classified once when the block object awakes (prefab instance or level load).
        private static void AfterPlaceableAwake(Placeable __instance)
        {
            if (!Poses.Active) return;
            try
            {
                __instance.GetComponentsInChildren(true, Scratch);
                for (int i = 0; i < Scratch.Count; i++)
                {
                    Animator animator = Scratch[i];
                    seen++;
                    if (animator == null || animator.cullingMode != AnimatorCullingMode.AlwaysAnimate) continue;
                    RuntimeAnimatorController controller = animator.runtimeAnimatorController;
                    if (controller == null) continue;
                    bool mover;
                    if (EventOnly.Contains(controller.name)) mover = false;
                    else if (Movers.Active && MoverList.Contains(controller.name)) mover = true;
                    else continue;
                    // Unity never reports an animator without renderers as visible; such an animator would stay culled.
                    if (animator.GetComponentInChildren<Renderer>(true) == null) continue;
                    Entry entry = new Entry();
                    entry.Animator = animator;
                    entry.Transform = animator.transform;
                    entry.Original = animator.cullingMode;
                    entry.Mover = mover;
                    Tracked.Add(entry);
                    adopted++;
                }
                Scratch.Clear();
            }
            catch (Exception e) { Poses.Fail(e); }
        }

        // Resumes after every Update, before Unity evaluates the animators for the frame.
        private static IEnumerator ViewLoop()
        {
            while (true)
            {
                yield return null;
                if (!Poses.Active || Tracked.Count == 0) continue;
                try { Step(); }
                catch (Exception e) { Poses.Fail(e); }
            }
        }

        private static void Step()
        {
            bool valid = View.Refresh();
            // With a valid view, a slice of the list per frame; without one, every animator goes back to its own mode now.
            int budget = valid ? Math.Min(PerFrame, Tracked.Count) : Tracked.Count;
            for (int k = 0; k < budget && Tracked.Count != 0; k++)
            {
                if (cursor >= Tracked.Count) cursor = 0;
                Entry entry = Tracked[cursor];
                if (entry.Animator == null)
                {
                    // Destroyed block: drop the entry without a scene scan.
                    int last = Tracked.Count - 1;
                    Tracked[cursor] = Tracked[last];
                    Tracked.RemoveAt(last);
                    if (entry.Culled) culledNow--;
                    continue;
                }
                bool cull = valid && !View.Contains(entry.Transform.position, Margin);
                if (cull != entry.Culled)
                {
                    entry.Animator.cullingMode = cull ? AnimatorCullingMode.CullUpdateTransforms : entry.Original;
                    entry.Culled = cull;
                    if (cull) { culls++; culledNow++; } else { unculls++; culledNow--; }
                }
                cursor++;
            }
        }

        private static void Restore(bool moversOnly)
        {
            int write = 0;
            for (int i = 0; i < Tracked.Count; i++)
            {
                Entry entry = Tracked[i];
                if (moversOnly && !entry.Mover) { Tracked[write++] = entry; continue; }
                if (entry.Animator != null && entry.Culled) entry.Animator.cullingMode = entry.Original;
                if (entry.Culled) culledNow--;
                entry.Culled = false;
            }
            Tracked.RemoveRange(write, Tracked.Count - write);
            cursor = 0;
        }

        internal static string Status()
        {
            int movers = 0;
            for (int i = 0; i < Tracked.Count; i++) if (Tracked[i].Mover) movers++;
            return "tracked=" + Tracked.Count + " movers=" + movers + " culledNow=" + culledNow + " adopted=" + adopted + " seen=" + seen +
                " culls=" + culls + " unculls=" + unculls + " view[" + View.Status() + "]";
        }
    }

    // The gameplay camera's world rectangle at the block plane (z = 0), computed at most once per frame and shared by
    // the animator view loop and the far-ferris-wheel option. A camera qualifies only while every living, active
    // player stands inside its rectangle (with a small allowance for camera lag); UCH's Camera.main is the UI camera.
    internal static class View
    {
        private const float PlayerAllowance = 4f;
        private static Camera camera;
        private static int frame = -1;
        private static bool valid;
        private static float minX, maxX, minY, maxY;
        private static long rejects, searches, refreshes;

        internal static bool Refresh()
        {
            int now = Time.frameCount;
            if (now == frame) return valid;
            frame = now;
            refreshes++;
            valid = Rect(camera) && PlayersInside();
            if (!valid)
            {
                searches++;
                Camera[] all = Camera.allCameras;
                for (int i = 0; i < all.Length; i++)
                {
                    Camera candidate = all[i];
                    if (candidate == camera) continue;
                    if (Rect(candidate) && PlayersInside()) { camera = candidate; valid = true; break; }
                }
            }
            if (!valid) rejects++;
            return valid;
        }

        private static bool Rect(Camera c)
        {
            if (c == null || !c.enabled || c.targetTexture != null) return false;
            // Distance from the camera to the block plane; UCH's gameplay camera is a perspective camera looking along +z.
            float depth = Mathf.Abs(c.transform.position.z);
            if (depth < 0.5f) depth = 10f;
            Vector3 a = c.ViewportToWorldPoint(new Vector3(0f, 0f, depth));
            Vector3 b = c.ViewportToWorldPoint(new Vector3(1f, 1f, depth));
            minX = Mathf.Min(a.x, b.x); maxX = Mathf.Max(a.x, b.x);
            minY = Mathf.Min(a.y, b.y); maxY = Mathf.Max(a.y, b.y);
            float width = maxX - minX, height = maxY - minY;
            return width > 1f && height > 1f && width < 3000f && height < 3000f;   // NaN fails these too
        }

        private static bool PlayersInside()
        {
            List<Character> all = Character.AllCharacters;
            if (all == null) return false;
            int living = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Character character = all[i];
                if (character == null || character.Dead || !character.gameObject.activeInHierarchy) continue;
                living++;
                if (!Contains(character.transform.position, PlayerAllowance)) return false;
            }
            return living > 0;
        }

        internal static bool Contains(Vector3 p, float margin)
        {
            return p.x >= minX - margin && p.x <= maxX + margin && p.y >= minY - margin && p.y <= maxY + margin;
        }

        internal static string Status()
        {
            return "camera=" + (camera == null ? "none" : camera.name) + " valid=" + valid + " rect=" + minX.ToString("0.0") + "," + minY.ToString("0.0") +
                ".." + maxX.ToString("0.0") + "," + maxY.ToString("0.0") + " refreshes=" + refreshes + " rejects=" + rejects + " searches=" + searches;
        }
    }
}
