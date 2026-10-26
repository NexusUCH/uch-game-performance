using System;
using System.Collections.Generic;
using UnityEngine;

namespace UCHGamePerformance
{
    // 2.5.0: on a full level most block animators are off screen (the play camera follows the players), yet Unity
    // evaluates every enabled Animator every frame (Eternity, 8VKK-7G4K: 760 animators, 13 visible; all of them
    // switched off = 110 -> 200 FPS). Animators that only draw something get Unity's own culling mode
    // "CullCompletely": they are not evaluated while none of their renderers is visible and resume when one is.
    // Only animators that cannot touch gameplay qualify, all three conditions checked live on the block's own objects:
    //   1. the controller is on a fixed list reviewed in the game code (its owner script only writes parameters or
    //      speed and never reads animator state: glue attach indicators, boxing-glove buttons, spring and black-hole
    //      sprites, hamster wheels, the goal flag);
    //   2. no Collider2D and no Rigidbody2D anywhere under the animator (the animation moves no fixture);
    //   3. no animation event in any clip (nothing is called from the animation).
    // Everything that drives gameplay (shooters fire from animation events, gloves, doors, crumble blocks, saws,
    // spikes, teleporters, cannons) keeps the vanilla mode. Measured on Eternity: animator phase 2.0 -> 1.35 ms per
    // frame, 95 -> 108 FPS. Visible difference: none while on screen; an off-screen looping sprite resumes at the
    // frame it was culled at instead of having advanced invisibly.
    internal static class AnimatorCullHooks
    {
        internal static readonly Feature Cull = new Feature("Graphics/CullInvisibleAnimators");
        private static readonly HashSet<string> Controllers = new HashSet<string> {
            "Indicators_Indicators.000", "BoxingGlove-SideButton", "BoxingGlove-BottomButton", "BoxingGlove-TopButton",
            "spring", "blackhole-Blackhole_Swhirl.000", "HorizontalHover", "Flag-Flag.000" };
        private static readonly List<Animator> Scratch = new List<Animator>();
        private static readonly List<Animator> Changed = new List<Animator>();
        private static readonly List<AnimatorCullingMode> Previous = new List<AnimatorCullingMode>();
        private static long culled, seen;

        internal static void Install()
        {
            Cull.Restore = Restore;
            Cull.Patch(Reflect.DeclaredMethod(typeof(Placeable), "Awake"), typeof(AnimatorCullHooks), null, "AfterPlaceableAwake", null, null);
        }

        // Every block's animators are looked at once when the block object awakes (prefab instance or level load).
        private static void AfterPlaceableAwake(Placeable __instance)
        {
            if (!Cull.Active) return;
            try
            {
                __instance.GetComponentsInChildren(true, Scratch);
                for (int i = 0; i < Scratch.Count; i++)
                {
                    Animator a = Scratch[i];
                    seen++;
                    if (a == null || a.cullingMode == AnimatorCullingMode.CullCompletely || !VisualOnly(a)) continue;
                    Changed.Add(a); Previous.Add(a.cullingMode);
                    a.cullingMode = AnimatorCullingMode.CullCompletely;
                    culled++;
                }
                Scratch.Clear();
                if ((Changed.Count & 1023) == 0) Prune();
            }
            catch (Exception e) { Cull.Fail(e); }
        }

        private static bool VisualOnly(Animator a)
        {
            RuntimeAnimatorController controller = a.runtimeAnimatorController;
            if (controller == null || !Controllers.Contains(controller.name)) return false;
            if (a.GetComponentInChildren<Collider2D>(true) != null || a.GetComponentInChildren<Rigidbody2D>(true) != null) return false;
            AnimationClip[] clips = controller.animationClips;
            for (int i = 0; i < clips.Length; i++) if (clips[i] != null && clips[i].events.Length != 0) return false;
            return true;
        }

        // Destroyed blocks leave dead references behind; drop them now and then (never a scene scan).
        private static void Prune()
        {
            int write = 0;
            for (int i = 0; i < Changed.Count; i++)
            {
                if (Changed[i] == null) continue;
                Changed[write] = Changed[i]; Previous[write] = Previous[i]; write++;
            }
            Changed.RemoveRange(write, Changed.Count - write);
            Previous.RemoveRange(write, Previous.Count - write);
        }

        private static void Restore()
        {
            for (int i = 0; i < Changed.Count; i++) if (Changed[i] != null) Changed[i].cullingMode = Previous[i];
            Changed.Clear(); Previous.Clear();
        }

        internal static string Status() { return "culled=" + culled + " seen=" + seen + " tracked=" + Changed.Count; }
    }
}
